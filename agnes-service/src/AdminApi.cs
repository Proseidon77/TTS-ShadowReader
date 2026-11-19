using System.Net;
using System.Text.Json.Nodes;
using Npgsql;

namespace AgnesService;

/// <summary>
/// Admin backend (TASK5), modelled on dbapi's admin users API. See DESIGN.md "Admin API".
///
///   GET    /api/admin/users                          list (+ limits, effective limits, today's usage)
///   POST   /api/admin/users                          {username, password, role?, limits?} → 201
///   PATCH  /api/admin/users/{id}                     {role?, is_active?, limits?, reading_ingest?, muse_id?} partial update
///   POST   /api/admin/users/{id}/reset-password      {newPassword} → new hash, sessions killed, unlocked
///   DELETE /api/admin/users/{id}
///   GET    /api/admin/ip-blocks                      DELETE /api/admin/ip-blocks/{ip}
///   GET    /api/admin/audit?limit=100
///
/// Authorization is layered: the "Admin" policy (JWT role claim = admin, checked by the authorization
/// middleware before any body is read: no token → 401, other role → 403), then <see cref="RequireAdmin"/>
/// re-checks the role in the DB (source of truth: a just-demoted admin's still-valid JWT gets 403).
///
/// Self-protection (dbapi): an admin cannot change their own role/status, reset their own password
/// here, or delete themselves (400 cannot_modify_self). Last-admin guard: no operation may leave zero
/// active admins (409 last_admin), whoever the actor is. Every mutation writes one admin_audit row in
/// the same transaction.
/// </summary>
public static class AdminApi
{
    public const string Policy = "Admin";
    public const int MaxLimitValue = 1_000_000;
    private const long BodyMax = 16 * 1024;

    /// <summary>Per-account override columns (TASK4); also the JSON keys of <c>limits</c> in requests/responses.</summary>
    public static readonly string[] LimitColumns = { "rate_per_minute", "daily_quota", "video_daily_quota", "max_concurrent" };

    public static string Iso(DateTime dt) => dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>TASK6 phase 2: values of users.reading_ingest.</summary>
    public static readonly string[] ReadingIngestValues = { "none", "web", "agent" };

    /// <summary>^A\d{3}$ is reserved for Muse agent ids (plan §1.2): never a login name.</summary>
    public static bool ReservedUsername(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"^[Aa]\d{3}$");

    private static string ParseReadingIngest(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && ReadingIngestValues.Contains(s)
            ? s : throw Bad("invalid_reading_ingest", "reading_ingest must be 'none', 'web' or 'agent'", "reading_ingest");

    /// <summary>muse_id: "A001"… or null (clear).</summary>
    private static string? ParseMuseId(JsonNode? n)
    {
        if (n == null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s) && System.Text.RegularExpressions.Regex.IsMatch(s, @"^A\d{3}$")) return s;
        throw Bad("invalid_muse_id", "muse_id must look like A001 (or null to clear)", "muse_id");
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(Policy);
        g.MapGet("/users", W(ListUsers));
        g.MapPost("/users", W(CreateUser));
        g.MapPatch("/users/{id:long}", W(ctx => PatchUser(ctx, RouteId(ctx))));
        g.MapPost("/users/{id:long}/reset-password", W(ctx => ResetPassword(ctx, RouteId(ctx))));
        g.MapDelete("/users/{id:long}", W(ctx => DeleteUser(ctx, RouteId(ctx))));
        g.MapGet("/ip-blocks", W(ListIpBlocks));
        g.MapDelete("/ip-blocks/{ip}", W(ctx => Unblock(ctx, ctx.Request.RouteValues["ip"]?.ToString() ?? "")));
        g.MapGet("/audit", W(ListAudit));
        g.MapGet("/calls", W(ListCalls));
    }

    private static long RouteId(HttpContext ctx) => long.Parse(ctx.Request.RouteValues["id"]!.ToString()!);

    /// <summary>Map DB problems to clear 503s (fail closed: no admin action without its audit row).</summary>
    public static RequestDelegate W(Func<HttpContext, Task> h) => async ctx =>
    {
        try { await h(ctx); }
        catch (PostgresException e) when (e.SqlState is "42P01" or "42703")
        {
            Log.Warn($"admin db objects missing: {e.MessageText}");
            throw new ApiError(503, "admin tables missing: apply auth_schema.sql (TASK5 migration adds agnes_auth.admin_audit)")
                { Code = "admin_unavailable" };
        }
        catch (NpgsqlException e) when (e is not PostgresException)
        {
            Log.Warn($"admin db query failed: {e.GetType().Name}");
            throw new ApiError(503, "auth database unavailable") { Code = "db_unavailable" };
        }
    };

    // ------------------------------------------------------------------ validation helpers

    private static ApiError Bad(string code, string msg, string? field = null, int status = 400, JsonObject? extra = null)
    {
        var x = extra ?? new JsonObject();
        if (field != null) x["field"] = field;
        return new ApiError(status, msg) { Code = code, Extra = x.Count > 0 ? x : null };
    }

    public static async Task<AuthUser> RequireAdmin(HttpContext ctx)
    {
        var u = await AuthService.RequireUser(ctx);
        if (u.Role != "admin") throw new ApiError(403, "admin role required") { Code = "forbidden" };
        return u;
    }

    private static async Task<JsonObject> ReadBody(HttpContext ctx)
    {
        Http.RequireJsonContentType(ctx);
        var body = await Json.ReadBody(ctx, BodyMax);
        try { Touch(body); }   // JsonObject materializes lazily; surface duplicate keys as 400, not 500
        catch (ArgumentException) { throw new ApiError(400, "body must not contain duplicate keys"); }
        return body;
    }

    private static void Touch(JsonNode? n)
    {
        if (n is JsonObject o) foreach (var kv in o) Touch(kv.Value);
        else if (n is JsonArray a) foreach (var x in a) Touch(x);
    }

    private static void RejectUnknown(JsonObject body, params string[] allowed)
    {
        foreach (var (k, _) in body)
            if (!allowed.Contains(k))
                throw Bad("unknown_field", $"unknown field '{k}' (allowed: {string.Join(", ", allowed)})", k);
    }

    /// <summary>Present, non-empty string or 400 missing_field naming the field; wrong type → <paramref name="badCode"/>.</summary>
    private static string RequireString(JsonObject body, string field, string badCode)
    {
        var n = body[field];
        if (n == null) throw Bad("missing_field", $"{field} is required", field);
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s)) throw Bad(badCode, $"{field} must be a string", field);
        if (s.Length == 0) throw Bad("missing_field", $"{field} is required", field);
        return s;
    }

    private static string ParseRole(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && AuthService.Roles.Contains(s)
            ? s : throw Bad("invalid_role", "role must be 'user' or 'admin'", "role");

    /// <summary>
    /// <c>limits</c> object → column → value. Only keys present in the JSON appear in the result:
    /// a number (0…1 000 000) = set override, JSON null = clear override (back to the system default).
    /// </summary>
    private static Dictionary<string, int?> ParseLimits(JsonNode? n)
    {
        if (n is not JsonObject o)
            throw Bad("invalid_limits", $"limits must be an object with any of: {string.Join(", ", LimitColumns)}", "limits");
        var d = new Dictionary<string, int?>();
        foreach (var (k, v) in o)
        {
            if (!LimitColumns.Contains(k))
                throw Bad("invalid_limits", $"unknown limit '{k}' (allowed: {string.Join(", ", LimitColumns)})", "limits." + k);
            if (v == null) { d[k] = null; continue; }
            if (v is not JsonValue jv || !jv.TryGetValue<int>(out var i) || i < 0 || i > MaxLimitValue)
                throw Bad("invalid_limits", $"limits.{k} must be an integer 0..{MaxLimitValue} or null (= system default)", "limits." + k);
            d[k] = i;
        }
        return d;
    }

    private static JsonObject FromTo(JsonNode? from, JsonNode? to) => new() { ["from"] = from, ["to"] = to };

    // ------------------------------------------------------------------ user view

    private const string UserSelect =
        "SELECT u.id, u.username, u.role, u.is_active, u.created_at, u.last_login_at, " +
        "CASE WHEN u.locked_until > now() THEN u.locked_until END, u.failed_logins, " +
        "u.rate_per_minute, u.daily_quota, u.video_daily_quota, u.max_concurrent, " +
        "coalesce((SELECT sum(d.count) FROM agnes_auth.usage_daily d WHERE d.user_id = u.id AND d.day = $1), 0)::int, " +
        "coalesce((SELECT d.count FROM agnes_auth.usage_daily d WHERE d.user_id = u.id AND d.day = $1 AND d.kind = 'video'), 0), " +
        "u.reading_ingest, u.muse_id " +
        "FROM agnes_auth.users u";

    private static async Task<List<JsonObject>> LoadUsers(NpgsqlConnection c, long? id, CancellationToken ct)
    {
        var (day, _) = LimitService.QuotaDay(DateTimeOffset.UtcNow);
        await using var cmd = id == null
            ? Db.Cmd(c, UserSelect + " ORDER BY u.id", null, day)
            : Db.Cmd(c, UserSelect + " WHERE u.id = $2", null, day, id.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<JsonObject>();
        while (await r.ReadAsync(ct))
        {
            int? O(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            var overridden = new JsonArray();
            for (var i = 0; i < 4; i++) if (O(8 + i) != null) overridden.Add(LimitColumns[i]);
            list.Add(new JsonObject
            {
                ["id"] = r.GetInt64(0),
                ["username"] = r.GetString(1),
                ["role"] = r.GetString(2),
                ["is_active"] = r.GetBoolean(3),
                ["created_at"] = Iso(r.GetDateTime(4)),
                ["last_login_at"] = r.IsDBNull(5) ? null : Iso(r.GetDateTime(5)),
                ["locked_until"] = r.IsDBNull(6) ? null : Iso(r.GetDateTime(6)),   // only while currently locked
                ["failed_logins"] = r.GetInt32(7),
                ["limits"] = new JsonObject   // overrides; null = system default
                {
                    ["rate_per_minute"] = O(8), ["daily_quota"] = O(9), ["video_daily_quota"] = O(10), ["max_concurrent"] = O(11),
                },
                ["effective_limits"] = new JsonObject   // same keys as GET /api/limits
                {
                    ["per_minute"] = O(8) ?? LimitService.DefaultPerMinute,
                    ["per_day"] = O(9) ?? LimitService.DefaultPerDay,
                    ["video_per_day"] = O(10) ?? LimitService.DefaultVideoPerDay,
                    ["concurrent"] = O(11) ?? LimitService.DefaultConcurrent,
                    ["overridden"] = overridden,
                },
                ["usage_today"] = new JsonObject
                {
                    ["day"] = day.ToString("yyyy-MM-dd"), ["total"] = r.GetInt32(12), ["video"] = r.GetInt32(13),
                },
                ["reading_ingest"] = r.GetString(14),                       // TASK6 phase 2
                ["muse_id"] = r.IsDBNull(15) ? null : r.GetString(15),
            });
        }
        return list;
    }

    private static async Task<JsonObject> LoadUser(long id, CancellationToken ct)
    {
        await using var c = await Db.Open(ct);
        return (await LoadUsers(c, id, ct)).FirstOrDefault() ?? throw Bad("not_found", "no such user", status: 404);
    }

    private sealed record Target(long Id, string Username, string Role, bool Active, int?[] Limits, string ReadingIngest, string? MuseId);

    /// <summary>
    /// Row-lock the target AND every active admin in one statement, in id order (no deadlocks between
    /// concurrent admin requests). Returns the target (null if absent) and the number of OTHER active
    /// admins, which is what the last-admin guard needs; concurrent demotions serialize on these locks.
    /// </summary>
    private static async Task<(Target? target, int otherAdmins)> LockTarget(NpgsqlConnection c, NpgsqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = Db.Cmd(c,
            "SELECT id, username, role, is_active, rate_per_minute, daily_quota, video_daily_quota, max_concurrent, reading_ingest, muse_id " +
            "FROM agnes_auth.users WHERE id = $1 OR (role = 'admin' AND is_active) ORDER BY id FOR UPDATE", tx, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        Target? t = null;
        var others = 0;
        while (await r.ReadAsync(ct))
        {
            int? O(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            var row = new Target(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), new[] { O(4), O(5), O(6), O(7) },
                r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));
            if (row.Id == id) t = row;
            else if (row.Role == "admin" && row.Active) others++;
        }
        return (t, others);
    }

    private static ApiError LastAdmin() => new(409,
        "refused: this is the last active admin account (at least one admin must remain); promote or enable another admin first")
        { Code = "last_admin" };

    private static ApiError Self(string what, JsonObject? extra = null) => Bad("cannot_modify_self", what, extra: extra);

    private static async Task<int> Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, params object?[] a)
    {
        await using var q = Db.Cmd(c, sql, tx, a);
        return await q.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------ handlers

    private static async Task ListUsers(HttpContext ctx)
    {
        await RequireAdmin(ctx);
        await using var c = await Db.Open(ctx.RequestAborted);
        var users = new JsonArray();
        foreach (var u in await LoadUsers(c, null, ctx.RequestAborted)) users.Add(u);
        await Json.Write(ctx, 200, new JsonObject
        {
            ["users"] = users,
            ["defaults"] = new JsonObject
            {
                ["per_minute"] = LimitService.DefaultPerMinute, ["per_day"] = LimitService.DefaultPerDay,
                ["video_per_day"] = LimitService.DefaultVideoPerDay, ["concurrent"] = LimitService.DefaultConcurrent,
            },
            ["day_boundary"] = "00:00 Asia/Tokyo",
        });
    }

    private static async Task CreateUser(HttpContext ctx)
    {
        var actor = await RequireAdmin(ctx);
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        RejectUnknown(body, "username", "password", "role", "limits", "reading_ingest", "muse_id");
        var username = RequireString(body, "username", "invalid_username").Trim();
        if (!AuthService.UsernameRe().IsMatch(username))
            throw Bad("invalid_username", "username must be 3-32 characters: letters, digits, '_', '.', '-'", "username");
        if (ReservedUsername(username))
            throw Bad("reserved_username", "usernames like A001 are reserved for Muse agent ids", "username");
        var readingIngest = body.ContainsKey("reading_ingest") ? ParseReadingIngest(body["reading_ingest"]) : "none";
        var museId = body.ContainsKey("muse_id") ? ParseMuseId(body["muse_id"]) : null;
        var password = RequireString(body, "password", "weak_password");
        var role = body.ContainsKey("role") ? ParseRole(body["role"]) : "user";
        var limits = body.ContainsKey("limits") ? ParseLimits(body["limits"]) : new();
        var problem = Passwords.PolicyProblem(password, username);
        if (problem != null) throw Bad("weak_password", problem, "password");
        var hash = Passwords.Hash(password);
        int? L(string k) => limits.GetValueOrDefault(k);

        long id;
        await using (var c = await Db.Open(ct))
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            try
            {
                await using var q = Db.Cmd(c,
                    "INSERT INTO agnes_auth.users (username, password_hash, role, rate_per_minute, daily_quota, video_daily_quota, max_concurrent, reading_ingest, muse_id) " +
                    "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING id", tx,
                    username, hash, role, L("rate_per_minute"), L("daily_quota"), L("video_daily_quota"), L("max_concurrent"), readingIngest, museId);
                id = (long)(await q.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException e) when (e.SqlState == "23505")
            {
                if (e.ConstraintName == "users_muse_id_key")
                    throw Bad("muse_id_taken", $"muse_id '{museId}' is already mapped to another account", "muse_id", 409);
                throw Bad("username_taken", $"username '{username}' already exists (case-insensitive)", "username", 409);
            }
            var lim = new JsonObject();
            foreach (var (k, v) in limits) if (v != null) lim[k] = v;
            await AuditService.Write(c, tx, actor.Id, actor.Username, Http.ClientIp(ctx), "create_user", id, username,
                new JsonObject { ["role"] = role, ["limits"] = lim, ["reading_ingest"] = readingIngest, ["muse_id"] = museId });
            await tx.CommitAsync(ct);
        }
        await Json.Write(ctx, 201, new JsonObject { ["user"] = await LoadUser(id, ct) });
    }

    private static async Task PatchUser(HttpContext ctx, long id)
    {
        var actor = await RequireAdmin(ctx);
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        RejectUnknown(body, "role", "is_active", "limits", "reading_ingest", "muse_id");
        // presence detection: a key that is absent means "leave unchanged"
        var newRole = body.ContainsKey("role") ? ParseRole(body["role"]) : null;
        bool? newActive = null;
        if (body.ContainsKey("is_active"))
            newActive = body["is_active"] is JsonValue v && v.TryGetValue<bool>(out var b)
                ? b : throw Bad("invalid_is_active", "is_active must be true or false", "is_active");
        var limits = body.ContainsKey("limits") ? ParseLimits(body["limits"]) : new();
        var newIngest = body.ContainsKey("reading_ingest") ? ParseReadingIngest(body["reading_ingest"]) : null;
        var museGiven = body.ContainsKey("muse_id");
        var newMuse = museGiven ? ParseMuseId(body["muse_id"]) : null;
        if (newRole == null && newActive == null && limits.Count == 0 && newIngest == null && !museGiven)
            throw Bad("no_changes", "nothing to change: send at least one of role, is_active, limits, reading_ingest, muse_id");
        if (id == actor.Id && (newRole != null || newActive != null))
            throw Self("cannot change your own role or disable yourself — guarantees at least one admin remains");

        var changed = false;
        await using (var c = await Db.Open(ct))
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            var (t, otherAdmins) = await LockTarget(c, tx, id, ct);
            if (t == null) throw Bad("not_found", "no such user", status: 404);
            var removesAdmin = t.Role == "admin" && t.Active && ((newRole != null && newRole != "admin") || newActive == false);
            if (removesAdmin && otherAdmins == 0) throw LastAdmin();

            var sets = new List<string>();
            var args = new List<object?>();
            var changes = new JsonObject();
            void Set(string col, object? val) { args.Add(val); sets.Add($"{col} = ${args.Count}"); }

            var roleChanged = newRole != null && newRole != t.Role;
            var deactivated = newActive == false && t.Active;
            if (roleChanged) { Set("role", newRole); changes["role"] = FromTo(t.Role, newRole); }
            if (newActive != null && newActive != t.Active) { Set("is_active", newActive.Value); changes["is_active"] = FromTo(t.Active, newActive); }
            var limitChanges = new JsonObject();
            foreach (var (col, val) in limits)
            {
                var cur = t.Limits[Array.IndexOf(LimitColumns, col)];
                if (cur == val) continue;
                Set(col, val);
                limitChanges[col] = FromTo(cur, val);   // null = system default
            }
            if (limitChanges.Count > 0) changes["limits"] = limitChanges;
            if (newIngest != null && newIngest != t.ReadingIngest) { Set("reading_ingest", newIngest); changes["reading_ingest"] = FromTo(t.ReadingIngest, newIngest); }
            if (museGiven && newMuse != t.MuseId) { Set("muse_id", newMuse); changes["muse_id"] = FromTo(t.MuseId, newMuse); }

            if (sets.Count > 0)
            {
                // role change / disable: bump token_version so outstanding access tokens die at once
                // (a role change only forces a token re-mint: refresh cookies stay valid and the next
                // refresh carries the new role; disabling also revokes the refresh tokens, like the CLI).
                if (roleChanged || deactivated) sets.Add("token_version = token_version + 1");
                sets.Add("updated_at = now()");
                args.Add(id);
                try { await Exec(c, tx, $"UPDATE agnes_auth.users SET {string.Join(", ", sets)} WHERE id = ${args.Count}", args.ToArray()); }
                catch (PostgresException e) when (e.SqlState == "23505" && e.ConstraintName == "users_muse_id_key")
                {
                    throw Bad("muse_id_taken", $"muse_id '{newMuse}' is already mapped to another account", "muse_id", 409);
                }
                if (deactivated)
                    changes["sessions_revoked"] = await Exec(c, tx,
                        "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), revoke_reason = 'admin' WHERE user_id = $1 AND revoked_at IS NULL", id);
                await AuditService.Write(c, tx, actor.Id, actor.Username, Http.ClientIp(ctx), "update_user", t.Id, t.Username,
                    new JsonObject { ["changes"] = changes });
                changed = true;
            }
            await tx.CommitAsync(ct);
        }
        AuthService.ForgetUserState();
        await Json.Write(ctx, 200, new JsonObject { ["user"] = await LoadUser(id, ct), ["changed"] = changed });
    }

    private static async Task ResetPassword(HttpContext ctx, long id)
    {
        var actor = await RequireAdmin(ctx);
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        if (id == actor.Id)
            throw Self("cannot reset your own password here; use 我的账户 → 修改口令 (POST /api/auth/change-password), which verifies your current password",
                new JsonObject { ["use"] = "/api/auth/change-password" });
        RejectUnknown(body, "newPassword", "new_password");
        var password = RequireString(body, body.ContainsKey("new_password") ? "new_password" : "newPassword", "weak_password");

        string? name;
        await using (var c = await Db.Open(ct))
        await using (var q = Db.Cmd(c, "SELECT username FROM agnes_auth.users WHERE id = $1", null, id))
            name = await q.ExecuteScalarAsync(ct) as string;
        if (name == null) throw Bad("not_found", "no such user", status: 404);
        var problem = Passwords.PolicyProblem(password, name);
        if (problem != null) throw Bad("weak_password", problem, "newPassword");
        var hash = Passwords.Hash(password);

        await using (var c = await Db.Open(ct))
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            bool wasLocked;
            await using (var q = Db.Cmd(c,
                "SELECT username, coalesce(locked_until > now(), false) FROM agnes_auth.users WHERE id = $1 FOR UPDATE", tx, id))
            await using (var r = await q.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) throw Bad("not_found", "no such user", status: 404);
                name = r.GetString(0);
                wasLocked = r.GetBoolean(1);
            }
            await Exec(c, tx,
                "UPDATE agnes_auth.users SET password_hash = $1, failed_logins = 0, locked_until = NULL, " +
                "token_version = token_version + 1, updated_at = now() WHERE id = $2", hash, id);
            var revoked = await Exec(c, tx,
                "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), revoke_reason = 'admin' WHERE user_id = $1 AND revoked_at IS NULL", id);
            await AuditService.Write(c, tx, actor.Id, actor.Username, Http.ClientIp(ctx), "reset_password", id, name,
                new JsonObject { ["sessions_invalidated"] = true, ["sessions_revoked"] = revoked, ["unlocked"] = wasLocked });
            await tx.CommitAsync(ct);
        }
        AuthService.ForgetUserState();
        await Json.Write(ctx, 200, new JsonObject { ["ok"] = true, ["user"] = await LoadUser(id, ct), ["sessions_invalidated"] = true });
    }

    private static async Task DeleteUser(HttpContext ctx, long id)
    {
        var actor = await RequireAdmin(ctx);
        var ct = ctx.RequestAborted;
        if (id == actor.Id) throw Self("cannot delete your own account — guarantees at least one admin remains");
        string name;
        await using (var c = await Db.Open(ct))
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            var (t, otherAdmins) = await LockTarget(c, tx, id, ct);
            if (t == null) throw Bad("not_found", "no such user", status: 404);
            if (t.Role == "admin" && t.Active && otherAdmins == 0) throw LastAdmin();
            name = t.Username;
            // dependent rows handled explicitly (the FKs would cascade / set null anyway)
            var usage = await Exec(c, tx, "DELETE FROM agnes_auth.usage_daily WHERE user_id = $1", id);
            var tokens = await Exec(c, tx, "DELETE FROM agnes_auth.refresh_tokens WHERE user_id = $1", id);
            await Exec(c, tx, "UPDATE agnes_auth.login_audit SET user_id = NULL WHERE user_id = $1", id);  // keep history (username stays)
            await Exec(c, tx, "DELETE FROM agnes_auth.users WHERE id = $1", id);
            await AuditService.Write(c, tx, actor.Id, actor.Username, Http.ClientIp(ctx), "delete_user", id, name,
                new JsonObject { ["role"] = t.Role, ["was_active"] = t.Active, ["usage_rows_deleted"] = usage, ["sessions_deleted"] = tokens });
            await tx.CommitAsync(ct);
        }
        AuthService.ForgetUserState();
        await Json.Write(ctx, 200, new JsonObject { ["ok"] = true, ["deleted"] = new JsonObject { ["id"] = id, ["username"] = name } });
    }

    private static async Task ListIpBlocks(HttpContext ctx)
    {
        await RequireAdmin(ctx);
        await using var c = await Db.Open(ctx.RequestAborted);
        await using var q = Db.Cmd(c,
            "SELECT host(ip), lockout_count, blocked_at, blocked_until, " +
            "blocked_at IS NOT NULL AND (blocked_until IS NULL OR blocked_until > now()), updated_at " +
            "FROM agnes_auth.ip_block ORDER BY updated_at DESC", null);
        await using var r = await q.ExecuteReaderAsync(ctx.RequestAborted);
        var rows = new JsonArray();
        while (await r.ReadAsync(ctx.RequestAborted))
            rows.Add(new JsonObject
            {
                ["ip"] = r.GetString(0),
                ["lockout_count"] = r.GetInt32(1),
                ["blocked"] = r.GetBoolean(4),          // currently banned (login refused with 403 ip_blocked)
                ["blocked_at"] = r.IsDBNull(2) ? null : Iso(r.GetDateTime(2)),
                ["blocked_until"] = r.IsDBNull(3) ? null : Iso(r.GetDateTime(3)),   // null + blocked = permanent
                ["updated_at"] = Iso(r.GetDateTime(5)),
            });
        await Json.Write(ctx, 200, new JsonObject { ["ip_blocks"] = rows });
    }

    /// <summary>Same effect as the CLI's --unblock-ip: drop the ip_block row (ban + lockout counter).</summary>
    private static async Task Unblock(HttpContext ctx, string ip)
    {
        var actor = await RequireAdmin(ctx);
        var ct = ctx.RequestAborted;
        if (!IPAddress.TryParse(ip, out var addr)) throw Bad("invalid_ip", "not a valid IP address", "ip");
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        var norm = addr.ToString();
        await using var c = await Db.Open(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        bool wasBlocked; int count;
        await using (var q = Db.Cmd(c,
            "DELETE FROM agnes_auth.ip_block WHERE ip = $1::inet RETURNING " +
            "blocked_at IS NOT NULL AND (blocked_until IS NULL OR blocked_until > now()), lockout_count", tx, norm))
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw Bad("not_found", $"no ip_block entry for {norm}", status: 404);
            wasBlocked = r.GetBoolean(0); count = r.GetInt32(1);
        }
        await AuditService.Write(c, tx, actor.Id, actor.Username, Http.ClientIp(ctx), "unblock_ip", null, null,
            new JsonObject { ["ip"] = norm, ["was_blocked"] = wasBlocked, ["lockout_count"] = count });
        await tx.CommitAsync(ct);
        await Json.Write(ctx, 200, new JsonObject { ["ok"] = true, ["ip"] = norm, ["was_blocked"] = wasBlocked });
    }

    private static async Task ListCalls(HttpContext ctx)
    {
        await RequireAdmin(ctx);
        var q = ctx.Request.Query;
        var limit = int.TryParse(q["limit"].FirstOrDefault(), out var n) ? Math.Clamp(n, 1, 500) : 100;
        long? uid = long.TryParse(q["user_id"].FirstOrDefault(), out var u2) ? u2 : null;
        var kind = q["kind"].FirstOrDefault();
        if (kind is not (null or "" or "text" or "image" or "video" or "analyze" or "ingest"))
            throw Bad("invalid_kind", "'kind' must be text, image, video, analyze or ingest", "kind");
        var rows = await CallLog.Recent(limit, uid, string.IsNullOrEmpty(kind) ? null : kind, ctx.RequestAborted);
        await Json.Write(ctx, 200, new JsonObject { ["calls"] = rows });
    }

    private static async Task ListAudit(HttpContext ctx)
    {
        await RequireAdmin(ctx);
        var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var n) ? Math.Clamp(n, 1, 500) : 100;
        await Json.Write(ctx, 200, new JsonObject { ["audit"] = await AuditService.Recent(limit, ctx.RequestAborted) });
    }
}
