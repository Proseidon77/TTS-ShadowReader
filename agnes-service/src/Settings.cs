using System.Security.Cryptography;

namespace AgnesService;

/// <summary>
/// Settings from the process environment, falling back to KEY=VALUE <c>.env</c> file(s)
/// (re-read when their mtime changes, so e.g. AGNES_API_KEY can be added without a restart).
/// Environment wins over files; with several files (--env-file … --env-file …) later files win.
/// Values are never logged.
/// </summary>
public static class Settings
{
    private sealed class EnvFileState
    {
        public required string Path;
        public DateTime Mtime = DateTime.MinValue;
        public Dictionary<string, string> Values = new();
    }

    private static readonly object Gate = new();
    private static List<EnvFileState> _files = new() { new EnvFileState { Path = ResolveDefaultEnvFile() } };

    public static IEnumerable<string> EnvFiles => _files.Select(f => f.Path);

    /// <summary>Default .env: $AGNES_ENV_FILE, else ./.env, else ../.env next to the dll (publish/ → project dir).</summary>
    public static string ResolveDefaultEnvFile()
    {
        var explicitPath = Environment.GetEnvironmentVariable("AGNES_ENV_FILE");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (File.Exists(cwd)) return cwd;
        var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".env"));
        return File.Exists(parent) ? parent : cwd;
    }

    /// <summary>Strip "--env-file PATH" pairs from args and use those files instead of the default.</summary>
    public static string[] ConsumeEnvFileArgs(string[] args)
    {
        var rest = new List<string>();
        var files = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--env-file" && i + 1 < args.Length) { files.Add(Path.GetFullPath(args[++i])); continue; }
            rest.Add(args[i]);
        }
        if (files.Count > 0)
            lock (Gate) _files = files.Select(f => new EnvFileState { Path = f }).ToList();
        return rest.ToArray();
    }

    public static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq].Trim();
            if (k.StartsWith("export ")) k = k[7..].Trim();
            var v = line[(eq + 1)..].Trim();
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0]) v = v[1..^1];
            values[k] = v;
        }
        return values;
    }

    private static Dictionary<string, string> Load(EnvFileState f)
    {
        DateTime mtime;
        try
        {
            if (!File.Exists(f.Path)) return new();
            mtime = File.GetLastWriteTimeUtc(f.Path);
        }
        catch { return new(); }
        if (mtime == f.Mtime) return f.Values;
        Dictionary<string, string> values;
        try { values = Parse(File.ReadAllLines(f.Path)); }
        catch { values = new(); }
        f.Values = values;
        f.Mtime = mtime;
        return values;
    }

    public static string Get(string name, string fallback = "")
    {
        var v = Environment.GetEnvironmentVariable(name)?.Trim();
        if (!string.IsNullOrEmpty(v)) return v;
        lock (Gate)
        {
            for (var i = _files.Count - 1; i >= 0; i--)
                if (Load(_files[i]).TryGetValue(name, out var f) && !string.IsNullOrWhiteSpace(f)) return f.Trim();
        }
        return fallback;
    }

    public static int GetInt(string name, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        var v = int.TryParse(Get(name), out var n) ? n : fallback;
        return Math.Clamp(v, min, max);
    }

    public static bool GetBool(string name, bool fallback = false) =>
        Get(name).ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };

    public static string AgnesApiKey => Get("AGNES_API_KEY");
    public static string JwtSecret => Get("JWT_SECRET");
    public static string AgnesBase => Get("AGNES_BASE_URL", "https://apihub.agnes-ai.com").TrimEnd('/');

    /// <summary>
    /// --init-env: create the default .env from .env.example if missing, chmod 600, generate JWT_SECRET
    /// if empty/short (written to the file, never printed) and default AGNES_DB_CONNECTION if absent.
    /// </summary>
    public static int InitEnv()
    {
        var path = _files[^1].Path;
        var dir = Path.GetDirectoryName(path)!;
        if (!File.Exists(path))
        {
            var example = new[] { Path.Combine(dir, ".env.example"), Path.Combine(AppContext.BaseDirectory, ".env.example") }
                .FirstOrDefault(File.Exists);
            File.WriteAllText(path, example != null ? File.ReadAllText(example) : "");
            Console.WriteLine($"created {path}" + (example != null ? $" from {example}" : ""));
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var lines = File.ReadAllLines(path).ToList();
        var cur = Parse(lines);
        if (!cur.TryGetValue("JWT_SECRET", out var s) || s.Length < AuthService.MinSecretLen)
        {
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var idx = lines.FindIndex(l => l.TrimStart().StartsWith("JWT_SECRET="));
            if (idx >= 0) lines[idx] = "JWT_SECRET=" + secret; else lines.Add("JWT_SECRET=" + secret);
            Console.WriteLine("generated JWT_SECRET (not shown)");
        }
        if (!cur.TryGetValue("AGNES_DB_CONNECTION", out var cs) || string.IsNullOrWhiteSpace(cs))
        {
            var line = "AGNES_DB_CONNECTION=Host=/var/run/postgresql;Database=db77database;Username=" + Environment.UserName;
            var idx = lines.FindIndex(l => l.TrimStart().StartsWith("AGNES_DB_CONNECTION="));
            if (idx >= 0) lines[idx] = line; else lines.Add(line);
            Console.WriteLine("set AGNES_DB_CONNECTION to local peer auth as OS user " + Environment.UserName);
        }
        File.WriteAllLines(path, lines);
        Console.WriteLine($"{path}: mode 600; AGNES_API_KEY {(string.IsNullOrEmpty(Parse(lines).GetValueOrDefault("AGNES_API_KEY")) ? "NOT set (generation will 503)" : "set")}");
        return 0;
    }
}
