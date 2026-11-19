using System.Text;
using Npgsql;

namespace AgnesService;

/// <summary>
/// Operator CLI, run through the service binary (no HTTP surface):
///   dotnet publish/AgnesService.dll --create-user NAME [user|admin] [--rate-per-minute N] [--daily-quota N]
///                                   [--video-daily-quota N] [--max-concurrent N] [--password-stdin]
///   … --reset-password NAME [--password-stdin] | --disable NAME | --enable NAME | --unlock NAME
///   … --logout-all NAME | --list-users | --unblock-ip IP
/// Password is read from the terminal without echo (asked twice); --password-stdin reads one line from
/// stdin instead (scripted test accounts; never pass passwords as arguments).
/// </summary>
public static class UserCli
{
    public static readonly string[] Commands =
        { "--create-user", "--reset-password", "--disable", "--enable", "--unlock", "--logout-all", "--list-users", "--unblock-ip",
          "--set-reading-ingest", "--help-users" };

    public static bool IsCli(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    private const string Usage = """
        agnes-service user management (schema agnes_auth; DB from AGNES_DB_CONNECTION in env or .env)

          AgnesService --create-user NAME [user|admin] [limit options] [--password-stdin]
          AgnesService --reset-password NAME [--password-stdin]   new password, unlock, log out everywhere
          AgnesService --disable NAME | --enable NAME              (disable also logs out everywhere)
          AgnesService --unlock NAME                               clear failed-login lock
          AgnesService --logout-all NAME                           bump token_version + revoke refresh tokens
          AgnesService --list-users
          AgnesService --unblock-ip IP
          AgnesService --set-reading-ingest NAME none|web|agent [--muse-id A001|none]
                                                                   reading ingest permission (TASK6 phase 2; audited)

        limit options (per-account overrides; omitted = system default from LIMITS_DEFAULT_*):
          --rate-per-minute N   --daily-quota N   --video-daily-quota N   --max-concurrent N
        reading ingest options for --create-user (default none / no mapping):
          --reading-ingest none|web|agent   --muse-id A001
        usernames shaped like A001 are reserved for Muse agent ids and refused.
        Changing limits of an existing account: SQL, see README ("Per-account limits").
        """;

    public static async Task<int> Run(string[] args)
    {
        try { return await RunInner(args); }
        catch (CliError e) { Console.Error.WriteLine("error: " + e.Message); return 2; }
        catch (ApiError e) { Console.Error.WriteLine("error: " + e.Message); return 1; }
        catch (PostgresException e) { Console.Error.WriteLine($"error: database: {e.MessageText}"); return 1; }
    }

    private sealed class CliError(string m) : Exception(m);

    private static async Task<int> RunInner(string[] args)
    {
        var cmd = args[0];
        if (cmd == "--help-users") { Console.WriteLine(Usage); return 0; }
        var positional = new List<string>();
        var opts = new Dictionary<string, int>();
        var passwordStdin = false;
        string? readingIngest = null, museId = null;
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--password-stdin": passwordStdin = true; break;
                case "--reading-ingest":
                    if (i + 1 >= args.Length || !AdminApi.ReadingIngestValues.Contains(args[i + 1]))
                        throw new CliError("--reading-ingest needs none, web or agent");
                    readingIngest = args[++i]; break;
                case "--muse-id":
                    if (i + 1 >= args.Length || !(args[i + 1] == "none" || System.Text.RegularExpressions.Regex.IsMatch(args[i + 1], @"^A\d{3}$")))
                        throw new CliError("--muse-id needs A001-style id (or none to clear)");
                    museId = args[++i]; break;
                case "--rate-per-minute" or "--daily-quota" or "--video-daily-quota" or "--max-concurrent":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var n) || n < 0)
                        throw new CliError($"{a} needs a non-negative integer");
                    opts[a] = n; i++; break;
                default:
                    if (a.StartsWith("--")) throw new CliError($"unknown option {a}\n\n{Usage}");
                    positional.Add(a); break;
            }
        }
        if (!Db.Configured) throw new CliError("AGNES_DB_CONNECTION not set (environment or .env)");
        await using var c = await Db.Open();

        if (cmd == "--list-users")
        {
            await using var q = Db.Cmd(c,
                "SELECT id, username, role, is_active, failed_logins, locked_until, last_login_at, " +
                "rate_per_minute, daily_quota, video_daily_quota, max_concurrent, reading_ingest, coalesce(muse_id, '-') FROM agnes_auth.users ORDER BY id", null);
            await using var r = await q.ExecuteReaderAsync();
            Console.WriteLine($"{"id",-4} {"username",-20} {"role",-6} {"active",-6} {"fails",-5} {"locked_until",-25} {"last_login_at",-25} limits(min/day/video/conc; - = default)  ingest/muse");
            while (await r.ReadAsync())
            {
                string N(int i) => r.IsDBNull(i) ? "-"
                    : r.GetValue(i) is DateTime dt ? dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'") : r.GetValue(i).ToString()!;
                Console.WriteLine($"{r.GetInt64(0),-4} {r.GetString(1),-20} {r.GetString(2),-6} {r.GetBoolean(3),-6} {r.GetInt32(4),-5} {N(5),-25} {N(6),-25} {N(7)}/{N(8)}/{N(9)}/{N(10)}  {N(11)}/{N(12)}");
            }
            return 0;
        }
        if (cmd == "--unblock-ip")
        {
            if (positional.Count != 1) throw new CliError("usage: --unblock-ip IP");
            await using var q = Db.Cmd(c, "DELETE FROM agnes_auth.ip_block WHERE ip = $1::inet", null, positional[0]);
            Console.WriteLine($"removed {await q.ExecuteNonQueryAsync()} ip_block row(s) for {positional[0]}");
            return 0;
        }

        if (positional.Count < 1) throw new CliError($"USERNAME is required\n\n{Usage}");
        var username = positional[0];
        if (!AuthService.UsernameRe().IsMatch(username)) throw new CliError("username must match ^[a-zA-Z0-9_.-]{3,32}$");
        long? uid = null; string? existing = null;
        await using (var q = Db.Cmd(c, "SELECT id, username FROM agnes_auth.users WHERE lower(username) = lower($1)", null, username))
        await using (var r = await q.ExecuteReaderAsync())
            if (await r.ReadAsync()) { uid = r.GetInt64(0); existing = r.GetString(1); }

        long NeedUser() => uid ?? throw new CliError($"no such user: {username}");

        async Task RevokeAll(NpgsqlTransaction tx, long id, string reason)
        {
            await using (var q = Db.Cmd(c, "UPDATE agnes_auth.users SET token_version = token_version + 1, updated_at = now() WHERE id = $1", tx, id))
                await q.ExecuteNonQueryAsync();
            await using (var q = Db.Cmd(c, "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), revoke_reason = $1 WHERE user_id = $2 AND revoked_at IS NULL", tx, reason, id))
                await q.ExecuteNonQueryAsync();
        }

        async Task Exec(NpgsqlTransaction? tx, string sql, params object?[] a)
        {
            await using var q = Db.Cmd(c, sql, tx, a);
            await q.ExecuteNonQueryAsync();
        }

        if (opts.Count > 0 && cmd != "--create-user")
            throw new CliError("limit options only apply to --create-user; change existing accounts with SQL (README)");
        if (readingIngest != null && cmd != "--create-user")
            throw new CliError("--reading-ingest only applies to --create-user (use --set-reading-ingest NAME VALUE)");
        if (museId != null && cmd is not ("--create-user" or "--set-reading-ingest"))
            throw new CliError("--muse-id only applies to --create-user / --set-reading-ingest");

        switch (cmd)
        {
            case "--create-user":
            {
                if (positional.Count > 2) throw new CliError("usage: --create-user NAME [user|admin]");
                var role = positional.Count == 2 ? positional[1] : "user";
                if (!AuthService.Roles.Contains(role)) throw new CliError("role must be user or admin");
                if (AdminApi.ReservedUsername(username)) throw new CliError("usernames like A001 are reserved for Muse agent ids");
                if (uid != null) throw new CliError($"user {existing} already exists (use --reset-password)");
                var hash = Passwords.Hash(ReadPassword(passwordStdin, username));
                int? O(string k) => opts.TryGetValue(k, out var v) ? v : null;
                object? id;
                await using (var tx0 = await c.BeginTransactionAsync())
                {
                    try
                    {
                        if (readingIngest == null && museId == null)
                        {
                            await using var q = Db.Cmd(c,
                                "INSERT INTO agnes_auth.users (username, password_hash, role, rate_per_minute, daily_quota, video_daily_quota, max_concurrent) " +
                                "VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id", tx0,
                                username, hash, role, O("--rate-per-minute"), O("--daily-quota"), O("--video-daily-quota"), O("--max-concurrent"));
                            id = await q.ExecuteScalarAsync();
                        }
                        else
                        {
                            await using var q = Db.Cmd(c,
                                "INSERT INTO agnes_auth.users (username, password_hash, role, rate_per_minute, daily_quota, video_daily_quota, max_concurrent, reading_ingest, muse_id) " +
                                "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING id", tx0,
                                username, hash, role, O("--rate-per-minute"), O("--daily-quota"), O("--video-daily-quota"), O("--max-concurrent"),
                                readingIngest ?? "none", museId is null or "none" ? null : museId);
                            id = await q.ExecuteScalarAsync();
                            await AuditService.Write(c, tx0, null, "cli:" + Environment.UserName, null, "create_user", (long)id!, username,
                                new System.Text.Json.Nodes.JsonObject { ["role"] = role, ["reading_ingest"] = readingIngest ?? "none", ["muse_id"] = museId is null or "none" ? null : museId });
                        }
                        await tx0.CommitAsync();
                    }
                    catch (PostgresException e) when (e.SqlState == "23505" && e.ConstraintName == "users_muse_id_key")
                    {
                        throw new CliError($"muse_id {museId} is already mapped to another account");
                    }
                }
                string L(string k) => opts.TryGetValue(k, out var v) ? v.ToString() : "default";
                Console.WriteLine($"created user {username} (id {id}, role {role}; limits: per_minute={L("--rate-per-minute")} " +
                                  $"daily={L("--daily-quota")} video_daily={L("--video-daily-quota")} concurrent={L("--max-concurrent")})");
                return 0;
            }
            case "--set-reading-ingest":
            {
                // TASK6 phase 2 (P2R-17): grant / revoke reading ingest without the web UI; audited like the admin API
                if (positional.Count != 2 || !AdminApi.ReadingIngestValues.Contains(positional[1]))
                    throw new CliError("usage: --set-reading-ingest NAME none|web|agent [--muse-id A001|none]");
                var id = NeedUser();
                var value = positional[1];
                await using var tx = await c.BeginTransactionAsync();
                string fromIngest; string? fromMuse;
                await using (var q = Db.Cmd(c, "SELECT reading_ingest, muse_id FROM agnes_auth.users WHERE id = $1 FOR UPDATE", tx, id))
                await using (var r = await q.ExecuteReaderAsync())
                {
                    await r.ReadAsync();
                    fromIngest = r.GetString(0); fromMuse = r.IsDBNull(1) ? null : r.GetString(1);
                }
                var toMuse = museId == null ? fromMuse : museId == "none" ? null : museId;
                try
                {
                    await Exec(tx, "UPDATE agnes_auth.users SET reading_ingest = $1, muse_id = $2, updated_at = now() WHERE id = $3", value, toMuse, id);
                }
                catch (PostgresException e) when (e.SqlState == "23505")
                {
                    throw new CliError($"muse_id {museId} is already mapped to another account");
                }
                var changes = new System.Text.Json.Nodes.JsonObject();
                if (fromIngest != value) changes["reading_ingest"] = new System.Text.Json.Nodes.JsonObject { ["from"] = fromIngest, ["to"] = value };
                if (fromMuse != toMuse) changes["muse_id"] = new System.Text.Json.Nodes.JsonObject { ["from"] = fromMuse, ["to"] = toMuse };
                await AuditService.Write(c, tx, null, "cli:" + Environment.UserName, null, "update_user", id, existing,
                    new System.Text.Json.Nodes.JsonObject { ["changes"] = changes });
                await tx.CommitAsync();
                Console.WriteLine($"{existing}: reading_ingest {fromIngest} -> {value}, muse_id {fromMuse ?? "-"} -> {toMuse ?? "-"} (audited)");
                return 0;
            }
            case "--reset-password":
            {
                var id = NeedUser();
                var hash = Passwords.Hash(ReadPassword(passwordStdin, existing));
                await using var tx = await c.BeginTransactionAsync();
                await Exec(tx, "UPDATE agnes_auth.users SET password_hash = $1, failed_logins = 0, locked_until = NULL, updated_at = now() WHERE id = $2", hash, id);
                await RevokeAll(tx, id, "admin");
                await tx.CommitAsync();
                Console.WriteLine($"password reset for {existing} (all sessions logged out)");
                return 0;
            }
            case "--disable" or "--enable":
            {
                var id = NeedUser();
                await using var tx = await c.BeginTransactionAsync();
                await Exec(tx, "UPDATE agnes_auth.users SET is_active = $1, updated_at = now() WHERE id = $2", cmd == "--enable", id);
                if (cmd == "--disable") await RevokeAll(tx, id, "admin");
                await tx.CommitAsync();
                Console.WriteLine($"{existing} {(cmd == "--enable" ? "enabled" : "disabled")}");
                return 0;
            }
            case "--unlock":
                await Exec(null, "UPDATE agnes_auth.users SET failed_logins = 0, locked_until = NULL, updated_at = now() WHERE id = $1", NeedUser());
                Console.WriteLine($"{existing} unlocked");
                return 0;
            case "--logout-all":
            {
                var id = NeedUser();
                await using var tx = await c.BeginTransactionAsync();
                await RevokeAll(tx, id, "admin");
                await tx.CommitAsync();
                Console.WriteLine($"{existing} logged out everywhere");
                return 0;
            }
        }
        throw new CliError(Usage);
    }

    private static string ReadPassword(bool fromStdin, string? username)
    {
        string pw;
        if (fromStdin || Console.IsInputRedirected)
        {
            if (!fromStdin) throw new CliError("stdin is not a terminal; use --password-stdin to read the password from stdin");
            pw = (Console.In.ReadLine() ?? "").TrimEnd('\r', '\n');
        }
        else
        {
            pw = Prompt("Password: ");
            if (Prompt("Repeat password: ") != pw) throw new CliError("passwords do not match");
        }
        var problem = Passwords.PolicyProblem(pw, username);
        if (problem != null) throw new CliError(problem);
        return pw;
    }

    /// <summary>Read a line from the terminal without echo.</summary>
    private static string Prompt(string label)
    {
        Console.Error.Write(label);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (k.Modifiers.HasFlag(ConsoleModifiers.Control) && k.Key == ConsoleKey.C) throw new CliError("aborted");
            if (k.KeyChar != '\0') sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }
}
