using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgnesService;

/// <summary>An error that maps to a JSON body <c>{"error": ..., "code"?, ...}</c> with an HTTP status.</summary>
public sealed class ApiError : Exception
{
    public int Status { get; }
    public string? Code { get; init; }
    public int? UpstreamStatus { get; init; }
    public string? UpstreamBody { get; init; }
    /// <summary>Upstream cool-down hint (seconds) from Retry-After or Gemini RetryInfo.retryDelay (TASK6 P2 circuit).</summary>
    public double? RetryAfterSeconds { get; init; }
    public List<KeyValuePair<string, string>> Headers { get; init; } = new();
    public JsonObject? Extra { get; init; }
    /// <summary>When set, the body is exactly this object (used for 429 limit bodies).</summary>
    public JsonObject? Body { get; init; }

    public ApiError(int status, string message) : base(message) { Status = status; }

    public JsonObject Payload()
    {
        if (Body != null) return (JsonObject)Body.DeepClone();
        var p = new JsonObject { ["error"] = Message };
        if (Code != null) p["code"] = Code;
        if (UpstreamStatus != null) p["upstream_status"] = UpstreamStatus;
        if (!string.IsNullOrEmpty(UpstreamBody)) p["upstream_body"] = UpstreamBody;
        if (Extra != null)
            foreach (var kv in Extra) p[kv.Key] = kv.Value?.DeepClone();
        return p;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        // like Python's ensure_ascii=False: Chinese text stays readable; still valid JSON
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public static async Task Write(HttpContext ctx, int status, JsonNode body,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        if (headers != null)
            foreach (var h in headers) ctx.Response.Headers.Append(h.Key, h.Value);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(body.ToJsonString(Options));
    }

    /// <summary>Read a JSON object body with a size cap. Mirrors the Python _json_body.</summary>
    public static async Task<JsonObject> ReadBody(HttpContext ctx, long maxLen, bool allowEmpty = false)
    {
        var len = ctx.Request.ContentLength;
        if (len is > 0 && len > maxLen) throw new ApiError(413, "request body too large");
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
        {
            if (ms.Length + n > maxLen) throw new ApiError(413, "request body too large");
            ms.Write(buf, 0, n);
        }
        if (ms.Length == 0)
        {
            if (allowEmpty) return new JsonObject();
            throw new ApiError(400, "JSON body required");
        }
        JsonNode? node;
        try { node = JsonNode.Parse(ms.ToArray()); }
        catch (Exception) { throw new ApiError(400, "body must be valid JSON"); }
        return node as JsonObject ?? throw new ApiError(400, "body must be a JSON object");
    }
}
