// TASK6 phase 2 (P2R-21): fake TtsWeb (reading service) on the same mock port 127.0.0.1:18793, so破坏性 / T-state
// cases are reproducible without touching the real reading library.
//
//   POST /share                              {text, lang?, rate?, title?, by?, added_via?} → {id, url}   (dedup by text)
//   GET  /api/v1/articles/{id}               {ok, article:{share_id,title,lang,rate,status,current_version,text,sentences[]}}
//   POST /api/v1/ingest/preview              {text, lang} → {ok, sentences:[{seq,text,toks}]}
//   POST /api/v1/ingest/commit               replace / base_version (409 VERSION_CONFLICT) / split check / tr_via whitelist
//   POST /api/v1/ingest/readings_check       {items:[{sentence,tok,s,r}]} → {results:[{supported, candidates}]}
// Control (no auth):
//   POST /mock/ttsweb/mode {mode, count?}   mode: normal | 503 | 401 | 409 | dup | rc503 ; count = how many requests (0 = until reset)
//   POST /mock/ttsweb/reset   GET /mock/ttsweb/state   POST /mock/ttsweb/complete/{id} (simulate an L2 commit)   POST /mock/ttsweb/delete/{id}
// Requires "Authorization: Bearer mock-ttsweb-token-not-real" (else 401 AUTH_FAILED).
// Preview skeleton: digit runs → pos=num, r=null, unk (fill-in class); kanji runs → r="かんじ" (rewrite class),
// except a tiny table (一→いち, 本→ほん, 東京→とうきょう); kana runs → r=themselves; punctuation → sym.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static partial class MockTtsWeb
{
    private const string Token = "Bearer mock-ttsweb-token-not-real";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Art> Arts = new();
    private static string _mode = "normal";
    private static int _count;
    public static readonly List<string> Log = new();

    private sealed class Art
    {
        public required string Id, Text, Lang, Rate;
        public string? Title, AddedBy, AddedVia;
        public string Status = "partial";
        public int Version = 1;
        public JsonArray Sentences = new();
        public List<JsonObject> Versions = new();
        public bool Deleted;
    }

    private static readonly HashSet<string> TrVia = new() { "self", "claude-code", "agy-gemini", "human", "import", "provided", "gemini-api", "agnes-api" };

    [GeneratedRegex(@"[0-9][0-9,.]*|[一二三四五六七八九十百]|[㐀-鿿々]+|[ぁ-ゖー]+|[ァ-ヺー]+|[A-Za-z]+|\s+|.", RegexOptions.Singleline)] private static partial Regex TokRe();
    [GeneratedRegex(@"[^。！？\n]+[。！？]*|[。！？]+")] private static partial Regex SentRe();

    private static IResult Err(string code, string msg, int status, JsonObject? details = null) =>
        Results.Json(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = msg, ["hint"] = "", ["details"] = details ?? new JsonObject() } }, statusCode: status);

    /// <summary>Global mode gate; returns an early result or null. 'consume' decrements the counter.</summary>
    private static IResult? Gatekeep(HttpContext ctx, string route)
    {
        lock (Gate)
        {
            Log.Add($"{ctx.Request.Method} {route}");
            if (ctx.Request.Headers.Authorization != Token) return Err("AUTH_FAILED", "令牌无效或已吊销", 401);
            if (_mode is "503" or "401")
            {
                var m = _mode;
                if (_count > 0 && --_count == 0) _mode = "normal";
                return m == "503" ? Err("RETRY_LATER", "数据库暂不可用", 503) : Err("AUTH_FAILED", "令牌无效或已吊销", 401);
            }
            return null;
        }
    }

    private static string Hira(string s) => new(s.Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c).ToArray());

    public static JsonArray Toks(string sentence)
    {
        var arr = new JsonArray();
        foreach (Match m in TokRe().Matches(sentence))
        {
            var w = m.Value;
            var c = w[0];
            JsonObject t;
            if (char.IsDigit(c)) t = new JsonObject { ["s"] = w, ["pos"] = "num", ["r"] = null, ["unk"] = true };
            else if (c is >= '㐀' and <= '鿿' or '々')
                t = new JsonObject { ["s"] = w, ["pos"] = w is "一" or "二" or "三" or "六" ? "num" : "noun", ["r"] = w switch { "一" => "いち", "本" => "ほん", "東京" => "とうきょう", "六" => "ろく", "杯" => "はい", _ => "かんじ" } };
            else if (c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ') t = new JsonObject { ["s"] = w, ["pos"] = "particle", ["r"] = Hira(w) };
            else if (char.IsLetter(c)) t = new JsonObject { ["s"] = w, ["pos"] = "noun", ["r"] = null, ["unk"] = true };
            else t = new JsonObject { ["s"] = w, ["pos"] = "sym", ["r"] = "" };
            arr.Add(t);
        }
        return arr;
    }

    public static List<string> Split(string text) => SentRe().Matches(text).Select(m => m.Value.Trim()).Where(x => x.Length > 0).ToList();

    private static JsonArray Skeleton(string text)
    {
        var arr = new JsonArray();
        var i = 0;
        foreach (var s in Split(text)) arr.Add(new JsonObject { ["seq"] = i++, ["para"] = 0, ["text"] = s, ["toks"] = Toks(s), ["tr"] = null });
        return arr;
    }

    private static string Norm(string t) => t.Replace("\r\n", "\n").Trim();

    private static JsonObject View(Art a) => new()
    {
        ["share_id"] = a.Id, ["title"] = a.Title, ["lang"] = a.Lang, ["rate"] = a.Rate, ["status"] = a.Status,
        ["current_version"] = a.Version, ["text"] = a.Text, ["added_by"] = a.AddedBy, ["added_via"] = a.AddedVia,
        ["sentences"] = a.Sentences.DeepClone(),
    };

    public static void Map(WebApplication app)
    {
        app.MapPost("/mock/ttsweb/mode", async (HttpContext ctx) =>
        {
            var b = (await JsonNode.ParseAsync(ctx.Request.Body)) as JsonObject ?? new JsonObject();
            lock (Gate) { _mode = (string?)b["mode"] ?? "normal"; _count = b["count"]?.GetValue<int>() ?? 0; }
            return Results.Json(new { mode = _mode, count = _count });
        });
        app.MapPost("/mock/ttsweb/reset", () => { lock (Gate) { Arts.Clear(); _mode = "normal"; _count = 0; Log.Clear(); } return Results.Json(new { ok = true }); });
        app.MapGet("/mock/ttsweb/state", () =>
        {
            lock (Gate)
                return Results.Json(new JsonObject
                {
                    ["mode"] = _mode, ["count"] = _count,
                    ["articles"] = new JsonArray(Arts.Values.Select(a => (JsonNode)new JsonObject
                    {
                        ["id"] = a.Id, ["status"] = a.Status, ["version"] = a.Version, ["title"] = a.Title, ["added_by"] = a.AddedBy,
                        ["added_via"] = a.AddedVia, ["versions"] = new JsonArray(a.Versions.Select(v => (JsonNode)v.DeepClone()).ToArray()),
                    }).ToArray()),
                    ["log"] = new JsonArray(Log.TakeLast(200).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                });
        });
        app.MapPost("/mock/ttsweb/delete/{id}", (string id) =>
        {
            lock (Gate) { if (Arts.TryGetValue(id, out var a)) a.Deleted = true; }
            return Results.Json(new { ok = true });
        });
        app.MapPost("/mock/ttsweb/complete/{id}", (string id) =>
        {
            lock (Gate)
            {
                if (!Arts.TryGetValue(id, out var a)) return Results.NotFound();
                a.Version++;
                a.Status = "ready";
                foreach (var s in a.Sentences.OfType<JsonObject>()) { s["tr"] = "[agy] 译文"; s["tr_via"] = "agy-gemini"; }
                a.Versions.Add(new JsonObject { ["v"] = a.Version, ["kind"] = "replace", ["tr_via"] = "agy-gemini", ["status"] = "ready" });
                return Results.Json(new { ok = true, version = a.Version });
            }
        });

        app.MapPost("/share", async (HttpContext ctx) =>
        {
            if (Gatekeep(ctx, "/share") is { } early) return early;
            var b = (await JsonNode.ParseAsync(ctx.Request.Body)) as JsonObject ?? new JsonObject();
            var text = Norm((string?)b["text"] ?? "");
            if (text.Length == 0) return Results.Json(new { error = "缺少 text" }, statusCode: 400);
            lock (Gate)
            {
                var hit = Arts.Values.FirstOrDefault(a => !a.Deleted && a.Text == text);
                if (hit != null) return Results.Json(new { id = hit.Id, url = "/?s=" + hit.Id });
                var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
                var a = new Art
                {
                    Id = id, Text = text, Lang = (string?)b["lang"] ?? "ja", Rate = (string?)b["rate"] ?? "-20%",
                    Title = (string?)b["title"], AddedBy = (string?)b["by"], AddedVia = (string?)b["added_via"] ?? "legacy",
                    Sentences = Skeleton(text),
                };
                if (_mode == "dup") { a.Status = "ready"; foreach (var s in a.Sentences.OfType<JsonObject>()) s["tr"] = "既有译文"; if (_count > 0 && --_count == 0) _mode = "normal"; }
                a.Versions.Add(new JsonObject { ["v"] = 1, ["kind"] = "share", ["status"] = a.Status });
                Arts[id] = a;
                return Results.Json(new { id, url = "/?s=" + id });
            }
        });

        app.MapGet("/api/v1/articles/{id}", (HttpContext ctx, string id) =>
        {
            if (Gatekeep(ctx, "/api/v1/articles") is { } early) return early;
            lock (Gate)
            {
                if (!Arts.TryGetValue(id, out var a)) return Err("NOT_FOUND", "文章不存在", 404);
                if (a.Deleted) return Err("NOT_FOUND", "文章已删除", 410);
                return Results.Json(new JsonObject { ["ok"] = true, ["article"] = View(a) });
            }
        });

        app.MapPost("/api/v1/ingest/preview", async (HttpContext ctx) =>
        {
            if (Gatekeep(ctx, "/preview") is { } early) return early;
            var b = (await JsonNode.ParseAsync(ctx.Request.Body)) as JsonObject ?? new JsonObject();
            var text = Norm((string?)b["text"] ?? "");
            return Results.Json(new JsonObject { ["ok"] = true, ["text"] = text, ["sentences"] = Skeleton(text), ["warnings"] = new JsonArray() });
        });

        app.MapPost("/api/v1/ingest/readings_check", async (HttpContext ctx) =>
        {
            if (Gatekeep(ctx, "/readings_check") is { } early) return early;
            lock (Gate)
                if (_mode == "rc503") return Err("RETRY_LATER", "readings_check unavailable", 503);
            var b = (await JsonNode.ParseAsync(ctx.Request.Body)) as JsonObject ?? new JsonObject();
            var res = new JsonArray();
            // mock dictionary: only 東京 → とうきょうと is "supported" (to exercise the dict path)
            foreach (var it in b["items"] as JsonArray ?? new JsonArray())
                res.Add(new JsonObject { ["supported"] = (string?)it?["s"] == "東京", ["candidates"] = new JsonArray() });
            return Results.Json(new JsonObject { ["ok"] = true, ["results"] = res });
        });

        app.MapPost("/api/v1/ingest/commit", async (HttpContext ctx) =>
        {
            if (Gatekeep(ctx, "/commit") is { } early) return early;
            var b = (await JsonNode.ParseAsync(ctx.Request.Body)) as JsonObject ?? new JsonObject();
            lock (Gate)
            {
                var sid = (string?)b["share_id"];
                if (b["replace"]?.GetValue<bool>() != true || sid == null) return Err("BODY_INVALID", "mock supports replace only", 400);
                if (!Arts.TryGetValue(sid, out var a)) return Err("NOT_FOUND", "要替换的文章不存在", 404);
                if (_mode == "409")
                {
                    // someone else (L2) committed first
                    a.Version++; a.Status = "ready";
                    a.Versions.Add(new JsonObject { ["v"] = a.Version, ["kind"] = "replace", ["tr_via"] = "agy-gemini", ["status"] = "ready" });
                    if (_count > 0 && --_count == 0) _mode = "normal";
                }
                var bv = b["base_version"]?.GetValue<int>();
                if (bv != a.Version) return Err("VERSION_CONFLICT", "base_version 不是当前版本", 409, new JsonObject { ["current_version"] = a.Version, ["base_version"] = bv });
                foreach (var k in new[] { "title", "executor", "tr_via" })
                    if (string.IsNullOrWhiteSpace((string?)b[k])) return Err("BODY_INVALID", $"缺少字段 {k}", 400, new JsonObject { ["path"] = k });
                if (!TrVia.Contains((string)b["tr_via"]!)) return Err("BODY_INVALID", "tr_via 取值非法", 400);
                var items = b["sentences"] as JsonArray ?? new JsonArray();
                var server = Skeleton(Norm((string?)b["text"] ?? ""));
                if (items.Count != server.Count || items.Select((x, i) => (string?)x?["text"] != (string?)server[i]!["text"]).Any(x => x))
                    return Err("SPLIT_MISMATCH", "sentences 与服务端分句不一致", 422);
                var complete = true;
                for (var i = 0; i < items.Count; i++)
                {
                    var it = (JsonObject)items[i]!;
                    var s = (JsonObject)server[i]!;
                    var toks = (JsonArray)s["toks"]!;
                    foreach (var rd in it["readings"] as JsonArray ?? new JsonArray())
                    {
                        var ti = rd!["tok"]!.GetValue<int>();
                        if (ti < 0 || ti >= toks.Count || (string?)toks[ti]!["s"] != (string?)rd["s"]) return Err("BODY_INVALID", "readings 的 s 与该下标的 token 不一致", 400);
                        toks[ti]!["r"] = (string?)rd["r"];
                    }
                    var tr = (string?)it["tr"];
                    s["tr"] = string.IsNullOrWhiteSpace(tr) ? null : tr;
                    var via = (string?)it["tr_via"] ?? (string?)b["tr_via"];
                    if (via != null && !TrVia.Contains(via)) return Err("BODY_INVALID", "sentences[].tr_via 取值非法", 400);
                    s["tr_via"] = s["tr"] == null ? null : via;
                    s["tr_model"] = s["tr"] == null ? null : (string?)it["tr_model"] ?? (string?)b["tr_model"];
                    if (s["tr"] == null) complete = false;
                    if (toks.Any(t => (string?)t!["pos"] != "sym" && Regex.IsMatch((string?)t["s"] ?? "", "[0-9㐀-鿿]") && t["r"] == null)) complete = false;
                }
                if (!complete && b["allow_partial"]?.GetValue<bool>() != true) return Err("TR_MISSING", "有句缺译文", 422);
                a.Version++;
                a.Status = complete ? "ready" : "partial";
                a.Sentences = server;
                a.Title = (string?)b["title"]; a.Lang = (string?)b["lang"] ?? a.Lang; a.Rate = (string?)b["rate"] ?? a.Rate;
                a.Versions.Add(new JsonObject
                {
                    ["v"] = a.Version, ["kind"] = "replace", ["tr_via"] = (string?)b["tr_via"], ["tr_model"] = (string?)b["tr_model"],
                    ["executor"] = (string?)b["executor"], ["prompt_version"] = (string?)b["prompt_version"], ["status"] = a.Status,
                    ["rate"] = a.Rate, ["title"] = a.Title,
                    ["readings"] = new JsonArray(items.SelectMany(x => (x?["readings"] as JsonArray ?? new JsonArray()).Select(r => r!.DeepClone())).ToArray()),
                    ["per_sentence_via"] = new JsonArray(server.Select(x => (JsonNode?)JsonValue.Create((string?)x!["tr_via"])).ToArray()),
                });
                return Results.Json(new JsonObject { ["ok"] = true, ["share_id"] = a.Id, ["url"] = "/?s=" + a.Id, ["duplicate"] = false, ["version"] = a.Version, ["status"] = a.Status });
            }
        });
    }
}
