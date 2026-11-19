using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace AgnesService;

public sealed record AuthUser(long Id, string Username, string Role, long ExpiresAt)
{
    /// <summary>TASK6 phase 2: POST /api/reading/ingest permission (none | web | agent), from the cached user state.</summary>
    public string ReadingIngest { get; init; } = "none";
    /// <summary>Agent channel: Muse id (A001 …) used as added_by; null = not mapped.</summary>
    public string? MuseId { get; init; }
}

public sealed record LoginResult(JsonObject Body, string RefreshPlain, int MaxAge);

/// <summary>
/// JWT + refresh-token auth against schema agnes_auth. Port of python-legacy/auth.py; behaviour
/// documented in DESIGN.md ("Authentication"). Access token validation itself is done by the
/// ASP.NET Core JwtBearer handler (configured in <see cref="ConfigureJwtBearer"/>); this class adds the
/// per-request user-state check (is_active / token_version / role, cached 10 s).
/// </summary>
public static partial class AuthService
{
    public const string Issuer = "agnes-service";
    public const string Audience = "agnes-service";
    public const string CookieName = "agnes_rt";
    public const string CookiePath = "/api/auth";
    public const int MinSecretLen = 32;
    public const int RotationGraceSeconds = 10;
    public const int UserCacheSeconds = 10;
    public static readonly string[] Roles = { "user", "admin" };

    [GeneratedRegex("^[a-zA-Z0-9_.-]{3,32}$")]
    public static partial Regex UsernameRe();

    public static int AccessTtl => Settings.GetInt("JWT_ACCESS_MINUTES", 30, 5, 24 * 60) * 60;
    public static int RefreshTtl => Settings.GetInt("REFRESH_TOKEN_DAYS", 7, 1, 90) * 86400;
    public static int FamilyTtl => Settings.GetInt("REFRESH_FAMILY_DAYS", 30, 1, 365) * 86400;
    public static int MaxFailedLogins => Settings.GetInt("LOGIN_MAX_FAILURES", 5, 1);
    public static int LockoutMinutes => Settings.GetInt("LOGIN_LOCKOUT_MINUTES", 15, 1);
    public static int IpBanLockouts => Settings.GetInt("IP_BAN_AFTER_LOCKOUTS", 3, 1);
    public static int IpBanHours => Settings.GetInt("IP_BAN_HOURS", 24, 0);     // 0 = permanent
    public static int IpMaxFailures => Settings.GetInt("IP_MAX_FAILURES", 20, 1); // per 15 min

    // ------------------------------------------------------------------ JWT secret

    public static string SecretStatus()
    {
        var s = Settings.JwtSecret;
        if (string.IsNullOrEmpty(s)) return "missing";
        return s.Length < MinSecretLen ? "too_short" : "ok";
    }

    public static SymmetricSecurityKey RequireKey()
    {
        switch (SecretStatus())
        {
            case "missing":
                throw new ApiError(503, "JWT_SECRET not configured - the service cannot issue or verify tokens") { Code = "jwt_secret_missing" };
            case "too_short":
                throw new ApiError(503, $"JWT_SECRET is too short (need >= {MinSecretLen} chars)") { Code = "jwt_secret_missing" };
        }
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Settings.JwtSecret)) { KeyId = null };
    }

    private static readonly JsonWebTokenHandler TokenHandler = new() { SetDefaultTimesOnTokenCreation = false };

    public static (string token, int ttl) IssueAccessToken(long userId, string username, string role, int tokenVersion)
    {
        var key = RequireKey();
        var now = DateTimeOffset.UtcNow;
        var ttl = AccessTtl;
        var desc = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddSeconds(ttl).UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["name"] = username,
                ["role"] = role,
                ["ver"] = tokenVersion,
                ["typ"] = "access",
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };
        return (TokenHandler.CreateToken(desc), ttl);
    }

    /// <summary>JwtBearer options: HS256 only, iss/aud pinned, 30 s leeway, secret re-read per request.</summary>
    public static void ConfigureJwtBearer(JwtBearerOptions o)
    {
        o.MapInboundClaims = false;
        o.RequireHttpsMetadata = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },  // blocks alg=none / switching
            ClockSkew = TimeSpan.FromSeconds(30),
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "name",
            RoleClaimType = "role",
            IssuerSigningKeyResolver = (_, _, _, _) =>
                SecretStatus() == "ok" ? new[] { RequireKey() } : Array.Empty<SecurityKey>(),
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                if (ctx.Principal?.FindFirst("typ")?.Value != "access") ctx.Fail("not an access token");
                return Task.CompletedTask;
            },
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                var header = ctx.Request.Headers.Authorization.ToString();
                var hasBearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7;
                if (hasBearer && SecretStatus() != "ok")
                {
                    try { RequireKey(); }
                    catch (ApiError e) { await Json.Write(ctx.HttpContext, e.Status, e.Payload()); return; }
                }
                string msg; string? err = "invalid_token";
                if (!hasBearer) { msg = "authentication required (Authorization: Bearer <token>)"; err = null; }
                else msg = "invalid token: " + ctx.AuthenticateFailure switch
                {
                    SecurityTokenExpiredException => "token expired",
                    SecurityTokenNotYetValidException => "token not yet valid",
                    SecurityTokenInvalidIssuerException => "wrong issuer",
                    SecurityTokenInvalidAudienceException => "wrong audience",
                    SecurityTokenInvalidAlgorithmException => "unsupported token algorithm",
                    SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException => "bad signature",
                    SecurityTokenMalformedException => "malformed token",
                    { Message: "not an access token" } => "not an access token",
                    _ => "rejected",
                };
                var e401 = Unauthorized(msg, err);
                await Json.Write(ctx.HttpContext, 401, e401.Payload(), e401.Headers);
            },
            // valid token but the policy (only "Admin" exists) refused it → JSON 403
            OnForbidden = ctx => Json.Write(ctx.HttpContext, 403,
                new JsonObject { ["error"] = "admin role required", ["code"] = "forbidden" }),
        };
    }

    public static ApiError Unauthorized(string msg, string? error = "invalid_token")
    {
        var hdr = "Bearer realm=\"agnes-service\"" + (error != null ? $", error=\"{error}\"" : "");
        return new ApiError(401, msg) { Code = "unauthorized", Headers = { new("WWW-Authenticate", hdr) } };
    }

    // ------------------------------------------------------------------ per-request user state

    private sealed record UserState(bool Exists, bool Active, int Version, string Role, string Username, DateTime At,
        string ReadingIngest = "none", string? MuseId = null)
    {
        public long Id { get; init; }
    }
    private static volatile bool _ingestCols = true;   // false while the phase-2 migration is not applied (42703)

    /// <summary>Load one user's state (phase 2: + reading_ingest / muse_id; falls back if the columns are missing).</summary>
    private static async Task<UserState> LoadState(NpgsqlConnection c, string where, object key, CancellationToken ct)
    {
        if (_ingestCols)
        {
            try
            {
                await using var cmd = Db.Cmd(c, "SELECT is_active, token_version, role, username, reading_ingest, muse_id, id FROM agnes_auth.users WHERE " + where, null, key);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                return await r.ReadAsync(ct)
                    ? new UserState(true, r.GetBoolean(0), r.GetInt32(1), r.GetString(2), r.GetString(3), DateTime.UtcNow,
                        r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)) { Id = r.GetInt64(6) }
                    : new UserState(false, false, -1, "", "", DateTime.UtcNow);
            }
            catch (PostgresException e) when (e.SqlState == "42703")
            {
                Log.Warn("agnes_auth.users has no reading_ingest/muse_id yet (apply the phase-2 migration); ingest disabled");
                _ingestCols = false;
            }
        }
        await using var cmd2 = Db.Cmd(c, "SELECT is_active, token_version, role, username, id FROM agnes_auth.users WHERE " + where, null, key);
        await using var r2 = await cmd2.ExecuteReaderAsync(ct);
        return await r2.ReadAsync(ct)
            ? new UserState(true, r2.GetBoolean(0), r2.GetInt32(1), r2.GetString(2), r2.GetString(3), DateTime.UtcNow) { Id = r2.GetInt64(4) }
            : new UserState(false, false, -1, "", "", DateTime.UtcNow);
    }

    private static readonly ConcurrentDictionary<long, UserState> UserCache = new();

    public static void ForgetUserState() => UserCache.Clear();

    /// <summary>Called by every protected endpoint after JwtBearer accepted the token.</summary>
    public static async Task<AuthUser> RequireUser(HttpContext ctx)
    {
        var p = ctx.User;
        if (p?.Identity?.IsAuthenticated != true) throw Unauthorized("authentication required (Authorization: Bearer <token>)", null);
        if (!long.TryParse(p.FindFirst("sub")?.Value, out var uid)) throw Unauthorized("invalid token: bad subject");
        if (!int.TryParse(p.FindFirst("ver")?.Value, out var ver)) throw Unauthorized("invalid token: no version");
        long.TryParse(p.FindFirst("exp")?.Value, out var exp);

        if (!UserCache.TryGetValue(uid, out var st) || DateTime.UtcNow - st.At > TimeSpan.FromSeconds(UserCacheSeconds))
        {
            try
            {
                await using var c = await Db.Open(ctx.RequestAborted);
                st = await LoadState(c, "id = $1", uid, ctx.RequestAborted);
            }
            catch (ApiError) { throw; }
            catch (Exception e) when (e is NpgsqlException or TimeoutException)
            {
                Log.Warn($"auth db query failed: {e.GetType().Name}");
                throw new ApiError(503, "auth database unavailable") { Code = "db_unavailable" };
            }
            if (UserCache.Count > 1000) UserCache.Clear();
            UserCache[uid] = st;
        }
        if (!st.Exists) throw Unauthorized("invalid token: unknown user");
        if (!st.Active) throw Unauthorized("account disabled");
        if (st.Version != ver) throw Unauthorized("token revoked");
        if (!Roles.Contains(st.Role)) throw new ApiError(403, "role not allowed") { Code = "forbidden" };
        return new AuthUser(uid, st.Username, st.Role, exp)   // DB is the source of truth for the role
            { ReadingIngest = st.ReadingIngest, MuseId = st.MuseId };
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Sha256(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    private static string NewOpaqueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IpExempt(string ip) => !IPAddress.TryParse(ip, out var a) || IPAddress.IsLoopback(a);

    private static async Task Audit(NpgsqlConnection c, NpgsqlTransaction tx, long? userId, string? username, string ip,
        bool success, string reason, string? ua)
    {
        await using var cmd = Db.Cmd(c,
            "INSERT INTO agnes_auth.login_audit (user_id, username, ip, success, reason, user_agent) VALUES ($1, $2, $3::inet, $4, $5, $6)",
            tx, userId, Trunc(username, 64), ip, success, reason, Trunc(ua, 300));
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Trunc(string? s, int n) => s == null ? "" : s.Length > n ? s[..n] : s;

    private static async Task<object?> Scalar(NpgsqlConnection c, NpgsqlTransaction tx, string sql, params object?[] args)
    {
        await using var cmd = Db.Cmd(c, sql, tx, args);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<int> Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, params object?[] args)
    {
        await using var cmd = Db.Cmd(c, sql, tx, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IpBlocked(NpgsqlConnection c, NpgsqlTransaction tx, string ip) =>
        await Scalar(c, tx, "SELECT 1 FROM agnes_auth.ip_block WHERE ip = $1::inet AND blocked_at IS NOT NULL " +
                            "AND (blocked_until IS NULL OR blocked_until > now())", ip) != null;

    private static async Task<long> RecentIpFailures(NpgsqlConnection c, NpgsqlTransaction tx, string ip) =>
        (long)(await Scalar(c, tx, "SELECT count(*) FROM agnes_auth.login_audit WHERE ip = $1::inet AND NOT success " +
                                   "AND at > now() - interval '15 minutes'", ip) ?? 0L);

    /// <summary>Count a lockout event against an IP; ban it after N events within 24 h (never loopback).</summary>
    private static async Task RegisterIpLockout(NpgsqlConnection c, NpgsqlTransaction tx, string ip)
    {
        var n = (int)(await Scalar(c, tx,
            "INSERT INTO agnes_auth.ip_block (ip, lockout_count, updated_at) VALUES ($1::inet, 1, now()) " +
            "ON CONFLICT (ip) DO UPDATE SET lockout_count = CASE WHEN agnes_auth.ip_block.updated_at < now() - interval '24 hours' " +
            "THEN 1 ELSE agnes_auth.ip_block.lockout_count + 1 END, updated_at = now() RETURNING lockout_count", ip) ?? 0);
        if (n < IpBanLockouts) return;
        if (IpExempt(ip)) { Log.Warn($"ip {ip} reached ban threshold but is loopback - not banned"); return; }
        var hours = IpBanHours;
        await Exec(c, tx,
            "UPDATE agnes_auth.ip_block SET blocked_at = now(), lockout_count = 0, " +
            "blocked_until = CASE WHEN $1::int > 0 THEN now() + make_interval(hours => $1::int) END, updated_at = now() " +
            "WHERE ip = $2::inet", hours, ip);
        Log.Warn($"ip {ip} banned after repeated lockouts");
    }

    private static async Task<(string plain, Guid id, int maxAge)> NewRefreshToken(NpgsqlConnection c, NpgsqlTransaction tx,
        long userId, Guid familyId, DateTime familyExpiresAt, string ip, string? ua)
    {
        var plain = NewOpaqueToken();
        await using var cmd = Db.Cmd(c,
            "INSERT INTO agnes_auth.refresh_tokens (user_id, family_id, token_hash, expires_at, family_expires_at, user_agent, ip) " +
            "VALUES ($1, $2, $3, LEAST(now() + make_interval(secs => $4::double precision), $5), $5, $6, $7::inet) " +
            "RETURNING id, extract(epoch FROM (expires_at - now()))::int",
            tx, userId, familyId, Sha256(plain), (double)RefreshTtl, familyExpiresAt, Trunc(ua, 300), ip);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (plain, r.GetGuid(0), Math.Max(0, r.GetInt32(1)));
    }

    private static JsonObject UserView(long id, string username, string role) =>
        new() { ["id"] = id, ["username"] = username, ["role"] = role };

    private static JsonObject TokenBody(long uid, string uname, string role, int ver)
    {
        var (token, ttl) = IssueAccessToken(uid, uname, role, ver);
        return new JsonObject
        {
            ["access_token"] = token, ["token_type"] = "Bearer", ["expires_in"] = ttl,
            ["user"] = UserView(uid, uname, role),
        };
    }

    // ------------------------------------------------------------------ login / refresh / logout

    public static async Task<LoginResult> Login(string? username, string? password, string ip, string? ua)
    {
        RequireKey();
        username = username?.Trim() ?? "";
        password ??= "";
        if (username.Length == 0 || password.Length == 0)
            throw new ApiError(400, "username and password are required") { Code = "bad_request" };
        if (password.Length > Passwords.MaxLength || username.Length > 64)
            throw new ApiError(400, "username or password too long") { Code = "bad_request" };

        ApiError? err = null;
        LoginResult? result = null;
        await using (var c = await Db.Open())
        {
            await using var tx = await c.BeginTransactionAsync();
            if (await IpBlocked(c, tx, ip))
            {
                await Audit(c, tx, null, username, ip, false, "ip_blocked", ua);
                err = new ApiError(403, "too many failed logins from this IP; it is blocked") { Code = "ip_blocked" };
            }
            else if (await RecentIpFailures(c, tx, ip) >= IpMaxFailures)
            {
                await Audit(c, tx, null, username, ip, false, "ip_throttled", ua);
                err = new ApiError(429, "too many failed logins from this IP, try again later")
                    { Code = "ip_throttled", Headers = { new("Retry-After", "900") } };
            }
            else
            {
                (long uid, string uname, string phash, string role, bool active, int ver, bool locked, int lockedSecs)? row = null;
                if (UsernameRe().IsMatch(username))
                {
                    await using var cmd = Db.Cmd(c,
                        "SELECT id, username, password_hash, role, is_active, token_version, " +
                        "coalesce(locked_until > now(), false), coalesce(extract(epoch FROM (locked_until - now()))::int, 0) " +
                        "FROM agnes_auth.users WHERE lower(username) = lower($1) FOR UPDATE", tx, username);
                    await using var r = await cmd.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                        row = (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
                               r.GetInt32(5), r.GetBoolean(6), r.GetInt32(7));
                }
                if (row == null)
                {
                    Passwords.BurnTime(password);
                    await Audit(c, tx, null, username, ip, false, "unknown_user", ua);
                    err = new ApiError(401, "invalid username or password") { Code = "invalid_credentials" };
                }
                else
                {
                    var u = row.Value;
                    if (u.locked)
                    {
                        await Audit(c, tx, u.uid, u.uname, ip, false, "locked", ua);
                        err = Locked(u.lockedSecs);
                    }
                    else if (!Passwords.Verify(password, u.phash))
                    {
                        var n = (int)(await Scalar(c, tx,
                            "UPDATE agnes_auth.users SET failed_logins = failed_logins + 1, updated_at = now() WHERE id = $1 RETURNING failed_logins",
                            u.uid) ?? 0);
                        if (n >= MaxFailedLogins)
                        {
                            var mins = LockoutMinutes;
                            await Exec(c, tx, "UPDATE agnes_auth.users SET failed_logins = 0, locked_until = now() + make_interval(mins => $1::int) WHERE id = $2",
                                mins, u.uid);
                            await Audit(c, tx, u.uid, u.uname, ip, false, "bad_password_locked", ua);
                            await RegisterIpLockout(c, tx, ip);
                            err = Locked(mins * 60);
                        }
                        else
                        {
                            await Audit(c, tx, u.uid, u.uname, ip, false, "bad_password", ua);
                            err = new ApiError(401, "invalid username or password") { Code = "invalid_credentials" };
                        }
                    }
                    else if (!u.active)
                    {
                        await Audit(c, tx, u.uid, u.uname, ip, false, "inactive", ua);
                        err = new ApiError(403, "account disabled") { Code = "disabled" };
                    }
                    else
                    {
                        await Exec(c, tx, "UPDATE agnes_auth.users SET failed_logins = 0, locked_until = NULL, last_login_at = now(), updated_at = now() WHERE id = $1", u.uid);
                        var famExp = (DateTime)(await Scalar(c, tx, "SELECT now() + make_interval(secs => $1::double precision)", (double)FamilyTtl))!;
                        var (plain, _, maxAge) = await NewRefreshToken(c, tx, u.uid, Guid.NewGuid(), famExp, ip, ua);
                        // housekeeping: drop this user's long-dead refresh tokens
                        await Exec(c, tx, "DELETE FROM agnes_auth.refresh_tokens WHERE user_id = $1 AND family_expires_at < now() - interval '30 days'", u.uid);
                        await Audit(c, tx, u.uid, u.uname, ip, true, "ok", ua);
                        result = new LoginResult(TokenBody(u.uid, u.uname, u.role, u.ver), plain, maxAge);
                    }
                }
            }
            if (err != null && (err.Status == 401 || err.Status == 423) && await RecentIpFailures(c, tx, ip) == IpMaxFailures)
                await RegisterIpLockout(c, tx, ip);     // IP just crossed the spray threshold
            await tx.CommitAsync();   // failure counters / audit rows persist even on error
        }
        if (err != null)
        {
            Log.Info($"login failed for ip {ip}: {err.Code}");
            throw err;
        }
        ForgetUserState();
        return result!;
    }

    private static ApiError Locked(int secs) => new(423, "account locked after too many failed logins")
    {
        Code = "locked", Headers = { new("Retry-After", secs.ToString()) },
        Extra = new JsonObject { ["retry_after"] = secs },
    };

    public static async Task<LoginResult> Refresh(string? plain, string ip, string? ua)
    {
        RequireKey();
        if (string.IsNullOrEmpty(plain) || plain.Length > 200)
            throw new ApiError(401, "no refresh token") { Code = "no_refresh_token" };
        ApiError? err = null;
        LoginResult? result = null;
        await using (var c = await Db.Open())
        {
            await using var tx = await c.BeginTransactionAsync();
            (Guid rid, long uid, Guid fam, DateTime famExp, bool expired, bool revoked, bool justRotated,
                string uname, string role, bool active, int ver)? row = null;
            await using (var cmd = Db.Cmd(c,
                "SELECT rt.id, rt.user_id, rt.family_id, rt.family_expires_at, " +
                "rt.expires_at <= now() OR rt.family_expires_at <= now(), rt.revoked_at IS NOT NULL, " +
                "coalesce(rt.used_at > now() - make_interval(secs => $1::double precision), false), " +
                "u.username, u.role, u.is_active, u.token_version " +
                "FROM agnes_auth.refresh_tokens rt JOIN agnes_auth.users u ON u.id = rt.user_id " +
                "WHERE rt.token_hash = $2 FOR UPDATE OF rt", tx, (double)RotationGraceSeconds, Sha256(plain)))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                if (await r.ReadAsync())
                    row = (r.GetGuid(0), r.GetInt64(1), r.GetGuid(2), r.GetDateTime(3), r.GetBoolean(4), r.GetBoolean(5),
                           r.GetBoolean(6), r.GetString(7), r.GetString(8), r.GetBoolean(9), r.GetInt32(10));
            }
            if (row == null)
                err = new ApiError(401, "invalid refresh token") { Code = "invalid_refresh_token" };
            else
            {
                var t = row.Value;
                if (t.revoked)
                {
                    if (t.justRotated)   // concurrent refresh from another tab: refuse, don't punish
                        // distinct code (PHASE2-AUTH-DESIGN §7-9): the endpoint keeps the cookie (the other tab already
                        // stored the rotated one in the shared jar) and the page simply refreshes again a moment later
                        err = new ApiError(401, "refresh token already rotated") { Code = "refresh_rotated" };
                    else
                    {
                        var n = await Exec(c, tx, "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), revoke_reason = 'reuse_detected' " +
                                                  "WHERE family_id = $1 AND revoked_at IS NULL", t.fam);
                        await Audit(c, tx, t.uid, t.uname, ip, false, "refresh_reuse", ua);
                        Log.Warn($"refresh token reuse detected for user {t.uid} (revoked {n})");
                        err = new ApiError(401, "refresh token reuse detected; session revoked") { Code = "invalid_refresh_token" };
                    }
                }
                else if (t.expired || !t.active)
                {
                    await Exec(c, tx, "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), revoke_reason = $1 WHERE family_id = $2 AND revoked_at IS NULL",
                        t.expired ? "expired" : "user_inactive", t.fam);
                    err = new ApiError(401, t.expired ? "session expired" : "account disabled") { Code = "invalid_refresh_token" };
                }
                else
                {
                    var (newPlain, newId, maxAge) = await NewRefreshToken(c, tx, t.uid, t.fam, t.famExp, ip, ua);
                    await Exec(c, tx, "UPDATE agnes_auth.refresh_tokens SET used_at = now(), revoked_at = now(), revoke_reason = 'rotated', replaced_by = $1 WHERE id = $2",
                        newId, t.rid);
                    result = new LoginResult(TokenBody(t.uid, t.uname, t.role, t.ver), newPlain, maxAge);
                }
            }
            await tx.CommitAsync();
        }
        if (err != null) throw err;
        return result!;
    }

    /// <summary>Revoke the session (refresh-token family) of the cookie. Never throws.</summary>
    public static async Task<bool> Logout(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || plain.Length > 200) return false;
        try
        {
            await using var c = await Db.Open();
            await using var cmd = Db.Cmd(c,
                "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), revoke_reason = 'logout' WHERE revoked_at IS NULL " +
                "AND family_id = (SELECT family_id FROM agnes_auth.refresh_tokens WHERE token_hash = $1)", null, Sha256(plain));
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
        catch (Exception e)
        {
            if (e is not ApiError) Log.Warn($"logout revoke failed: {e.GetType().Name}");
            return false;
        }
    }
}
