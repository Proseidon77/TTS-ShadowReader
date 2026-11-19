using Npgsql;

namespace AgnesService;

/// <summary>
/// PostgreSQL access for schema agnes_auth. Connection string from AGNES_DB_CONNECTION (Npgsql
/// format), or — for .env files from the Python round — a simple libpq AGNES_DB_DSN
/// ("dbname=… host=… user=… password=… port=…") converted to Npgsql form. Nothing is hardcoded.
/// One pooled NpgsqlDataSource per distinct connection string.
/// </summary>
public static class Db
{
    private static readonly object Gate = new();
    private static string? _cs;
    private static NpgsqlDataSource? _ds;

    public static string ConnectionString()
    {
        var cs = Settings.Get("AGNES_DB_CONNECTION");
        if (!string.IsNullOrEmpty(cs)) return cs;
        var dsn = Settings.Get("AGNES_DB_DSN");
        return string.IsNullOrEmpty(dsn) ? "" : FromLibpq(dsn);
    }

    /// <summary>Convert "dbname=x user=y host=/var/run/postgresql" style conninfo.</summary>
    public static string FromLibpq(string dsn)
    {
        var b = new NpgsqlConnectionStringBuilder();
        foreach (var tok in dsn.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = tok.IndexOf('=');
            if (eq <= 0) continue;
            var k = tok[..eq].ToLowerInvariant();
            var v = tok[(eq + 1)..].Trim('\'');
            switch (k)
            {
                case "dbname": b.Database = v; break;
                case "host": b.Host = v; break;
                case "port": b.Port = int.TryParse(v, out var p) ? p : 5432; break;
                case "user": b.Username = v; break;
                case "password": b.Password = v; break;
                case "connect_timeout": b.Timeout = int.TryParse(v, out var t) ? t : 5; break;
            }
        }
        // libpq defaults: unix socket + OS user name (peer auth)
        if (string.IsNullOrEmpty(b.Host)) b.Host = "/var/run/postgresql";
        if (string.IsNullOrEmpty(b.Username)) b.Username = Environment.UserName;
        return b.ConnectionString;
    }

    public static bool Configured => !string.IsNullOrEmpty(ConnectionString());

    public static NpgsqlDataSource Source()
    {
        var cs = ConnectionString();
        if (string.IsNullOrEmpty(cs))
            throw new ApiError(503, "AGNES_DB_CONNECTION not configured") { Code = "db_unavailable" };
        lock (Gate)
        {
            if (_ds != null && _cs == cs) return _ds;
            var b = new NpgsqlConnectionStringBuilder(cs);
            if (!cs.Contains("Timeout", StringComparison.OrdinalIgnoreCase)) b.Timeout = 5;
            if (string.IsNullOrEmpty(b.ApplicationName)) b.ApplicationName = "agnes-service";
            _ds?.Dispose();
            _ds = NpgsqlDataSource.Create(b.ConnectionString);
            _cs = cs;
            return _ds;
        }
    }

    public static async Task<NpgsqlConnection> Open(CancellationToken ct = default)
    {
        var ds = Source();
        try { return await ds.OpenConnectionAsync(ct); }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            Log.Warn($"auth db connect failed: {e.GetType().Name}");
            throw new ApiError(503, "auth database unavailable") { Code = "db_unavailable" };
        }
    }

    public static NpgsqlCommand Cmd(NpgsqlConnection c, string sql, NpgsqlTransaction? tx, params object?[] args)
    {
        var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var a in args) cmd.Parameters.Add(a as NpgsqlParameter ?? new NpgsqlParameter { Value = a ?? DBNull.Value });   // typed (e.g. jsonb) passes through
        return cmd;
    }

    private static DateTime _statusAt = DateTime.MinValue;
    private static Dictionary<string, object?>? _status;

    /// <summary>For /health: dsn_configured / reachable / schema_ready (+ limits_ready), cached 30 s.</summary>
    public static async Task<Dictionary<string, object?>> Status()
    {
        var st = new Dictionary<string, object?>
        {
            ["driver"] = true, ["dsn_configured"] = Configured, ["reachable"] = null,
            ["schema_ready"] = null, ["limits_ready"] = null, ["admin_ready"] = null, ["calls_ready"] = null,
        };
        if (!Configured) return st;
        lock (Gate)
            if (_status != null && DateTime.UtcNow - _statusAt < TimeSpan.FromSeconds(30)) return _status;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var c = await Source().OpenConnectionAsync(cts.Token);
            st["reachable"] = true;
            await using var cmd = Cmd(c,
                "SELECT to_regclass('agnes_auth.users') IS NOT NULL AND to_regclass('agnes_auth.refresh_tokens') IS NOT NULL " +
                "AND to_regclass('agnes_auth.ip_block') IS NOT NULL AND to_regclass('agnes_auth.login_audit') IS NOT NULL, " +
                "to_regclass('agnes_auth.usage_daily') IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns " +
                "WHERE table_schema='agnes_auth' AND table_name='users' AND column_name='daily_quota'), " +
                "to_regclass('agnes_auth.admin_audit') IS NOT NULL, " +
                "to_regclass('agnes_auth.call_log') IS NOT NULL", null);
            await using var r = await cmd.ExecuteReaderAsync(cts.Token);
            if (await r.ReadAsync(cts.Token)) { st["schema_ready"] = r.GetBoolean(0); st["limits_ready"] = r.GetBoolean(1); st["admin_ready"] = r.GetBoolean(2); st["calls_ready"] = r.GetBoolean(3); }
        }
        catch (Exception e)
        {
            Log.Warn($"auth db health check failed: {e.GetType().Name}");
            st["reachable"] = false;
        }
        lock (Gate) { _status = st; _statusAt = DateTime.UtcNow; }
        return st;
    }
}

public static class Log
{
    private static void W(string level, string msg) =>
        Console.Out.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {msg}");
    public static void Info(string msg) => W("INFO", msg);
    public static void Warn(string msg) => W("WARNING", msg);
    public static void Error(string msg) => W("ERROR", msg);
}
