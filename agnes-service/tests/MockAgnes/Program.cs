// Test-only fake Agnes API for agnes-service limiter/proxy tests. Listens on 127.0.0.1:18793.
// Prompt conventions: "sleep:N ..." -> wait N seconds before answering; "reject" -> HTTP 400;
// "boom" -> HTTP 500. Requires "Authorization: Bearer mock-key-not-real".
//
// TASK6 (review O9) — analyze requests (response_format json_object, user content "### INPUT\n{json}"):
// scenario prefix anywhere in the input text, "scenario:NAME":
//   json_ok          valid analyzer / enrich JSON
//   json_bad_concat  raw: first answer drops a character (concat mismatch); the repair answer is valid
//   json_invalid     first answer is not JSON; the repair answer is valid
//   bad_always       raw: concat always wrong / enrich: out-of-range fix index always (validation never passes)
//   oob              enrich: first answer has an out-of-range fix index; the repair answer is valid
//   403              Agnes routes answer 403 insufficient_user_quota for every model (gemini route answers normally)
//   403m             Agnes answers 403 only for agnes-3.0-flash (model-level quota, review O1)
//   500              Agnes routes answer 500
//   403all           Agnes AND gemini answer 403 (every upstream call is a 4xx → quota refund)
//   400              Agnes answers 400 (parameter class: no fallback)
//   length           enrich chunks with > 1 sentence answer finish_reason=length (forces chunk split)
//   sleep:N          Agnes routes wait N seconds first (timeout → fallback to gemini)
// Route POST /gemini/chat/completions = same fake, but requires "Bearer mock-gemini-key-not-real" and a
// reasoning_effort of low|minimal (gemini strategy), so call_log can tell the providers apart.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var app = WebApplication.CreateSlimBuilder(args).Build();
app.Urls.Add("http://127.0.0.1:18793");

static async Task<(JsonObject body, IResult? early)> Prep(HttpContext ctx)
{
    if (ctx.Request.Headers.Authorization != "Bearer mock-key-not-real")
        return (new(), Results.Json(new { error = new { message = "bad key" } }, statusCode: 401));
    var body = (await JsonNode.ParseAsync(ctx.Request.Body)) as JsonObject ?? new JsonObject();
    if (body["response_format"] != null) return (body, null);   // analyze request: handled by Analyze()
    var prompt = body["prompt"]?.GetValue<string>()
                 ?? (body["messages"] as JsonArray)?.Last()?["content"]?.GetValue<string>() ?? "";
    if (prompt.StartsWith("sleep:") && int.TryParse(prompt[6..].Split(' ')[0], out var s)) await Task.Delay(s * 1000);
    if (prompt.Contains("reject")) return (body, Results.Json(new { error = new { message = "mock: rejected prompt" } }, statusCode: 400));
    if (prompt.Contains("boom")) return (body, Results.Json(new { error = new { message = "mock: upstream failure" } }, statusCode: 500));
    return (body, null);
}

app.MapGet("/v1/models", () => Results.Json(new { data = new[] { new { id = "agnes-2.5-flash" }, new { id = "agnes-image-2.5-flash" }, new { id = "agnes-video-2.5-flash" } } }));

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    var (body, early) = await Prep(ctx);
    if (early != null) return early;
    if (body["response_format"] != null) return await MockAnalyze.Answer(body, "agnes");
    return Results.Json(new
    {
        model = body["model"]?.GetValue<string>(),
        choices = new[] { new { message = new { role = "assistant", content = "mock reply", reasoning_content = "mock reasoning" }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 3, completion_tokens = 2, total_tokens = 5 },
    });
});

app.MapPost("/gemini/chat/completions", async (HttpContext ctx) =>
{
    if (ctx.Request.Headers.Authorization != "Bearer mock-gemini-key-not-real")
        return Results.Json(new[] { new { error = new { code = 401, message = "API key not valid", status = "UNAUTHENTICATED" } } }, statusCode: 401);
    var body = (await JsonNode.ParseAsync(ctx.Request.Body)) as JsonObject ?? new JsonObject();
    var effort = body["reasoning_effort"]?.GetValue<string>();
    if (effort is not ("low" or "minimal"))
        return Results.Json(new[] { new { error = new { code = 400, message = "mock gemini: reasoning_effort low|minimal required", status = "INVALID_ARGUMENT" } } }, statusCode: 400);
    return await MockAnalyze.Answer(body, "gemini");
});

app.MapPost("/v1/images/generations", async (HttpContext ctx) =>
{
    var (body, early) = await Prep(ctx);
    if (early != null) return early;
    var fmt = body["extra_body"]?["response_format"]?.GetValue<string>();
    return Results.Json(new { data = new[] { new { url = "https://example.invalid/mock.png", note = "response_format=" + fmt } } });
});

app.MapPost("/v1/videos", async (HttpContext ctx) =>
{
    var (body, early) = await Prep(ctx);
    if (early != null) return early;
    return Results.Json(new { id = "task_mock1", task_id = "task_mock1", video_id = "video_mock1", @object = "video",
        model = body["model"]?.GetValue<string>(), status = "queued", progress = 0, seconds = body["seconds"]?.GetValue<string>(), size = body["size"]?.GetValue<string>() });
});

app.MapGet("/agnesapi", (string video_id, string? model_name) =>
    Results.Json(new { video_id, model = model_name, status = "completed", progress = 100, url = "https://example.invalid/mock.mp4", seconds = "5", size = "720P" }));

MockTtsWeb.Map(app);   // TASK6 phase 2: fake TtsWeb (tests/MockAgnes/MockTtsWeb.cs)

app.Run();

static partial class MockAnalyze
{
    [GeneratedRegex(@"scenario:([a-z_0-9]+)(?::(\d+))?")] private static partial Regex ScenarioRe();
    [GeneratedRegex(@"[㐀-鿿々]+|[ぁ-ゖー]+|[ァ-ヺー]+|[A-Za-z0-9:_ ]+|.", RegexOptions.Singleline)] private static partial Regex RunRe();

    // canned school-grammar analysis of the TASK6 sample sentence (so pos map + ruby can be checked)
    private static readonly (string w, string p, string f)[] Sample =
    {
        ("彼女", "代名詞", "かのじょ"), ("は", "助詞", "は"), ("静かな", "形容動詞", "しずかな"), ("図書館", "名詞", "としょかん"),
        ("で", "助詞", "で"), ("本", "名詞", "ほん"), ("を", "助詞", "を"), ("読ん", "動詞", "よん"), ("で", "助詞", "で"),
        ("いる", "動詞", "いる"), ("。", "記号", ""),
    };
    private const string SampleText = "彼女は静かな図書館で本を読んでいる。";

    public static async Task<IResult> Answer(JsonObject body, string provider)
    {
        var model = body["model"]?.GetValue<string>() ?? "";
        var msgs = body["messages"] as JsonArray ?? new JsonArray();
        var repair = msgs.Count > 2;
        var user = msgs.Count > 1 ? msgs[1]?["content"]?.GetValue<string>() ?? "" : "";
        var idx = user.IndexOf("### INPUT\n", StringComparison.Ordinal);
        var input = idx >= 0 ? JsonNode.Parse(user[(idx + 10)..]) as JsonObject ?? new JsonObject() : new JsonObject();
        // phase 2: enrich-v2 carries the whole text as context.full_text in every chunk; the scenario is taken
        // from this chunk's own input only (as before), never from the context.
        var own = (JsonObject)input.DeepClone();
        own.Remove("context");
        var m = ScenarioRe().Match(idx >= 0 ? own.ToJsonString() : user);
        var scen = m.Success ? m.Groups[1].Value : "json_ok";
        if (scen == "sleep" && provider == "agnes" && int.TryParse(m.Groups[2].Value, out var secs)) await Task.Delay(secs * 1000);
        if (scen == "403all")
            return Results.Json(new { error = new { message = "insufficient_user_quota" } }, statusCode: 403);
        // ---- TASK6 phase 2 scenarios
        if (provider == "gemini")
        {
            if (scen == "g429")   // Gemini rate limit with google.rpc.RetryInfo
                return Results.Json(new[] { new { error = new { code = 429, message = "Resource has been exhausted (e.g. check quota).", status = "RESOURCE_EXHAUSTED",
                    details = new object[] { new Dictionary<string, object> { ["@type"] = "type.googleapis.com/google.rpc.RetryInfo", ["retryDelay"] = "7s" } } } } }, statusCode: 429);
            if (scen == "g500") return Results.Json(new { error = new { message = "mock gemini failure" } }, statusCode: 500);
            if (scen == "gsleep" && int.TryParse(m.Groups[2].Value, out var gs)) await Task.Delay(gs * 1000);
        }
        if (scen == "allquota" )
            return Results.Json(new { error = new { message = "insufficient_user_quota" } }, statusCode: provider == "gemini" ? 429 : 403);
        if (provider == "agnes")
        {
            if (scen == "400") return Results.Json(new { error = new { message = "mock: invalid parameter" } }, statusCode: 400);
            if (scen == "403" || (scen == "403m" && model == "agnes-3.0-flash"))
                return Results.Json(new { error = new { message = "insufficient_user_quota", type = "insufficient_user_quota" } }, statusCode: 403);
            if (scen == "500") return Results.Json(new { error = new { message = "mock: upstream failure" } }, statusCode: 500);
        }
        string content;
        var finish = "stop";
        if (input["text"] is JsonValue tv)
        {
            var text = tv.GetValue<string>();
            var tokens = RawTokens(text);
            if ((scen == "json_bad_concat" && !repair) || scen == "bad_always") tokens.RemoveAt(tokens.Count - 1);
            content = new JsonObject { ["tokens"] = new JsonArray(tokens.ToArray()) }.ToJsonString();
            if (scen == "json_invalid" && !repair) content = "{\"tokens\": [ 这不是 JSON";
        }
        else
        {
            var sents = input["sentences"] as JsonArray ?? new JsonArray();
            if (scen == "length" && sents.Count > 1)
            {
                finish = "length";
                content = "{\"sentences\":[{\"seq\":";
            }
            else
            {
                var outArr = new JsonArray();
                foreach (var sn in sents)
                {
                    var fixes = new JsonArray();
                    foreach (var t in sn?["toks"] as JsonArray ?? new JsonArray())
                    {
                        var s = t?["s"]?.GetValue<string>() ?? "";
                        if (t?["r"] == null && Regex.IsMatch(s, "[0-9㐀-鿿]"))
                            fixes.Add(new JsonObject { ["i"] = t!["i"]!.GetValue<int>(), ["s"] = s, ["r"] = "じゅうはち" });
                    }
                    if ((scen == "oob" && !repair) || scen == "bad_always" || (scen == "gbad" && provider == "gemini"))
                        fixes.Add(new JsonObject { ["i"] = 999, ["s"] = "本", ["r"] = "ほん" });
                    if (scen == "rewrite")   // rewrite-class fixes on tokens that already have a reading
                        foreach (var t in sn?["toks"] as JsonArray ?? new JsonArray())
                        {
                            var s = t?["s"]?.GetValue<string>() ?? "";
                            var r0 = t?["r"]?.GetValue<string>();
                            if (r0 == null || !Regex.IsMatch(s, "^[㐀-鿿]+$")) continue;
                            var to = s switch { "一" => "いっ", "本" => "ぽん", "六" => "ろっ", "杯" => "ぱい", "東京" => "とうきょうと", _ => r0 + "ん" };
                            fixes.Add(new JsonObject { ["i"] = t!["i"]!.GetValue<int>(), ["s"] = s, ["r"] = to });
                        }
                    outArr.Add(new JsonObject
                    {
                        ["seq"] = sn!["seq"]!.GetValue<long>(),
                        ["tr"] = $"[{provider}:{model}] 译文：" + (sn["text"]?.GetValue<string>() ?? ""),
                        ["fixes"] = fixes,
                    });
                }
                content = new JsonObject { ["sentences"] = outArr }.ToJsonString();
                if (scen == "json_invalid" && !repair) content = "not json at all";
            }
        }
        return Results.Json(new
        {
            model,
            choices = new[] { new { message = new { role = "assistant", content, reasoning_content = "mock reasoning" }, finish_reason = finish } },
            usage = new { prompt_tokens = 30, completion_tokens = 20, total_tokens = 50 },
        });
    }

    private static List<JsonNode> RawTokens(string text)
    {
        var list = new List<JsonNode>();
        var rest = text;
        var pre = text.IndexOf(SampleText, StringComparison.Ordinal);
        if (pre >= 0 && pre + SampleText.Length == text.Length)
        {
            if (pre > 0) list.Add(Tok(text[..pre], "記号", ""));
            foreach (var (w, p, f) in Sample) list.Add(Tok(w, p, f));
            return list;
        }
        foreach (Match r in RunRe().Matches(rest))
        {
            var w = r.Value;
            var c = w[0];
            if (c is >= '㐀' and <= '鿿' or '々') list.Add(Tok(w, "名詞", "よみ"));
            else if (c is >= 'ぁ' and <= 'ゖ') list.Add(Tok(w, "助詞", w));
            else if (c is >= 'ァ' and <= 'ヺ') list.Add(Tok(w, "名詞", new string(w.Select(x => x is >= 'ァ' and <= 'ヶ' ? (char)(x - 0x60) : x).ToArray())));
            else list.Add(Tok(w, "記号", ""));
        }
        return list;
    }

    private static JsonObject Tok(string w, string p, string f) => new() { ["word"] = w, ["pos"] = p, ["furigana"] = f };
}
