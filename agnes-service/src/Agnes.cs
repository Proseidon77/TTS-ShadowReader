using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgnesService;

/// <summary>Agnes AI upstream client + request handlers. Port of python-legacy/server.py.</summary>
public static partial class Agnes
{
    public const string Version = "3.0.0";
    public const int MaxPrompt = 8000;
    public const long MaxBody = 20L * 1024 * 1024;   // allows data-URI images for image-to-image

    public static double TextTimeout => Settings.GetInt("TEXT_TIMEOUT", 300, 5);
    public static double ImageTimeout => Settings.GetInt("IMAGE_TIMEOUT", 360, 5);
    public static double VideoCreateTimeout => Settings.GetInt("VIDEO_CREATE_TIMEOUT", 120, 5);
    public static double VideoStatusTimeout => Settings.GetInt("VIDEO_STATUS_TIMEOUT", 30, 5);
    private const double ModelsTimeout = 15;

    public static readonly Dictionary<string, string[]> KnownModels = new()
    {
        ["text"] = new[] { "agnes-2.5-flash", "agnes-2.5-pro", "agnes-2.5-pro-alpha", "agnes-2.5-pro-beta", "agnes-3.0-flash" },
        ["image"] = new[] { "agnes-image-2.5-flash", "agnes-image-2.1-flash", "agnes-image-2.0-flash" },
        ["video"] = new[] { "agnes-video-2.5-flash", "agnes-video-2.5" },
    };
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["text"] = "agnes-2.5-flash", ["image"] = "agnes-image-2.5-flash", ["video"] = "agnes-video-2.5-flash",
    };

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,200}$")] public static partial Regex VideoIdRe();
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,100}$")] public static partial Regex ModelRe();
    [GeneratedRegex(@"^[A-Za-z0-9:x.\-]{1,20}$")] private static partial Regex ShortRe();

    /// <summary>Shared upstream connection pool (Agnes and the analyze providers, see Upstream.Call).</summary>
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
    }) { Timeout = Timeout.InfiniteTimeSpan };   // per-call timeout via CancellationTokenSource

    /// <summary>Redact every sensitive setting value (Upstream.SensitiveKeys).</summary>
    public static string Redact(string text) => Upstream.Redact(text);

    private static string Snip(string s) => Upstream.Snip(s);

    /// <summary>Call Agnes. Timeout is the TOTAL time for the call (the Python version used per-socket-op timeouts).</summary>
    public static Task<JsonObject> Call(HttpMethod method, string path, JsonNode? body, double timeoutSeconds, CancellationToken aborted) =>
        Upstream.Call("Agnes API", Settings.AgnesBase, Settings.AgnesApiKey, "AGNES_API_KEY", method, path, body, timeoutSeconds, aborted);

    // ---------------------------------------------------------------- input helpers

    private static string? ReqStr(JsonObject body, string name, bool required = false, int maxLen = MaxPrompt, Regex? pattern = null)
    {
        var node = body[name];
        if (node == null || (node is JsonValue v0 && v0.TryGetValue<string>(out var s0) && string.IsNullOrWhiteSpace(s0)))
        {
            if (required) throw new ApiError(400, $"'{name}' is required");
            return null;
        }
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
            throw new ApiError(400, $"'{name}' must be a string");
        s = s.Trim();
        if (s.Length > maxLen) throw new ApiError(400, $"'{name}' is too long (max {maxLen} chars)");
        if (pattern != null && !pattern.IsMatch(s)) throw new ApiError(400, $"'{name}' has an invalid value");
        return s;
    }

    private static string PickModel(JsonObject body, string kind) => ReqStr(body, "model", pattern: ModelRe()) ?? Defaults[kind];

    private static string? OptUrl(JsonNode? node, string name)
    {
        if (node == null) return null;
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
            throw new ApiError(400, $"'{name}' must be a URL or data URI string");
        if (s == "") return null;
        if (!(s.StartsWith("http://") || s.StartsWith("https://") || s.StartsWith("data:")))
            throw new ApiError(400, $"'{name}' must start with http(s):// or data:");
        return s;
    }

    private static JsonArray? OptUrlList(JsonNode? node, string name)
    {
        if (node == null) return null;
        if (node is JsonValue) { var one = OptUrl(node, name); return one == null ? null : new JsonArray(one); }
        if (node is not JsonArray arr) throw new ApiError(400, $"'{name}' must be a list of URLs");
        var outList = new JsonArray();
        foreach (var x in arr) { var u = OptUrl(x, name); if (u != null) outList.Add(u); }
        return outList.Count == 0 ? null : outList;
    }

    private static bool IsNumber(JsonNode? n) => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number;

    private static bool TryInt(JsonNode? n, out long value)
    {
        value = 0;
        return n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && v.TryGetValue(out value);
    }

    private static ApiError Shape(string what, JsonNode res) =>
        new(502, $"Unexpected response shape from Agnes {what} API") { UpstreamStatus = 200, UpstreamBody = Snip(res.ToJsonString()) };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString();

    // ---------------------------------------------------------------- models

    private static readonly object ModelsGate = new();
    private static (DateTime at, JsonObject data)? _modelsCache;

    public static async Task<JsonObject> Models(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(Settings.AgnesApiKey))
        {
            lock (ModelsGate)
                if (_modelsCache is { } m && DateTime.UtcNow - m.at < TimeSpan.FromMinutes(10)) return (JsonObject)m.data.DeepClone();
            try
            {
                var res = await Call(HttpMethod.Get, "/v1/models", null, ModelsTimeout, ct);
                var ids = (res["data"] as JsonArray ?? new JsonArray())
                    .Select(x => Str(x?["id"])).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (ids.Count > 0)
                {
                    var grouped = new Dictionary<string, List<string>> { ["text"] = new(), ["image"] = new(), ["video"] = new() };
                    foreach (var id in ids)
                        grouped[id!.Contains("video") ? "video" : id.Contains("image") ? "image" : "text"].Add(id);
                    var models = new JsonObject();
                    foreach (var (k, list) in grouped)
                    {
                        if (list.Remove(Defaults[k])) list.Insert(0, Defaults[k]);   // preferred default first
                        models[k] = new JsonArray(list.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
                    }
                    var data = new JsonObject { ["source"] = "agnes", ["models"] = models, ["defaults"] = DefaultsJson() };
                    lock (ModelsGate) _modelsCache = (DateTime.UtcNow, data);
                    return (JsonObject)data.DeepClone();
                }
            }
            catch (ApiError e) { Log.Warn($"model list proxy failed ({e.Message}); using static list"); }
        }
        var st = new JsonObject();
        foreach (var (k, list) in KnownModels) st[k] = new JsonArray(list.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        return new JsonObject { ["source"] = "static", ["models"] = st, ["defaults"] = DefaultsJson() };
    }

    private static JsonObject DefaultsJson()
    {
        var d = new JsonObject();
        foreach (var (k, v) in Defaults) d[k] = v;
        return d;
    }

    // ---------------------------------------------------------------- text

    /// <summary>Validate + build the upstream payload (no side effects, so 400s cost no quota).</summary>
    public static (JsonObject payload, string model) BuildText(JsonObject body)
    {
        var prompt = ReqStr(body, "prompt", required: true)!;
        var system = ReqStr(body, "system");
        var model = PickModel(body, "text");
        var messages = new JsonArray();
        if (system != null) messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
        var payload = new JsonObject { ["model"] = model, ["messages"] = messages };
        foreach (var k in new[] { "temperature", "top_p" })
            if (IsNumber(body[k])) payload[k] = body[k]!.DeepClone();
        if (TryInt(body["max_tokens"], out var mt) && mt > 0 && mt <= 65536) payload["max_tokens"] = mt;
        return (payload, model);
    }

    public static async Task<JsonObject> DoText(JsonObject payload, string model, CancellationToken ct)
    {
        var res = await Call(HttpMethod.Post, "/v1/chat/completions", payload, TextTimeout, ct);
        if (res["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) throw Shape("chat", res);
        var msg = choice["message"] as JsonObject ?? new JsonObject();
        var outObj = new JsonObject { ["text"] = Str(msg["content"]) ?? "", ["model"] = Str(res["model"]) ?? model };
        if (!string.IsNullOrEmpty(Str(msg["reasoning_content"]))) outObj["reasoning"] = Str(msg["reasoning_content"]);
        if (res["usage"] is JsonObject u && u.Count > 0) outObj["usage"] = u.DeepClone();
        if (!string.IsNullOrEmpty(Str(choice["finish_reason"]))) outObj["finish_reason"] = Str(choice["finish_reason"]);
        return outObj;
    }

    // ---------------------------------------------------------------- image

    public sealed record ImageReq(JsonObject Payload, string Model, string Size, string? Ratio);

    public static ImageReq BuildImage(JsonObject body)
    {
        var prompt = ReqStr(body, "prompt", required: true)!;
        var model = PickModel(body, "image");
        var size = ReqStr(body, "size", pattern: ShortRe()) ?? "1K";
        var ratio = ReqStr(body, "ratio", pattern: ShortRe());
        var fmt = ReqStr(body, "response_format") ?? "url";
        if (fmt is not ("url" or "b64_json")) throw new ApiError(400, "'response_format' must be 'url' or 'b64_json'");
        var extra = new JsonObject { ["response_format"] = fmt };   // MUST be inside extra_body (verified by operator)
        JsonNode? image = body["image"] is JsonArray ? OptUrlList(body["image"], "image") : OptUrl(body["image"], "image");
        if (image != null) extra["image"] = image;                 // image-to-image input
        var payload = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["size"] = size, ["extra_body"] = extra };
        if (ratio != null) payload["ratio"] = ratio;
        return new ImageReq(payload, model, size, ratio);
    }

    public static async Task<JsonObject> DoImage(ImageReq r, CancellationToken ct)
    {
        var res = await Call(HttpMethod.Post, "/v1/images/generations", r.Payload, ImageTimeout, ct);
        if (res["data"] is not JsonArray { Count: > 0 } data || data[0] is not JsonObject item) throw Shape("image", res);
        var outObj = new JsonObject { ["model"] = r.Model, ["size"] = r.Size };
        if (r.Ratio != null) outObj["ratio"] = r.Ratio;
        if (!string.IsNullOrEmpty(Str(item["url"]))) outObj["url"] = Str(item["url"]);
        if (!string.IsNullOrEmpty(Str(item["b64_json"]))) outObj["b64"] = Str(item["b64_json"]);
        if (!string.IsNullOrEmpty(Str(item["revised_prompt"]))) outObj["revised_prompt"] = Str(item["revised_prompt"]);
        if (!outObj.ContainsKey("url") && !outObj.ContainsKey("b64"))
            throw new ApiError(502, "Agnes image API returned no url/b64_json") { UpstreamStatus = 200, UpstreamBody = Snip(res.ToJsonString()) };
        return outObj;
    }

    // ---------------------------------------------------------------- video

    private static readonly object VideoGate = new();
    private static readonly Dictionary<string, string> VideoModels = new();   // video_id -> model

    private static JsonObject VideoView(JsonObject res, string? fallbackId, string? model)
    {
        var errNode = res["error"];
        string? err = errNode is JsonObject eo ? (Str(eo["message"]) ?? Trunc(eo.ToJsonString(), 300)) : Str(errNode);
        var status = Str(res["status"]) ?? "unknown";
        var outObj = new JsonObject
        {
            ["id"] = Str(res["video_id"]) ?? fallbackId ?? Str(res["id"]),
            ["task_id"] = Str(res["task_id"]) ?? Str(res["id"]),
            ["model"] = Str(res["model"]) ?? model,
            ["status"] = status,
            ["progress"] = res["progress"]?.DeepClone(),
            ["url"] = status == "completed" ? Str(res["url"]) : null,
            ["error"] = string.IsNullOrEmpty(err) ? null : err,
        };
        foreach (var k in new[] { "seconds", "size", "created_at", "completed_at", "expires_at" })
            if (res[k] != null) outObj[k] = res[k]!.DeepClone();
        return outObj;
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    public static (JsonObject payload, string model) BuildVideo(JsonObject body)
    {
        var prompt = ReqStr(body, "prompt", required: true)!;
        var model = PickModel(body, "video");
        var flash = model.EndsWith("-flash");
        var payload = new JsonObject { ["model"] = model, ["prompt"] = prompt };
        var secNode = body["seconds"];
        var secStr = secNode == null ? null : Str(secNode);
        if (!string.IsNullOrEmpty(secStr))
        {
            if (!int.TryParse(secStr, out var s)) throw new ApiError(400, "'seconds' must be an integer 4-12");
            if (s is < 4 or > 12) throw new ApiError(400, "'seconds' must be between 4 and 12");
            payload["seconds"] = s.ToString();   // upstream wants a string
        }
        else payload["seconds"] = "5";
        var size = ReqStr(body, "size", pattern: ShortRe()) ?? "720P";
        if (size is not ("720P" or "1080P" or "1K" or "2K")) throw new ApiError(400, "'size' must be one of 720P, 1080P, 1K, 2K");
        if (flash && size != "720P") throw new ApiError(400, $"{model} only supports size 720P");
        payload["size"] = size;
        var ar = ReqStr(body, "aspect_ratio", pattern: ShortRe());
        if (ar != null)
        {
            if (ar is not ("21:9" or "16:9" or "4:3" or "1:1" or "3:4" or "9:16"))
                throw new ApiError(400, "'aspect_ratio' must be one of 21:9 16:9 4:3 1:1 3:4 9:16");
            payload["aspect_ratio"] = ar;
        }
        if (TryInt(body["seed"], out var seed)) payload["seed"] = seed;

        var first = OptUrl(body["first_frame"], "first_frame");
        var last = OptUrl(body["last_frame"], "last_frame");
        var images = OptUrlList(body["images"], "images");
        var audios = OptUrlList(body["audios"], "audios");
        JsonArray? videos = null;
        if (body["videos"] is JsonArray va && va.Count > 0)
        {
            videos = new JsonArray();
            foreach (var v in va)
            {
                if (v is not JsonObject vo || string.IsNullOrEmpty(Str(vo["url"])))
                    throw new ApiError(400, "'videos' must be a list of {url, start_seconds?, require_audio?}");
                var clean = new JsonObject();
                foreach (var k in new[] { "url", "start_seconds", "require_audio" })
                    if (vo.ContainsKey(k)) clean[k] = vo[k]?.DeepClone();
                videos.Add(clean);
            }
        }
        else if (body["videos"] is not null and not JsonArray)
            throw new ApiError(400, "'videos' must be a list of {url, start_seconds?, require_audio?}");

        var mode = ReqStr(body, "mode")
                   ?? (first != null || last != null ? "keyframe" : images != null || audios != null || videos != null ? "reference" : "text");
        if (mode is not ("text" or "keyframe" or "reference")) throw new ApiError(400, "'mode' must be text, keyframe or reference");
        payload["mode"] = mode;
        if (mode == "keyframe")
        {
            if (first == null && last == null) throw new ApiError(400, "keyframe mode needs first_frame and/or last_frame");
            if (first != null) payload["first_frame"] = first;
            if (last != null) payload["last_frame"] = last;
        }
        else if (mode == "reference")
        {
            if (images == null && audios == null && videos == null) throw new ApiError(400, "reference mode needs images, audios or videos");
            if (images != null) payload["images"] = images;
            if (audios != null) payload["audios"] = audios;
            if (videos != null) payload["videos"] = videos;
        }
        return (payload, model);
    }

    public static async Task<JsonObject> DoVideoCreate(JsonObject payload, string model, CancellationToken ct)
    {
        var res = await Call(HttpMethod.Post, "/v1/videos", payload, VideoCreateTimeout, ct);
        var vid = Str(res["video_id"]) ?? Str(res["id"]);
        if (string.IsNullOrEmpty(vid))
            throw new ApiError(502, "Agnes video API returned no video_id") { UpstreamStatus = 200, UpstreamBody = Snip(res.ToJsonString()) };
        lock (VideoGate)
        {
            if (VideoModels.Count > 5000) VideoModels.Clear();
            VideoModels[vid] = model;
        }
        var outObj = VideoView(res, vid, model);
        outObj["poll"] = $"/api/video/status/{Uri.EscapeDataString(vid)}?model={Uri.EscapeDataString(model)}";
        return outObj;
    }

    public static async Task<JsonObject> DoVideoStatus(string videoId, string? model, CancellationToken ct)
    {
        if (!VideoIdRe().IsMatch(videoId)) throw new ApiError(400, "invalid video id");
        if (!string.IsNullOrEmpty(model) && !ModelRe().IsMatch(model)) throw new ApiError(400, "invalid model");
        if (string.IsNullOrEmpty(model))
            lock (VideoGate) model = VideoModels.GetValueOrDefault(videoId);
        var q = "video_id=" + Uri.EscapeDataString(videoId);
        if (!string.IsNullOrEmpty(model)) q += "&model_name=" + Uri.EscapeDataString(model);
        var res = await Call(HttpMethod.Get, "/agnesapi?" + q, null, VideoStatusTimeout, ct);
        return VideoView(res, videoId, model);
    }
}
