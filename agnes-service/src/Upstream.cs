using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgnesService;

/// <summary>
/// Generic OpenAI-style upstream call (TASK6 B7), extracted from the former Agnes.Call: Bearer key,
/// JSON in/out, total-time timeout, 4xx passthrough / 5xx → 502 mapping, redacted error snippets.
/// Uses the shared <see cref="Agnes.Http"/> pool (no second HttpClient).
///
/// TASK6 phase 2 (P2R-4): every POST to a provider passes the per-provider token bucket
/// (<see cref="UpstreamLimiter"/>) first — Agnes' ~20 calls/min is one pool shared by text / image / video /
/// analyze / ingest. Interactive requests fail fast with 429 upstream_throttled; ingest jobs (marked with
/// <see cref="IngestScope"/>) wait, and additionally take from their own sub-bucket.
/// </summary>
public static partial class Upstream
{
    private const int ErrSnippet = 600;

    /// <summary>Names of settings whose values must never appear in logs, errors or call_log.</summary>
    public static readonly string[] SensitiveKeys = { "AGNES_API_KEY", "JWT_SECRET", "GEMINI_API_KEY", "TTSWEB_TOKEN" };

    public static string Redact(string text)
    {
        foreach (var name in SensitiveKeys)
        {
            var s = Settings.Get(name);
            if (!string.IsNullOrEmpty(s)) text = text.Replace(s, "***");
        }
        return text;
    }

    public static string Snip(string s) => Redact(s.Length > ErrSnippet ? s[..ErrSnippet] : s);

    /// <summary>Set (AsyncLocal) while an ingest job runs: upstream throttling waits instead of failing fast.</summary>
    public static readonly AsyncLocal<IngestCallScope?> IngestScope = new();

    public sealed class IngestCallScope
    {
        public DateTime WaitUntil = DateTime.MaxValue;   // give up waiting for a token after this (job deadline)
        public int ThrottleWaits;                          // how often the job had to wait (detail "tb=")
    }

    /// <summary>Bucket key of an upstream label ("Agnes API" → agnes, "Gemini API" → gemini), null = no bucket.</summary>
    public static string? BucketOf(string name) =>
        name.StartsWith("Agnes", StringComparison.OrdinalIgnoreCase) ? "agnes"
        : name.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase) ? "gemini" : null;

    /// <summary>
    /// Call an upstream. <paramref name="name"/> is the human label used in error messages
    /// (e.g. "Agnes API"); <paramref name="keyName"/> the setting name reported when the key is empty.
    /// The URL is <paramref name="baseUrl"/> + <paramref name="path"/> (path may be "" for a full endpoint URL).
    /// Timeout is the TOTAL time for the call.
    /// </summary>
    public static async Task<JsonObject> Call(string name, string baseUrl, string key, string keyName,
        HttpMethod method, string path, JsonNode? body, double timeoutSeconds, CancellationToken aborted)
    {
        if (string.IsNullOrEmpty(key)) throw new ApiError(503, $"{keyName} not configured");
        // P2R-4: generation calls (POST) take a token; GET polls (video status, model list) are not throttled.
        if (method == HttpMethod.Post && BucketOf(name) is { } bucket)
            await UpstreamLimiter.Acquire(bucket, name, IngestScope.Value, aborted);

        using var req = new HttpRequestMessage(method, baseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("agnes-service/" + Agnes.Version);
        if (body != null) req.Content = new StringContent(body.ToJsonString(Json.Options), Encoding.UTF8, "application/json");

        // never log the URL of a non-Agnes upstream (may be configured with a key in the query)
        var logPath = path.Length > 0 ? path.Split('?')[0] : $"<{name}>";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        string raw;
        try
        {
            resp = await Agnes.Http.SendAsync(req, cts.Token);
            raw = await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            Log.Warn($"upstream {method} {logPath} timed out after {sw.Elapsed.TotalSeconds:F1}s");
            throw new ApiError(504, $"{name} timed out after {(int)timeoutSeconds}s");
        }
        catch (HttpRequestException e)
        {
            throw new ApiError(502, $"Cannot reach {name}: " + Snip(e.Message));
        }
        var status = (int)resp.StatusCode;
        var retryAfter = RetryAfterOf(resp);
        resp.Dispose();
        if (status >= 400)
        {
            Log.Warn($"upstream {method} {logPath} -> HTTP {status} in {sw.Elapsed.TotalSeconds:F1}s");
            // Pass client-side errors through; map 5xx/other to 502.
            var outStatus = status is 400 or 401 or 402 or 403 or 404 or 409 or 413 or 422 or 429 ? status : 502;
            throw new ApiError(outStatus, $"{name} returned HTTP {status}")
                { UpstreamStatus = status, UpstreamBody = Snip(raw), RetryAfterSeconds = retryAfter ?? RetryDelayOf(raw) };
        }
        Log.Info($"upstream {method} {logPath} -> HTTP {status} in {sw.Elapsed.TotalSeconds:F1}s");
        try
        {
            if (JsonNode.Parse(raw) is JsonObject o) return o;
        }
        catch (Exception) { /* fall through */ }
        throw new ApiError(502, $"{name} returned non-JSON response") { UpstreamStatus = status, UpstreamBody = Snip(raw) };
    }

    private static double? RetryAfterOf(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is { } d) return d.TotalSeconds;
        if (ra?.Date is { } at) return Math.Max(0, (at - DateTimeOffset.UtcNow).TotalSeconds);
        return null;
    }

    [GeneratedRegex("\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"")] private static partial Regex RetryDelayRe();

    /// <summary>Gemini google.rpc.RetryInfo: {"@type": "...RetryInfo", "retryDelay": "37s"} anywhere in the error body.</summary>
    public static double? RetryDelayOf(string raw)
    {
        var m = RetryDelayRe().Match(raw);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }
}

/// <summary>
/// Per-provider sliding-window token buckets (TASK6 P2R-4), in memory (single instance):
///   UPSTREAM_RPM_AGNES=18   all Agnes generation calls (text/image/video/analyze/ingest), 60 s window
///   UPSTREAM_RPM_GEMINI=0   gemini (0 = no bucket; set to the project's RPM limit)
///   INGEST_UPSTREAM_RPM_AGNES=12   sub-quota for ingest jobs inside the agnes bucket (leaves room for interactive)
///   INGEST_UPSTREAM_RPM_GEMINI=0   same for gemini (0 = only the provider bucket applies)
/// Interactive callers: no token → 429 upstream_throttled (Retry-After). Ingest callers wait for a token
/// (both buckets) until their scope's deadline.
/// </summary>
public static class UpstreamLimiter
{
    private sealed class Window
    {
        public readonly Queue<DateTime> Hits = new();
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Window> Windows = new();

    public static int Rpm(string bucket) => bucket switch
    {
        "agnes" => Settings.GetInt("UPSTREAM_RPM_AGNES", 18, 0, 100000),
        "gemini" => Settings.GetInt("UPSTREAM_RPM_GEMINI", 0, 0, 100000),
        _ => 0,
    };

    public static int IngestRpm(string bucket) => bucket switch
    {
        "agnes" => Settings.GetInt("INGEST_UPSTREAM_RPM_AGNES", 12, 0, 100000),
        "gemini" => Settings.GetInt("INGEST_UPSTREAM_RPM_GEMINI", 0, 0, 100000),
        _ => 0,
    };

    private static Window W(string key)
    {
        if (!Windows.TryGetValue(key, out var w)) Windows[key] = w = new Window();
        return w;
    }

    private static void Prune(Window w, DateTime now)
    {
        while (w.Hits.Count > 0 && now - w.Hits.Peek() >= TimeSpan.FromSeconds(60)) w.Hits.Dequeue();
    }

    /// <summary>Try to take one token from bucket (+ ingest sub-bucket). Returns 0 on success, else seconds to wait.</summary>
    public static double TryTake(string bucket, bool ingest)
    {
        var rpm = Rpm(bucket);
        var sub = ingest ? IngestRpm(bucket) : 0;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            var w = W(bucket);
            var ws = W(bucket + ":ingest");
            Prune(w, now);
            Prune(ws, now);
            double wait = 0;
            if (rpm > 0 && w.Hits.Count >= rpm) wait = Math.Max(wait, (w.Hits.Peek().AddSeconds(60) - now).TotalSeconds);
            if (sub > 0 && ws.Hits.Count >= sub) wait = Math.Max(wait, (ws.Hits.Peek().AddSeconds(60) - now).TotalSeconds);
            if (wait > 0) return Math.Max(0.05, wait);
            if (rpm > 0) w.Hits.Enqueue(now);
            if (sub > 0) ws.Hits.Enqueue(now);
            return 0;
        }
    }

    public static async Task Acquire(string bucket, string label, Upstream.IngestCallScope? scope, CancellationToken ct)
    {
        var first = true;
        while (true)
        {
            var wait = TryTake(bucket, scope != null);
            if (wait <= 0) return;
            if (scope == null)
            {
                var secs = Math.Max(1, (int)Math.Ceiling(wait));
                throw new ApiError(429, $"{label}: upstream call budget is used up ({Rpm(bucket)}/min shared by all users); retry in {secs} s")
                {
                    Code = "upstream_throttled", Headers = { new("Retry-After", secs.ToString()) },
                    Extra = new JsonObject { ["retry_after"] = secs, ["bucket"] = bucket },
                };
            }
            if (first) { scope.ThrottleWaits++; first = false; }
            if (DateTime.UtcNow.AddSeconds(wait) > scope.WaitUntil)
                throw new ApiError(504, $"{label}: no upstream call budget before the ingest deadline") { Code = "upstream_throttled" };
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait + 0.05, 30)), ct);
        }
    }

    /// <summary>/health view: current usage per bucket (no secrets).</summary>
    public static JsonObject View()
    {
        var o = new JsonObject();
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            foreach (var b in new[] { "agnes", "gemini" })
            {
                var w = W(b); Prune(w, now);
                var ws = W(b + ":ingest"); Prune(ws, now);
                o[b] = new JsonObject
                {
                    ["rpm"] = Rpm(b), ["used_last_minute"] = w.Hits.Count,
                    ["ingest_rpm"] = IngestRpm(b), ["ingest_used_last_minute"] = ws.Hits.Count,
                };
            }
        }
        return o;
    }
}
