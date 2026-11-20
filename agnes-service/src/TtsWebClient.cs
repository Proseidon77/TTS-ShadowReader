using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AgnesService;

/// <summary>
/// Client for the reading service (TtsWeb, 8791): /share, /api/v1/articles/{id}, /api/v1/ingest/preview|commit,
/// /api/v1/ingest/readings_check. Base URL TTSWEB_URL (default http://127.0.0.1:8791), write token TTSWEB_TOKEN
/// (registered in TtsWeb tokens.json as "agnes-service", can_act_for A001/A002/A003; redacted everywhere).
/// Failure classes (fallback design E13–E16): T = unreachable / 5xx / 503 RETRY_LATER / timeout (retry, never L2);
/// auth = AUTH_FAILED 401/403 (write path broken → L2); conflict = 409 VERSION_CONFLICT (someone else finished);
/// gone = 404/410 NOT_FOUND; invalid = other 4xx (our input).
/// </summary>
public static class TtsWebClient
{
    public static string BaseUrl => Settings.Get("TTSWEB_URL", "http://127.0.0.1:8791").TrimEnd('/');
    public static string Token => Settings.Get("TTSWEB_TOKEN");
    public static double TimeoutS => Settings.GetInt("TTSWEB_TIMEOUT_S", 30, 2, 300);

    public sealed class TtsWebError : Exception
    {
        public required string Kind { get; init; }       // t | auth | conflict | gone | invalid
        public int Status { get; init; }
        public string? Code { get; init; }
        public JsonObject? Body { get; init; }
        public TtsWebError(string msg) : base(msg) { }
    }

    private static async Task<JsonObject> Send(HttpMethod method, string path, JsonNode? body, CancellationToken ct, double? timeout = null)
    {
        var token = Token;
        if (string.IsNullOrEmpty(token))
            throw new TtsWebError("TTSWEB_TOKEN not configured") { Kind = "auth", Code = "AUTH_FAILED" };
        using var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("agnes-service/" + Agnes.Version);
        if (body != null) req.Content = new StringContent(body.ToJsonString(Json.Options), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout ?? TimeoutS));
        int status;
        string raw;
        try
        {
            using var resp = await Agnes.Http.SendAsync(req, cts.Token);
            status = (int)resp.StatusCode;
            raw = await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TtsWebError($"TtsWeb {path.Split('?')[0]} timed out") { Kind = "t", Code = "timeout" };
        }
        catch (HttpRequestException e)
        {
            throw new TtsWebError("cannot reach TtsWeb: " + Upstream.Snip(e.Message)) { Kind = "t", Code = "unreachable" };
        }
        JsonObject? js = null;
        try { js = JsonNode.Parse(raw) as JsonObject; } catch { /* non-JSON */ }
        if (status is >= 200 and < 300 && js != null) return js;
        var code = js?["error"] is JsonObject eo ? (string?)eo["code"] : null;
        var msg = js?["error"] is JsonObject eo2 ? (string?)eo2["message"] : js?["error"]?.ToString();
        var kind = status switch
        {
            401 or 403 => "auth",
            409 => "conflict",
            404 or 410 => "gone",
            429 => "t",
            >= 500 => "t",
            >= 200 and < 300 => "t",   // 2xx but not JSON: treat as transient
            _ => "invalid",
        };
        throw new TtsWebError($"TtsWeb {path.Split('?')[0]} -> HTTP {status} {code} {Upstream.Snip(msg ?? raw)}")
            { Kind = kind, Status = status, Code = code ?? status.ToString(), Body = js };
    }

    /// <summary>POST /share (share-first): returns the short id. by = added_by (A00x on behalf; web for the web channel).</summary>
    public static async Task<string> Share(string text, string lang, string? rate, string? title, string? by, string addedVia, CancellationToken ct)
    {
        var body = new JsonObject { ["text"] = text, ["lang"] = lang, ["added_via"] = addedVia };
        if (!string.IsNullOrEmpty(rate)) body["rate"] = rate;
        if (!string.IsNullOrEmpty(title)) body["title"] = title;
        if (!string.IsNullOrEmpty(by)) body["by"] = by;
        var js = await Send(HttpMethod.Post, "/share", body, ct);
        return (string?)js["id"] ?? throw new TtsWebError("TtsWeb /share returned no id") { Kind = "t", Code = "shape" };
    }

    public static async Task<JsonObject> GetArticle(string id, CancellationToken ct)
    {
        var js = await Send(HttpMethod.Get, "/api/v1/articles/" + Uri.EscapeDataString(id), null, ct);
        return js["article"] as JsonObject ?? throw new TtsWebError("TtsWeb article response has no article") { Kind = "t", Code = "shape" };
    }

    // ---- 2026-11-20 delete / edit (DELETE-EDIT-REPORT.md): article management for the「添加文章」page

    /// <summary>GET /api/v1/articles (live articles, newest update first, ≤500; each with a `sentences` count).</summary>
    public static async Task<JsonArray> ListArticles(CancellationToken ct)
    {
        var js = await Send(HttpMethod.Get, "/api/v1/articles", null, ct, 15);
        return js["articles"] as JsonArray ?? throw new TtsWebError("TtsWeb article list has no articles") { Kind = "t", Code = "shape" };
    }

    /// <summary>DELETE /api/v1/articles/{id}: soft delete (deleted_at). 410 when already deleted (Kind gone).</summary>
    public static Task<JsonObject> DeleteArticle(string id, CancellationToken ct) =>
        Send(HttpMethod.Delete, "/api/v1/articles/" + Uri.EscapeDataString(id), null, ct, 15);

    /// <summary>PATCH /api/v1/articles/{id} {title?, lang?}: metadata only, no new version.</summary>
    public static async Task<JsonObject> PatchArticle(string id, string? title, string? lang, CancellationToken ct)
    {
        var body = new JsonObject();
        if (title != null) body["title"] = title;
        if (lang != null) body["lang"] = lang;
        var js = await Send(HttpMethod.Patch, "/api/v1/articles/" + Uri.EscapeDataString(id), body, ct, 15);
        return js["article"] as JsonObject ?? throw new TtsWebError("TtsWeb patch response has no article") { Kind = "t", Code = "shape" };
    }

    public static Task<JsonObject> Preview(string text, string lang, CancellationToken ct) =>
        Send(HttpMethod.Post, "/api/v1/ingest/preview", new JsonObject { ["text"] = text, ["lang"] = lang }, ct);

    public static Task<JsonObject> Commit(JsonObject body, CancellationToken ct) =>
        Send(HttpMethod.Post, "/api/v1/ingest/commit", body, ct, Math.Max(TimeoutS, 60));

    /// <summary>
    /// Layer-2 dictionary check (§5): one verdict per item (true supported / false not supported), or null when the
    /// endpoint is unavailable (old TtsWeb, error) — the caller then falls back to layer 1 only.
    /// </summary>
    public static async Task<List<bool?>?> ReadingsCheck(IReadOnlyList<FixTriage.Item> items, CancellationToken ct)
    {
        if (items.Count == 0 || !Settings.GetBool("INGEST_READINGS_CHECK", true)) return null;
        var arr = new JsonArray(items.Select(x => (JsonNode)new JsonObject { ["sentence"] = x.Sentence, ["tok"] = x.I, ["s"] = x.S, ["r"] = x.To }).ToArray());
        try
        {
            var js = await Send(HttpMethod.Post, "/api/v1/ingest/readings_check", new JsonObject { ["items"] = arr }, ct, 15);
            if (js["results"] is not JsonArray res || res.Count != items.Count) return null;
            return res.Select(r => r?["supported"] is JsonValue v && v.TryGetValue<bool>(out var b) ? (bool?)b : null).ToList();
        }
        catch (TtsWebError e)
        {
            Log.Warn($"readings_check unavailable: {e.Kind} {e.Code}");
            return null;
        }
    }
}
