using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgnesService;

// =====================================================================================================
// TASK6 phase 1: provider registry + POST /api/analyze engine (raw | enrich).
//   * registry: static dictionary of records (review O4); per-provider model order = model-level fallback
//     first (O1); both providers use response_format json_object, gemini adds reasoning_effort (O2).
//   * failure classes (O5 / fallback design §2.1, L1 order): param 4xx → no fallback (fatal);
//     401 → skip the rest of that provider; 403/429/5xx/timeout/network/bad shape → next model, then next
//     provider; JSON parse / validation failure → one repair retry on the same model, then next model;
//     finish_reason=length → chunk too big → split in half (enrich), next model (raw).
//   * enrich (review B6): compact projection [{i,s,r,unk}], ~20 sentences / ~1500 chars per chunk,
//     validate/repair/fallback per chunk, only failed chunks are redone; fixes carry i + original s and
//     the server checks s (O8); tokens that need no reading (symbols, Latin unk words) are excluded (gap 6).
// =====================================================================================================

/// <summary>One upstream LLM backend. Url/Key are read per call (Settings hot reload).</summary>
/// TASK6 phase 2 (P2R-7): chunk size and per-call timeout of the ingest path hang off the provider
/// (ChunkSents / ChunkChars / CallTimeoutS(model)); all read per use (new keys need a restart, plan §1.6).
public sealed record ProviderDef(string Name, string Label, string KeyName, string[] Models,
    IReadOnlyDictionary<string, string> ReasoningEffort, Func<string> Url, Func<string> Key,
    Func<int> ChunkSents, Func<int> ChunkChars, Func<string, int> CallTimeoutS)
{
    public string Strategy => ReasoningEffort.Count > 0 ? "json_object+reasoning_effort" : "json_object";
    /// <summary>A chunk fits when it is within both limits (a single sentence always fits: it cannot be split).</summary>
    public bool Fits(IReadOnlyCollection<Analyze.Sent> chunk) =>
        chunk.Count <= 1 || (chunk.Count <= ChunkSents() && chunk.Sum(s => s.Text.Length) <= ChunkChars());
}

public static class Providers
{
    public const string DefaultGeminiUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";

    public static readonly Dictionary<string, ProviderDef> All = new()
    {
        ["agnes"] = new("agnes", "Agnes API", "AGNES_API_KEY",
            new[] { "agnes-3.0-flash", "agnes-2.5-flash" },
            new Dictionary<string, string>(),
            () => Settings.AgnesBase + "/v1/chat/completions", () => Settings.AgnesApiKey,
            () => Settings.GetInt("INGEST_CHUNK_SENTS_AGNES", 8, 1, 200), () => Settings.GetInt("INGEST_CHUNK_CHARS_AGNES", 600, 50, 5000),
            _ => Settings.GetInt("INGEST_TIMEOUT_AGNES_S", 120, 1, 900)),
        ["gemini"] = new("gemini", "Gemini API", "GEMINI_API_KEY",
            new[] { "gemini-flash-lite-latest", "gemini-flash-latest" },
            new Dictionary<string, string> { ["gemini-flash-latest"] = "low", ["gemini-flash-lite-latest"] = "minimal" },
            () => Settings.Get("GEMINI_API_URL", DefaultGeminiUrl), () => Settings.Get("GEMINI_API_KEY"),
            () => Settings.GetInt("INGEST_CHUNK_SENTS_GEMINI", 20, 1, 200), () => Settings.GetInt("INGEST_CHUNK_CHARS_GEMINI", 1500, 50, 5000),
            m => m.Contains("lite") ? Settings.GetInt("INGEST_TIMEOUT_GEMINI_LITE_S", 45, 1, 900) : Settings.GetInt("INGEST_TIMEOUT_GEMINI_FLASH_S", 90, 1, 900)),
    };

    public static double CallTimeout => Settings.GetInt("ANALYZE_TIMEOUT_S", 180, 5, 900);
    /// <summary>Whole-request wall clock of direct POST /api/analyze (was the constant DeadlineSeconds=600).</summary>
    public static double AnalyzeDeadline => Settings.GetInt("ANALYZE_DEADLINE_S", 600, 10, 3600);
    /// <summary>Ingest: enrich wall clock, counted from the start of enrich (plan §2.2, DF-7).</summary>
    public static double IngestDeadline => Settings.GetInt("INGEST_DEADLINE_S", 420, 10, 3600);
    public static string DefaultProvider => Settings.Get("ANALYZE_DEFAULT_PROVIDER", "agnes").Trim().ToLowerInvariant();
    public static List<string> Order => ParseOrder(Settings.Get("ANALYZE_PROVIDER_ORDER", "agnes,gemini"));

    public static List<string> ParseOrder(string raw)
    {
        var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).Where(All.ContainsKey).Distinct().ToList();
        return list.Count > 0 ? list : new List<string> { "agnes", "gemini" };
    }

    /// <summary>Ready = key configured and well-formed + URL parses as absolute http(s) (review B8). No probe.</summary>
    public static bool Ready(ProviderDef p) => KeyLooksValid(p.Key()) && UrlLooksValid(p.Url());

    public static bool KeyLooksValid(string key) =>
        !string.IsNullOrWhiteSpace(key) && !key.StartsWith('#') && !key.Any(char.IsWhiteSpace);

    public static bool UrlLooksValid(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") && !string.IsNullOrEmpty(u.Host);

    public static ProviderDef? OwnerOf(string model) => All.Values.FirstOrDefault(p => p.Models.Contains(model));

    /// <summary>
    /// Ordered (provider, model) candidates: requested provider (or the owner of the requested model)
    /// &gt; ANALYZE_DEFAULT_PROVIDER &gt; ANALYZE_PROVIDER_ORDER; within a provider its model order, with a
    /// requested model moved to the front (same-provider model fallback before switching provider, O1).
    /// Providers that are not ready are skipped.
    /// </summary>
    public static List<(ProviderDef P, string Model)> Candidates(string? reqProvider, string? reqModel,
        string defaultProvider, IList<string> order, Func<ProviderDef, bool> ready)
    {
        var first = reqProvider ?? (reqModel != null ? OwnerOf(reqModel)?.Name : null)
                    ?? (All.ContainsKey(defaultProvider) ? defaultProvider : order[0]);
        var names = new List<string> { first };
        foreach (var n in order) if (!names.Contains(n)) names.Add(n);
        var outList = new List<(ProviderDef, string)>();
        foreach (var n in names)
        {
            var p = All[n];
            if (!ready(p)) continue;
            var models = p.Models.ToList();
            if (reqModel != null && models.Remove(reqModel)) models.Insert(0, reqModel);
            foreach (var m in models) outList.Add((p, m));
        }
        return outList;
    }

    /// <summary>/health "analyze" section: readiness + model names only, never URLs or keys (review gap 9).</summary>
    public static JsonObject HealthView()
    {
        var provs = new JsonObject();
        foreach (var p in All.Values)
            provs[p.Name] = new JsonObject
            {
                ["ready"] = Ready(p),
                ["models"] = new JsonArray(p.Models.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
                ["strategy"] = p.Strategy,
            };
        return new JsonObject
        {
            ["providers"] = provs,
            ["default_provider"] = DefaultProvider,
            ["order"] = new JsonArray(Order.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["prompt_versions"] = new JsonObject { ["raw"] = Analyze.RawPromptVersion, ["enrich"] = Analyze.EnrichPromptVersion },
            ["deadline_s"] = new JsonObject { ["analyze"] = AnalyzeDeadline, ["ingest"] = IngestDeadline },
            ["circuit"] = Circuit.View(),
            ["upstream_rpm"] = UpstreamLimiter.View(),
        };
    }
}

/// <summary>Filled by the analyze run, read by the Generate pipeline for call_log + refund (review B9).</summary>
public sealed class CallOutcome
{
    public string? Model;      // "provider:model"
    public string? Detail;     // compact trail
    public bool Refund;        // true only if every upstream call was answered with a 4xx
}

public static partial class Analyze
{
    public const string RawPromptVersion = "analyze-v1";
    public const string EnrichPromptVersion = "enrich-v2";   // P2R-6: + context.full_text
    public const int RawMaxChars = 2000;
    public const int EnrichMaxSentences = 200;
    public const int EnrichMaxChars = 5000;          // = reading.articles text limit
    public const int ChunkMaxSentences = 20;
    public const int ChunkMaxChars = 1500;
    public const int RawCallCap = 4;                 // TASK6 §3: whole raw request ≤ 4 upstream calls
    public const int ChunkCallCap = 3;               // review B6: ≤ 3 calls per chunk (repair + model switch)
    public const int EnrichCallCapMax = 30;          // whole enrich request ≤ min(30, 2*chunks + 4)
    public const int ChunkParallelism = 2;           // fallback design §3.1
    // whole request wall clock: Providers.AnalyzeDeadline (ANALYZE_DEADLINE_S, default 600; phase 2 made it configurable)

    // ---------------------------------------------------------------- pos (school grammar → reading enum)

    /// <summary>
    /// Closed set the model may use (analyzer shape) → reading pos enum. Targets were taken from the TtsWeb
    /// tokenizer (tts-redesign/impl/service/tokenizer.py POS_MAP + 数詞→num; enum: noun pronoun verb adj adv
    /// conj particle aux affix sym num, "other" for unmapped). Review B3: 代名詞→pronoun, 形容動詞→adj,
    /// 接頭/接尾→affix, 数詞→num; 連体詞→adv and 感動詞→conj mirror TtsWeb (it has no adn/intj).
    /// </summary>
    public static readonly Dictionary<string, string> PosMap = new()
    {
        ["名詞"] = "noun", ["代名詞"] = "pronoun", ["動詞"] = "verb", ["形容詞"] = "adj", ["形容動詞"] = "adj",
        ["副詞"] = "adv", ["連体詞"] = "adv", ["接続詞"] = "conj", ["感動詞"] = "conj", ["助詞"] = "particle",
        ["助動詞"] = "aux", ["接頭辞"] = "affix", ["接尾辞"] = "affix", ["数詞"] = "num", ["記号"] = "sym", ["改行"] = "sym",
    };

    // ---------------------------------------------------------------- character classes

    public static bool IsKanji(char c) => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff' or '々' or '〆' or 'ヶ';
    public static bool IsDigit(char c) => c is >= '0' and <= '9' or >= '０' and <= '９';
    public static bool IsKana(char c) => c is >= '\u3041' and <= '\u3096' or >= '\u30a1' and <= '\u30fa' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ';
    public static char ToHira(char c) => c is >= '\u30a1' and <= '\u30f6' ? (char)(c - 0x60) : c;
    public static string ToHira(string s) => new(s.Select(ToHira).ToArray());

    /// <summary>Kana that can be okurigana (small ヵ/ヶ are counters read か/こ/が, treated like kanji).</summary>
    private static bool IsTailKana(char c) => IsKana(c) && c is not ('ヵ' or 'ヶ');

    /// <summary>Trailing kana run of a word (its okurigana candidate).</summary>
    public static string KanaTail(string s)
    {
        var t = 0;
        while (t < s.Length && IsTailKana(s[s.Length - 1 - t])) t++;
        return s[(s.Length - t)..];
    }

    /// <summary>
    /// A reading must cover the whole word: for a word with kanji/digits and trailing kana, the reading ends
    /// with that kana (静かな → しずかな, not しずか). Found in the real smoke (agnes-3.0-flash gave 静かな/しずか).
    /// </summary>
    public static string? ReadingCoverageError(string s, string r)
    {
        if (r.Length == 0 || !s.Any(c => IsKanji(c) || IsDigit(c))) return null;
        var tail = ToHira(KanaTail(s));
        if (tail.Length == 0 || (r.EndsWith(tail, StringComparison.Ordinal) && r.Length > tail.Length)) return null;
        return $"「{Clip(s)}」的读音「{Clip(r)}」没有覆盖整个词：读音应包含词尾假名「{tail}」（如 静かな→しずかな）";
    }

    /// <summary>Same rule as TtsWeb ingest.NEEDS_READING_RE: a token needs a reading iff it has kanji or digits.</summary>
    public static bool NeedsReading(string s) => s.Any(c => IsKanji(c) || c is >= '0' and <= '9');

    [GeneratedRegex(@"^[ぁ-ゖー]+$")] private static partial Regex HiraRe();
    public static bool IsHiragana(string s) => HiraRe().IsMatch(s);

    // ---------------------------------------------------------------- ruby (TASK6 §6)

    /// <summary>
    /// Deterministic ruby: null if r is empty or s has no kanji/digit (pure kana / symbol / Latin).
    /// Otherwise the longest common suffix of r and the trailing kana run of s is okurigana:
    /// [{t: stem, r: reading-minus-suffix}, {t: suffix}]; no usable suffix → whole word [{t: s, r}].
    /// Digits are annotated like kanji (whole word, as TtsWeb does).
    /// </summary>
    public static JsonArray? Ruby(string s, string? r)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(r)) return null;
        if (!s.Any(c => IsKanji(c) || IsDigit(c))) return null;
        var tail = KanaTail(s).Length;
        var k = 0;
        while (k < tail && k < r.Length && ToHira(s[s.Length - 1 - k]) == r[r.Length - 1 - k]) k++;
        if (k > 0 && k < r.Length && k < s.Length)
            return new JsonArray(new JsonObject { ["t"] = s[..^k], ["r"] = r[..^k] }, new JsonObject { ["t"] = s[^k..] });
        return new JsonArray(new JsonObject { ["t"] = s, ["r"] = r });
    }

    // ---------------------------------------------------------------- request parsing (pre-admission: 400s cost nothing)

    public sealed record Tok(int I, string S, string? R, bool Unk, string? Pos);
    public sealed record Sent(long Seq, string Text, List<Tok> Toks);
    public sealed record Req(string Mode, string? Provider, string? Model, string? Text, List<Sent>? Sentences, bool Gloss);

    private static ApiError Bad(string code, string msg) => new(400, msg) { Code = code };

    private static string? OptStr(JsonObject o, string name)
    {
        var n = o[name];
        if (n == null) return null;
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s)) throw Bad("invalid_request", $"'{name}' must be a string");
        return s;
    }

    public static Req Parse(JsonObject body)
    {
        var mode = OptStr(body, "mode");
        if (mode is not ("raw" or "enrich")) throw Bad("invalid_mode", "'mode' is required: raw or enrich");
        var provider = OptStr(body, "provider")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(provider)) provider = null;
        if (provider != null && !Providers.All.ContainsKey(provider))
            throw Bad("invalid_provider", $"'provider' must be one of {string.Join(", ", Providers.All.Keys)}");
        var model = OptStr(body, "model")?.Trim();
        if (string.IsNullOrEmpty(model)) model = null;
        if (model != null)
        {
            var owner = Providers.OwnerOf(model);
            if (owner == null || (provider != null && owner.Name != provider))
                throw Bad("invalid_model", provider != null
                    ? $"'model' must be one of {string.Join(", ", Providers.All[provider].Models)}"
                    : $"'model' must be one of {string.Join(", ", Providers.All.Values.SelectMany(p => p.Models))}");
        }
        var gloss = body["gloss"] is JsonValue gv && gv.TryGetValue<bool>(out var g) && g;

        if (mode == "raw")
        {
            var text = OptStr(body, "text")?.Trim();
            if (string.IsNullOrEmpty(text)) throw Bad("invalid_text", "'text' is required for mode=raw");
            if (text.Length > RawMaxChars) throw Bad("invalid_text", $"'text' is too long (max {RawMaxChars} chars; split long texts yourself)");
            return new Req(mode, provider, model, text, null, false);
        }

        if (body["sentences"] is not JsonArray arr || arr.Count == 0)
            throw Bad("invalid_sentences", "'sentences' (non-empty array) is required for mode=enrich");
        if (arr.Count > EnrichMaxSentences) throw Bad("invalid_sentences", $"too many sentences (max {EnrichMaxSentences})");
        var sents = new List<Sent>();
        var seen = new HashSet<long>();
        var total = 0;
        for (var k = 0; k < arr.Count; k++)
        {
            var path = $"sentences[{k}]";
            if (arr[k] is not JsonObject so) throw Bad("invalid_sentences", $"{path} must be an object");
            if (so["seq"] is not JsonValue sv || !sv.TryGetValue<long>(out var seq) || seq < 0)
                throw Bad("invalid_sentences", $"{path}.seq must be a non-negative integer");
            if (!seen.Add(seq)) throw Bad("invalid_sentences", $"{path}.seq {seq} is duplicated");
            var text = so["text"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
            if (string.IsNullOrWhiteSpace(text)) throw Bad("invalid_sentences", $"{path}.text is required");
            total += text.Length;
            if (total > EnrichMaxChars) throw Bad("invalid_sentences", $"sentences are too long in total (max {EnrichMaxChars} chars)");
            var toks = new List<Tok>();
            if (so["toks"] is JsonArray ta)
            {
                for (var i = 0; i < ta.Count; i++)
                {
                    if (ta[i] is not JsonObject to || to["s"] is not JsonValue tsv || !tsv.TryGetValue<string>(out var s))
                        throw Bad("invalid_sentences", $"{path}.toks[{i}].s must be a string");
                    string? r = to["r"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : null;
                    var unk = to["unk"] is JsonValue uv && uv.TryGetValue<bool>(out var ub) && ub;
                    string? pos = to["pos"] is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps : null;
                    toks.Add(new Tok(i, s, r, unk, pos));
                }
                if (toks.Count > 0 && string.Concat(toks.Select(t => t.S)) != text)
                    throw Bad("invalid_sentences", $"{path}: toks do not concatenate to text (pass the preview skeleton unchanged)");
            }
            else if (so["toks"] is not null) throw Bad("invalid_sentences", $"{path}.toks must be an array or null");
            sents.Add(new Sent(seq, text, toks));
        }
        return new Req(mode, provider, model, null, sents, gloss);
    }

    /// <summary>call_log prompt_preview selector (B9): raw → text, enrich → first sentence (Generate cuts to 120).</summary>
    public static string? Preview(JsonObject body)
    {
        if (body["text"] is JsonValue tv && tv.TryGetValue<string>(out var t)) return t.Trim();
        if (body["sentences"] is JsonArray { Count: > 0 } a && a[0]?["text"] is JsonValue sv && sv.TryGetValue<string>(out var s)) return s;
        return null;
    }

    // ---------------------------------------------------------------- prompts (versioned constants)

    // analyze-v1: rules written from the japanese-analyzer rule set (facts, not its text).
    private const string RawSystem =
        "你是日语形态分析器。把用户给出的日语文本按日本学校文法切分为词，并标注词性与读音。规则：\n" +
        "1. 按学校文法切分；助动词（た・ます・です・ない・れる・られる・せる・させる・う・よう・たい・らしい 等）一律单独成词。\n" +
        "2. て形与补助动词分开：「読んでいる」→ 読ん｜で｜いる；「食べてしまった」→ 食べ｜て｜しまっ｜た。补助动词（いる・ある・しまう・おく・くる・いく・みる 等）的词性是 動詞，不是 助動詞。\n" +
        "3. 形容動詞连同词尾作为一个词（「静かな」「綺麗に」各一个词，词性 形容動詞）。\n" +
        "4. 助词与前面的词分开。\n" +
        "5. 区分两种「ない」：接在动词未然形后的是 助動詞（読まない）；表示不存在或接在 〜く 后的是 形容詞（お金がない、高くない）。\n" +
        "6. 同形多读的词结合上下文判断读音（如 今日・明日・一日・上手・方）。数字与量词按实际音变给读音（一本→いっぽん、十八→じゅうはち）。\n" +
        "7. 数字（汉数字、阿拉伯数字，含 万・億）的词性为 数詞；量词/助数词（本・年・人・枚 等）为 接尾辞。数字与量词分成两个词时，音变要分别写进各自的读音：一本 → 一:いっ、本:ぽん；三本 → 三:さん、本:ぼん；六百 → 六:ろっ、百:ぴゃく。\n" +
        "8. furigana 一律用平假名，且是整个词的读音：含汉字的词连同词尾送假名一起写（静かな→しずかな、読ん→よん，不能只写汉字部分）；平假名词照抄；片假名词转写为平假名；記号与改行的 furigana 为空字符串。\n" +
        "9. pos 只能取以下之一：名詞、代名詞、動詞、形容詞、形容動詞、副詞、連体詞、接続詞、感動詞、助詞、助動詞、接頭辞、接尾辞、数詞、記号、改行。\n" +
        "10. 标点、空格各自成为 記号 词；换行符本身成为一个 改行 词（word 为 \"\\n\"）。\n" +
        "11. 所有 word 按顺序拼接后必须与输入文本逐字完全相同（不增、不删、不改任何字符，包括空格与标点）。\n" +
        "只输出 JSON，不要任何解释或代码块标记，格式：{\"tokens\":[{\"word\":\"…\",\"pos\":\"…\",\"furigana\":\"…\"}]}";

    private const string EnrichSystem =
        "你是日语朗读教材的翻译与读音校对员。输入是若干日语句子及其分词骨架（toks：i=词下标，s=表层形，r=当前平假名读音，" +
        "r 为 null 表示缺读音，unk=true 表示词典里没有该词）。对每一句：\n" +
        "1. tr：给出自然、通顺的简体中文意译（不要逐词硬译），不得为空，单句不超过 300 字。\n" +
        "2. fixes：只列出需要改正或补齐读音的词，每项 {\"i\":下标,\"s\":该下标的表层形(原样照抄),\"r\":正确读音}；r 只能是平假名（可含 ー），且是整个词的读音（含词尾假名，如 静か→しずか）。\n" +
        "   - 含汉字或数字且 r 为 null 的词必须补读音（数字是重点：18→じゅうはち，3万2,000→さんまんにせん）。\n" +
        "   - 数字+量词的音变、同形多读（如 今日・一日・上手・方）是校对重点；跨词的音变要把各词读音分别改对（「一」「本」→ いっ、ぽん）。\n" +
        "   - 不含汉字和数字的词（假名词、拉丁字母等外文词、符号）不要给 fixes；读音已正确的词不要列出。\n" +
        "   - 不得改动原文、分词边界与词序；i 与 s 必须与输入一一对应。\n" +
        "{GLOSS}" +
        "输入可能带 context.full_text（全文，只用于理解上下文、统一人名地名等译法，不要翻译或输出它）；只处理并输出 sentences 中的句子。\n" +
        "输出必须覆盖输入的全部句子（seq 与输入完全一致，顺序相同），只输出 JSON，不要任何解释或代码块标记，格式：\n" +
        "{\"sentences\":[{\"seq\":0,\"tr\":\"…\",\"fixes\":[{\"i\":3,\"s\":\"…\",\"r\":\"…\"}]{GLOSSFMT}}]}";

    private const string GlossRule =
        "3. gloss：可选，为生词难点给出简短中文释义，键为词下标字符串，如 {\"5\":\"图书馆\"}；不求全覆盖。\n";

    private static string EnrichSystemFor(bool gloss) =>
        EnrichSystem.Replace("{GLOSS}", gloss ? GlossRule : "").Replace("{GLOSSFMT}", gloss ? ",\"gloss\":{\"5\":\"…\"}" : "");

    public const string InputMarker = "### INPUT\n";

    private static string RawUser(string text) => InputMarker + new JsonObject { ["text"] = text }.ToJsonString(Json.Options);

    /// <summary>Compact projection sent to the model (B6): [{i, s, r, unk?}], symbol/whitespace tokens dropped (i kept).</summary>
    public static JsonObject Project(IEnumerable<Sent> sents)
    {
        var arr = new JsonArray();
        foreach (var s in sents)
        {
            var toks = new JsonArray();
            foreach (var t in s.Toks)
            {
                if (t.Pos == "sym" || string.IsNullOrWhiteSpace(t.S)) continue;
                var o = new JsonObject { ["i"] = t.I, ["s"] = t.S, ["r"] = t.R };
                if (t.Unk) o["unk"] = true;
                toks.Add(o);
            }
            arr.Add(new JsonObject { ["seq"] = s.Seq, ["text"] = s.Text, ["toks"] = toks });
        }
        return new JsonObject { ["sentences"] = arr };
    }

    /// <summary>P2R-6: when the chunk is not the whole text, the full text rides along as read-only context.</summary>
    public static string EnrichUser(IReadOnlyCollection<Sent> chunk, string? fullText)
    {
        var o = Project(chunk);
        if (!string.IsNullOrEmpty(fullText))
        {
            var ctx = new JsonObject { ["full_text"] = fullText.Length > EnrichMaxChars ? fullText[..EnrichMaxChars] : fullText };
            o = new JsonObject { ["context"] = ctx, ["sentences"] = o["sentences"]!.DeepClone() };
        }
        return InputMarker + o.ToJsonString(Json.Options);
    }

    private static string RepairUser(List<string> failures) =>
        "上一次输出没有通过校验：\n" + string.Join("\n", failures.Take(8).Select(f => "- " + f)) +
        "\n请改正这些问题，重新输出完整的 JSON（覆盖全部内容，只输出 JSON）。";

    // ---------------------------------------------------------------- content parsing + validation

    /// <summary>Parse model content as a JSON object (tolerates ``` fences and leading/trailing chatter).</summary>
    public static JsonObject? ParseContent(string content, out string? error)
    {
        error = null;
        var c = content.Trim();
        if (c.StartsWith("```"))
        {
            var nl = c.IndexOf('\n');
            c = nl >= 0 ? c[(nl + 1)..] : c[3..];
            if (c.EndsWith("```")) c = c[..^3];
            c = c.Trim();
        }
        foreach (var candidate in new[] { c, Slice(c) })
        {
            if (candidate == null) continue;
            try { if (JsonNode.Parse(candidate) is JsonObject o) return o; }
            catch (Exception e) { error = e.Message.Split('\n')[0]; }
        }
        error ??= "output is not a JSON object";
        return null;

        static string? Slice(string s)
        {
            var a = s.IndexOf('{');
            var b = s.LastIndexOf('}');
            return a >= 0 && b > a ? s[a..(b + 1)] : null;
        }
    }

    private static string Clip(string s, int n = 20) => s.Length > n ? s[..n] + "…" : s;

    public sealed record RawTok(string Word, string Pos, string Furigana);

    /// <summary>TASK6 §6 raw checks: tokens array; concat == text; furigana hiragana or empty; pos in the closed set.</summary>
    public static List<string> ValidateRaw(JsonObject o, string text, out List<RawTok> toks)
    {
        toks = new List<RawTok>();
        var errs = new List<string>();
        if (o["tokens"] is not JsonArray arr || arr.Count == 0) { errs.Add("缺少非空的 tokens 数组"); return errs; }
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject t || t["word"] is not JsonValue wv || !wv.TryGetValue<string>(out var w) || w.Length == 0)
            { errs.Add($"tokens[{i}].word 必须是非空字符串"); continue; }
            var pos = t["pos"] is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps.Trim() : "";
            var fu = t["furigana"] is JsonValue fv && fv.TryGetValue<string>(out var fs) ? ToHira(fs.Trim()) : "";
            if (!PosMap.ContainsKey(pos)) errs.Add($"tokens[{i}]「{Clip(w)}」的 pos「{Clip(pos)}」不在允许的集合内");
            if (fu.Length > 0 && !IsHiragana(fu)) errs.Add($"tokens[{i}]「{Clip(w)}」的 furigana「{Clip(fu)}」不是纯平假名");
            else if (ReadingCoverageError(w, fu) is { } cov) errs.Add($"tokens[{i}] {cov}");
            toks.Add(new RawTok(w, pos, fu));
        }
        var joined = string.Concat(toks.Select(t => t.Word));
        if (joined != text)
        {
            var k = 0;
            while (k < joined.Length && k < text.Length && joined[k] == text[k]) k++;
            errs.Insert(0, $"word 拼接与原文不一致：第 {k} 个字符处原文为「{Clip(text[k..], 12)}」，拼接结果为「{Clip(joined[k..], 12)}」");
        }
        return errs;
    }

    public sealed record Fix(int I, string S, string R);
    public sealed record SentOut(long Seq, string Tr, List<Fix> Fixes, JsonObject? Gloss, List<string> MissingReadings, int DroppedFixes);

    /// <summary>TASK6 §6 enrich checks per chunk (+O8 s check, gap-6 exclusion). Returns failures; fills outs when clean.</summary>
    public static List<string> ValidateEnrich(JsonObject o, IReadOnlyList<Sent> chunk, out List<SentOut> outs)
    {
        outs = new List<SentOut>();
        var errs = new List<string>();
        if (o["sentences"] is not JsonArray arr) { errs.Add("缺少 sentences 数组"); return errs; }
        var want = chunk.ToDictionary(s => s.Seq);
        var got = new Dictionary<long, JsonObject>();
        foreach (var n in arr)
        {
            if (n is not JsonObject so || so["seq"] is not JsonValue sv || !sv.TryGetValue<long>(out var seq))
            { errs.Add("sentences 中有项缺少整数 seq"); continue; }
            if (!want.ContainsKey(seq)) { errs.Add($"seq {seq} 不在输入中"); continue; }
            if (!got.TryAdd(seq, so)) errs.Add($"seq {seq} 重复");
        }
        var missing = want.Keys.Where(k => !got.ContainsKey(k)).ToList();
        if (missing.Count > 0) errs.Add($"缺少 seq：{string.Join(",", missing.Take(10))}");

        foreach (var s in chunk)
        {
            if (!got.TryGetValue(s.Seq, out var so)) continue;
            var tr = so["tr"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts.Trim() : "";
            if (tr.Length == 0) errs.Add($"seq {s.Seq} 的 tr 为空");
            else if (tr.Length > 500) errs.Add($"seq {s.Seq} 的 tr 超过 500 字");
            var fixes = new List<Fix>();
            var dropped = 0;
            var byI = new Dictionary<int, string>();
            if (so["fixes"] is JsonArray fa)
            {
                foreach (var fn in fa)
                {
                    if (fn is not JsonObject fo || fo["i"] is not JsonValue iv || !iv.TryGetValue<int>(out var i))
                    { errs.Add($"seq {s.Seq} 的 fixes 项缺少整数 i"); continue; }
                    var fs = fo["s"] is JsonValue fsv && fsv.TryGetValue<string>(out var fss) ? fss : null;
                    var fr = fo["r"] is JsonValue frv && frv.TryGetValue<string>(out var frs) ? ToHira(frs.Trim()) : "";
                    if (i < 0 || i >= s.Toks.Count) { errs.Add($"seq {s.Seq} 的 fixes 下标 i={i} 越界（该句 toks 共 {s.Toks.Count} 个）"); continue; }
                    var tok = s.Toks[i];
                    if (fs != tok.S) { errs.Add($"seq {s.Seq} 的 fixes i={i} 的 s「{Clip(fs ?? "null")}」与该下标的词「{Clip(tok.S)}」不一致"); continue; }
                    if (!IsHiragana(fr)) { errs.Add($"seq {s.Seq} 的 fixes i={i}「{Clip(tok.S)}」的读音「{Clip(fr)}」不是纯平假名"); continue; }
                    if (tok.Pos == "sym" || !NeedsReading(tok.S)) { dropped++; continue; }   // no reading needed (kana/Latin/symbol)
                    if (ReadingCoverageError(tok.S, fr) is { } cov) { errs.Add($"seq {s.Seq} 的 fixes i={i} {cov}"); continue; }
                    if (fr == tok.R) { dropped++; continue; }                               // no-op
                    if (byI.TryGetValue(i, out var prev))
                    {
                        if (prev != fr) errs.Add($"seq {s.Seq} 的 fixes 对 i={i} 给了两个不同读音");
                        continue;
                    }
                    byI[i] = fr;
                    fixes.Add(new Fix(i, tok.S, fr));
                }
            }
            else if (so["fixes"] is not null) errs.Add($"seq {s.Seq} 的 fixes 必须是数组");

            JsonObject? gloss = null;
            if (so["gloss"] is JsonObject go)
            {
                gloss = new JsonObject();
                foreach (var kv in go)
                    if (int.TryParse(kv.Key, out var gi) && gi >= 0 && gi < s.Toks.Count && kv.Value is JsonValue gvv && gvv.TryGetValue<string>(out var gs) && gs.Trim().Length > 0)
                        gloss[gi.ToString()] = gs.Trim();
                    else dropped++;
                if (gloss.Count == 0) gloss = null;
            }
            var stillMissing = s.Toks.Where(t => t.Pos != "sym" && NeedsReading(t.S) && string.IsNullOrEmpty(t.R) && !byI.ContainsKey(t.I))
                .Select(t => $"{t.I}:{t.S}").ToList();
            outs.Add(new SentOut(s.Seq, tr, fixes, gloss, stillMissing, dropped));
        }
        if (errs.Count > 0) outs.Clear();
        return errs;
    }

    // ---------------------------------------------------------------- chunking

    public static List<List<Sent>> MakeChunks(IReadOnlyList<Sent> sents, int maxSents = ChunkMaxSentences, int maxChars = ChunkMaxChars)
    {
        var chunks = new List<List<Sent>>();
        var cur = new List<Sent>();
        var chars = 0;
        foreach (var s in sents)
        {
            if (cur.Count > 0 && (cur.Count >= maxSents || chars + s.Text.Length > maxChars))
            {
                chunks.Add(cur);
                cur = new List<Sent>();
                chars = 0;
            }
            cur.Add(s);
            chars += s.Text.Length;
        }
        if (cur.Count > 0) chunks.Add(cur);
        return chunks;
    }

    // ---------------------------------------------------------------- engine

    private enum CallKind { Content, Length, Next, SkipProvider, Fatal }

    private sealed record CallResult(CallKind Kind, string? Content, string Code, ApiError? Error);

    /// <summary>
    /// How a run is executed. Direct POST /api/analyze: ANALYZE_TIMEOUT_S per call, ANALYZE_DEADLINE_S overall,
    /// global 20/1500 chunks, no circuit (phase-1 behaviour). Ingest (phase 2): per-model call timeout and chunk
    /// size from the ProviderDef, re-chunking when the candidate switches provider, INGEST_DEADLINE_S, circuit
    /// breakers consulted and fed.
    /// </summary>
    public sealed class EngineOptions
    {
        public bool Ingest;
        public double DeadlineSeconds = Providers.AnalyzeDeadline;
        public static EngineOptions Direct() => new() { DeadlineSeconds = Providers.AnalyzeDeadline };
        public static EngineOptions ForIngest() => new() { Ingest = true, DeadlineSeconds = Providers.IngestDeadline };
        public double CallTimeout(ProviderDef p, string model) => Ingest ? p.CallTimeoutS(model) : Providers.CallTimeout;
    }

    /// <summary>Shared per-request state (thread-safe: chunks run with parallelism 2).</summary>
    private sealed class RunState
    {
        public required List<(ProviderDef P, string Model)> Cands;
        public required int Cap;
        public required DateTime Deadline;
        public required EngineOptions Opt;
        public int Calls;
        public int NonFourxx;          // > 0 once any call got a non-4xx answer (200, 5xx, timeout, network)
        public ApiError? Fatal;
        public int DeadlineHit;
        public int Probes;

        public bool TakeCall()
        {
            if (Interlocked.Increment(ref Calls) <= Cap) return true;
            Interlocked.Decrement(ref Calls);
            return false;
        }
    }

    private static string Short(ProviderDef p, string model) => p.Name + "/" + (model.StartsWith(p.Name + "-") ? model[(p.Name.Length + 1)..] : model);

    private static async Task<CallResult> CallModel(RunState st, ProviderDef p, string model, JsonArray messages, CancellationToken ct)
    {
        var remaining = (st.Deadline - DateTime.UtcNow).TotalSeconds;
        if (remaining < 5) return new CallResult(CallKind.Next, null, "deadline", null);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
        };
        if (p.ReasoningEffort.TryGetValue(model, out var effort)) payload["reasoning_effort"] = effort;
        JsonObject res;
        try
        {
            res = await Upstream.Call(p.Label, p.Url(), p.Key(), p.KeyName, HttpMethod.Post, "", payload,
                Math.Min(st.Opt.CallTimeout(p, model), remaining), ct);
        }
        catch (ApiError e)
        {
            if (e.Code == "upstream_throttled")   // our own token bucket: nothing was sent upstream
                return new CallResult(CallKind.Next, null, "rpm", e);
            var us = e.UpstreamStatus;
            if (us is >= 400 and < 500) { }
            else Interlocked.Increment(ref st.NonFourxx);
            var r = us switch
            {
                401 => new CallResult(CallKind.SkipProvider, null, "401", e),
                403 or 429 => new CallResult(CallKind.Next, null, us.ToString()!, e),
                >= 400 and < 500 => new CallResult(CallKind.Fatal, null, us.ToString()!, e),
                >= 500 => new CallResult(CallKind.Next, null, us.ToString()!, e),
                200 => new CallResult(CallKind.Next, null, "shape", e),       // non-JSON body
                _ => new CallResult(CallKind.Next, null, e.Status == 504 ? "timeout" : e.Status == 503 ? "nokey" : "net", e),
            };
            if (st.Opt.Ingest) FeedCircuit(p, model, r.Code, e);
            return r;
        }
        Interlocked.Increment(ref st.NonFourxx);
        CallResult ok;
        if (res["choices"] is not JsonArray { Count: > 0 } ch || ch[0] is not JsonObject choice)
            ok = new CallResult(CallKind.Next, null, "shape", null);
        else
        {
            var content = choice["message"]?["content"] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : null;
            var finish = choice["finish_reason"] is JsonValue fv && fv.TryGetValue<string>(out var fs) ? fs : null;
            ok = finish == "length" ? new CallResult(CallKind.Length, content, "len", null)
                : string.IsNullOrWhiteSpace(content) ? new CallResult(CallKind.Next, null, "empty", null)
                : new CallResult(CallKind.Content, content, "ok", null);
        }
        if (st.Opt.Ingest)
        {
            if (ok.Kind is CallKind.Content or CallKind.Length) Circuit.Success(p.Name, model);
            else Circuit.Failure(p.Name, model, "transient");
        }
        return ok;
    }

    /// <summary>Upstream failure → breaker class (plan §2.2): 429 / quota (403 + "quota") / auth / transient.</summary>
    private static void FeedCircuit(ProviderDef p, string model, string code, ApiError e)
    {
        switch (code)
        {
            case "429": Circuit.Failure(p.Name, model, "429", e.RetryAfterSeconds); break;
            case "403":
                Circuit.Failure(p.Name, model,
                    (e.UpstreamBody ?? "").Contains("quota", StringComparison.OrdinalIgnoreCase) ? "quota" : "transient");
                break;
            case "401" or "nokey": Circuit.Failure(p.Name, model, "auth"); break;
            case "rpm": break;
            default:
                if (e.UpstreamStatus is >= 400 and < 500) break;   // parameter 4xx: our bug, not the provider's
                Circuit.Failure(p.Name, model, "transient");
                break;
        }
    }

    private sealed class UnitResult
    {
        public bool Ok;
        public bool Split;
        public ProviderDef? SplitFor;    // phase 2: re-chunk for this provider (else: halve, finish_reason=length)
        public int CandIndex;
        public JsonObject? Parsed;
        public object? Value;            // List<RawTok> or List<SentOut>
        public string? Provider, Model;
        public bool ValidationFailure;   // last failure was content validation (vs transport)
        public List<string> Failures = new();
        public int Calls;
    }

    /// <summary>
    /// One unit of work (the raw text, or one enrich chunk) through the L1 ladder: per candidate one call,
    /// on bad content one repair retry on the same model, then the next candidate; finish_reason=length →
    /// split (if allowed). Ingest runs additionally skip open breakers and re-chunk when the next candidate's
    /// provider takes smaller chunks (P2R-7). Trail tokens are appended to <paramref name="trail"/>.
    /// </summary>
    private static async Task<UnitResult> RunUnit(RunState st, int startCand, int unitCap, bool canSplit,
        Func<JsonArray> buildMessages, Func<JsonObject, (List<string> errs, object? value)> validate,
        List<string> trail, CancellationToken ct, IReadOnlyCollection<Sent>? chunk = null)
    {
        var ur = new UnitResult();
        var skip = new HashSet<string>();
        for (var ci = startCand; ci < st.Cands.Count; ci++)
        {
            var (p, model) = st.Cands[ci];
            if (skip.Contains(p.Name)) continue;
            if (st.Opt.Ingest)
            {
                if (Circuit.OpenReason(p.Name, model) is { } why)
                {
                    trail.Add(Short(p, model) + ":open-" + why);
                    ur.Failures = new List<string> { $"{p.Name}:{model}: circuit open ({why})" };
                    continue;
                }
                if (canSplit && chunk != null && !p.Fits(chunk))
                {
                    trail.Add(Short(p, model) + ":rechunk");
                    ur.Split = true; ur.SplitFor = p; ur.CandIndex = ci;
                    return ur;
                }
                if (Circuit.IsProbe(p.Name, model)) { Interlocked.Increment(ref st.Probes); trail.Add("probe"); }
            }
            var messages = buildMessages();
            for (var rep = 0; rep < 2; rep++)
            {
                if (ur.Calls >= unitCap || !st.TakeCall())
                {
                    trail.Add("cap");
                    ur.Failures = ur.Failures.Count > 0 ? ur.Failures : new List<string> { "upstream call budget exhausted" };
                    return ur;
                }
                ur.Calls++;
                var r = await CallModel(st, p, model, messages, ct);
                var tag = (rep == 1 ? "r" : Short(p, model)) + ":";
                switch (r.Kind)
                {
                    case CallKind.Fatal:
                        trail.Add(tag + r.Code);
                        st.Fatal ??= r.Error;
                        ur.Failures = new List<string> { r.Error?.Message ?? "upstream rejected the request" };
                        return ur;
                    case CallKind.SkipProvider:
                        trail.Add(tag + r.Code);
                        skip.Add(p.Name);
                        ur.ValidationFailure = false;
                        ur.Failures = new List<string> { $"{p.Name}: {r.Error?.Message}" };
                        goto nextCandidate;
                    case CallKind.Next:
                        trail.Add(tag + r.Code);
                        if (r.Code == "deadline")
                        {
                            Interlocked.Increment(ref st.DeadlineHit);
                            ur.Failures = new List<string> { "analyze deadline reached" };
                            return ur;
                        }
                        if (r.Code == "rpm") { Interlocked.Decrement(ref st.Calls); ur.Calls--; }   // throttled locally: nothing sent
                        ur.ValidationFailure = false;
                        ur.Failures = new List<string> { $"{p.Name}:{model}: {r.Error?.Message ?? r.Code}" };
                        goto nextCandidate;
                    case CallKind.Length:
                        trail.Add(tag + "len");
                        if (canSplit) { ur.Split = true; ur.CandIndex = ci; return ur; }
                        ur.ValidationFailure = true;
                        ur.Failures = new List<string> { "output truncated (finish_reason=length)" };
                        goto nextCandidate;
                    default:
                        var parsed = ParseContent(r.Content!, out var perr);
                        List<string> errs;
                        object? value = null;
                        if (parsed == null) errs = new List<string> { "输出不是合法 JSON：" + perr };
                        else (errs, value) = validate(parsed);
                        if (errs.Count == 0)
                        {
                            trail.Add(tag + "ok");
                            ur.Ok = true; ur.Parsed = parsed; ur.Value = value; ur.Provider = p.Name; ur.Model = model;
                            return ur;
                        }
                        trail.Add(tag + (parsed == null ? "json" : "bad"));
                        ur.ValidationFailure = true;
                        ur.Failures = errs;
                        if (rep == 0)
                        {
                            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = r.Content!.Length > 8000 ? r.Content[..8000] : r.Content });
                            messages.Add(new JsonObject { ["role"] = "user", ["content"] = RepairUser(errs) });
                        }
                        break;
                }
            }
            nextCandidate: ;
        }
        if (ur.Failures.Count == 0) ur.Failures.Add("no candidate left");
        return ur;
    }

    private static List<(ProviderDef P, string Model)> ResolveCandidates(Req req) =>
        Providers.Candidates(req.Provider, req.Model, Providers.DefaultProvider, Providers.Order, Providers.Ready);

    private static ApiError NoProvider() =>
        new(503, "no analyze provider is ready (configure AGNES_API_KEY or GEMINI_API_KEY)") { Code = "no_provider_available" };

    /// <summary>Final error for a failed request; also sets the refund flag (only if every upstream call was a 4xx).</summary>
    private static ApiError Failure(RunState st, CallOutcome oc, bool validation, JsonObject extra)
    {
        oc.Refund = Volatile.Read(ref st.NonFourxx) == 0;   // every call a 4xx, or none sent (all throttled locally)
        if (st.Fatal is { } f)
        {
            extra["provider_error"] = f.Message;
            return new ApiError(502, "analyze upstream rejected the request") { Code = "upstream_rejected", UpstreamStatus = f.UpstreamStatus, UpstreamBody = f.UpstreamBody, Extra = extra };
        }
        return validation
            ? new ApiError(502, "model output failed validation after repair and fallback") { Code = "analyze_validation_failed", Extra = extra }
            : new ApiError(502, "all analyze providers failed") { Code = "upstream_failed", Extra = extra };
    }

    public static Task<JsonObject> Run(Req req, CallOutcome oc, CancellationToken ct) =>
        req.Mode == "raw" ? RunRaw(req, oc, ct) : RunEnrich(req, oc, ct);

    // ---------------------------------------------------------------- raw

    private static async Task<JsonObject> RunRaw(Req req, CallOutcome oc, CancellationToken ct)
    {
        var cands = ResolveCandidates(req);
        if (cands.Count == 0) { oc.Detail = "m=raw;no_provider"; throw NoProvider(); }
        var opt = EngineOptions.Direct();
        var st = new RunState { Cands = cands, Cap = RawCallCap, Deadline = DateTime.UtcNow.AddSeconds(opt.DeadlineSeconds), Opt = opt };
        var text = req.Text!;
        var trail = new List<string>();
        var ur = await RunUnit(st, 0, RawCallCap, false,
            () => new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = RawSystem },
                new JsonObject { ["role"] = "user", ["content"] = RawUser(text) }),
            o => { var e = ValidateRaw(o, text, out var t); return (e, t); },
            trail, ct);
        oc.Detail = $"m=raw;a={st.Calls};fb={string.Join(">", trail)};v=" + (ur.Ok ? "ok" : "fail:" + Clip(ur.Failures.FirstOrDefault() ?? "", 120));
        if (!ur.Ok)
        {
            oc.Model = st.Cands[0].P.Name + ":" + st.Cands[0].Model;
            throw Failure(st, oc, ur.ValidationFailure, new JsonObject
            {
                ["attempts"] = st.Calls,
                ["trail"] = new JsonArray(trail.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["details"] = new JsonArray(ur.Failures.Take(20).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            });
        }
        oc.Model = ur.Provider + ":" + ur.Model;
        var raw = (List<RawTok>)ur.Value!;
        var toks = new JsonArray();
        var tokens = new JsonArray();
        foreach (var t in raw)
        {
            var pos = PosMap[t.Pos];
            string? r = t.Furigana.Length > 0 ? t.Furigana
                : pos == "sym" ? ""
                : t.Word.All(IsKana) ? ToHira(t.Word) : null;
            toks.Add(new JsonObject { ["s"] = t.Word, ["pos"] = pos, ["r"] = r, ["ruby"] = Ruby(t.Word, r) });
            tokens.Add(new JsonObject { ["word"] = t.Word, ["pos"] = t.Pos, ["furigana"] = t.Furigana });
        }
        return new JsonObject
        {
            ["mode"] = "raw", ["provider"] = ur.Provider, ["model"] = ur.Model,
            ["prompt_version"] = RawPromptVersion, ["attempts"] = st.Calls,
            ["trail"] = new JsonArray(trail.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            ["toks"] = toks, ["tokens"] = tokens,
        };
    }

    // ---------------------------------------------------------------- enrich

    private sealed class ChunkRec
    {
        public required string Label;
        public required List<Sent> Sents;
        public List<string> Trail = new();
        public UnitResult? Result;
    }

    /// <summary>Structured enrich result (phase 2: the ingest job consumes this instead of the HTTP JSON).</summary>
    public sealed class EnrichOutcome
    {
        public required List<SentOut> Outs;                                   // sentences of chunks that passed
        public required Dictionary<long, (string Provider, string Model)> By;  // seq → who produced it
        public required List<long> FailedSeqs;
        public required bool ValidationFailure;                               // some failed chunk ended in validation
        public required ApiError? Fatal;
        public required bool DeadlineHit;
        public required int Calls;
        public required int Probes;
        public required bool NoCandidate;                                     // every candidate open / not ready
        public required string Detail;
        public required JsonArray ChunksJson;
        public required List<(string Key, int N)> Usage;                     // provider:model → sentences, desc
        public required List<string> AllTrail;
        public required HashSet<string> ValidationFailedProviders;            // E7 input
        public required HashSet<string> OkProviders;
        public required JsonArray FailedDetails;                              // [{chunk, seq, failures}]
        public bool Complete => FailedSeqs.Count == 0 && Fatal == null;
    }

    /// <summary>
    /// Enrich engine shared by direct analyze and ingest. Chunks run with parallelism 2; per chunk the L1 ladder
    /// (RunUnit); finish_reason=length → halve; ingest: candidate on a smaller-chunk provider → re-chunk (P2R-7).
    /// Every chunk request carries the full text as context when the chunk is not the whole text (P2R-6).
    /// </summary>
    public static async Task<(EnrichOutcome Outcome, int NonFourxx)> RunEnrichCore(List<Sent> sents,
        List<(ProviderDef P, string Model)> cands, bool gloss, EngineOptions opt, CancellationToken ct)
    {
        var initial = opt.Ingest && cands.Count > 0
            ? MakeChunks(sents, cands[0].P.ChunkSents(), cands[0].P.ChunkChars())
            : MakeChunks(sents);
        var cap = opt.Ingest ? Math.Min(60, 2 * initial.Count + 4) : Math.Min(EnrichCallCapMax, 2 * initial.Count + 4);
        var st = new RunState { Cands = cands, Cap = cap, Deadline = DateTime.UtcNow.AddSeconds(opt.DeadlineSeconds), Opt = opt };
        var system = EnrichSystemFor(gloss);
        var fullText = string.Concat(sents.Select(s => s.Text));
        var done = new ConcurrentBag<ChunkRec>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(ChunkParallelism);

        async Task Process(ChunkRec rec, int startCand)
        {
            var chunk = rec.Sents;
            var ctxText = chunk.Count < sents.Count ? fullText : null;
            var ur = await RunUnit(st, startCand, ChunkCallCap, chunk.Count > 1,
                () => new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = EnrichUser(chunk, ctxText) }),
                o => { var e = ValidateEnrich(o, chunk, out var outs); return (e, outs); },
                rec.Trail, linked.Token, chunk);
            rec.Result = ur;
            done.Add(rec);
            if (st.Fatal != null) { linked.Cancel(); return; }
            if (ur.Split && ur.SplitFor is { } target)
            {
                var parts = MakeChunks(chunk, target.ChunkSents(), target.ChunkChars());
                if (parts.Count < 2) parts = new List<List<Sent>> { chunk.Take(chunk.Count / 2).ToList(), chunk.Skip(chunk.Count / 2).ToList() };
                Interlocked.Add(ref st.Cap, Math.Min(2 * (parts.Count - 1), Math.Max(0, 60 - st.Cap)));
                for (var i = 0; i < parts.Count; i++)
                    await Process(new ChunkRec { Label = rec.Label + "." + i, Sents = parts[i] }, ur.CandIndex);
            }
            else if (ur.Split)
            {
                var half = chunk.Count / 2;
                await Process(new ChunkRec { Label = rec.Label + "a", Sents = chunk.Take(half).ToList() }, ur.CandIndex);
                await Process(new ChunkRec { Label = rec.Label + "b", Sents = chunk.Skip(half).ToList() }, ur.CandIndex);
            }
        }

        var tasks = initial.Select(async (chunk, n) =>
        {
            await gate.WaitAsync(linked.Token);
            try { await Process(new ChunkRec { Label = "c" + n, Sents = chunk }, 0); }
            finally { gate.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (st.Fatal != null && !ct.IsCancellationRequested) { /* fatal: handled below */ }

        var recs = done.OrderBy(r => r.Sents[0].Seq).ThenBy(r => r.Label.Length).ToList();
        string Rng(ChunkRec r) => r.Sents.Count == 1 ? $"{r.Sents[0].Seq}" : $"{r.Sents[0].Seq}-{r.Sents[^1].Seq}";
        var detail = new StringBuilder($"m=enrich;n={sents.Count};c={initial.Count};a={st.Calls}");
        foreach (var r in recs) detail.Append($";{r.Label}[{Rng(r)}]={string.Join(">", r.Trail)}");

        var leaves = recs.Where(r => r.Result is { Split: false }).ToList();
        var okLeaves = leaves.Where(r => r.Result!.Ok).ToList();
        var failed = leaves.Where(r => !r.Result!.Ok).ToList();
        var covered = new HashSet<long>(leaves.SelectMany(r => r.Sents.Select(s => s.Seq)));
        var uncovered = sents.Where(s => !covered.Contains(s.Seq)).Select(s => s.Seq).ToList();
        var outs = okLeaves.SelectMany(r => (List<SentOut>)r.Result!.Value!).OrderBy(o => o.Seq).ToList();
        var by = new Dictionary<long, (string, string)>();
        foreach (var r in okLeaves) foreach (var s in r.Sents) by[s.Seq] = (r.Result!.Provider!, r.Result!.Model!);
        var usage = okLeaves.GroupBy(r => r.Result!.Provider + ":" + r.Result!.Model)
            .Select(g => (g.Key, g.Sum(r => r.Sents.Count))).OrderByDescending(x => x.Item2).ToList();
        var chunksJson = new JsonArray(recs.Select(r => (JsonNode)new JsonObject
        {
            ["chunk"] = r.Label, ["seq"] = Rng(r),
            ["provider"] = r.Result?.Provider, ["model"] = r.Result?.Model,
            ["attempts"] = r.Result?.Calls ?? 0,
            ["result"] = r.Result is { Split: true } ? "split" : r.Result is { Ok: true } ? "ok" : "failed",
            ["trail"] = new JsonArray(r.Trail.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        }).ToArray());
        var failedSeqs = failed.SelectMany(r => r.Sents).Select(s => s.Seq).Concat(uncovered).Distinct().OrderBy(x => x).ToList();
        // providers whose attempts on a failed chunk ended in validation errors ("bad"/"json"/"len")
        var vfail = new HashSet<string>();
        foreach (var r in failed.Where(r => r.Result!.ValidationFailure))
            foreach (var t in r.Trail)
                if (t.EndsWith(":bad") || t.EndsWith(":json") || t.EndsWith(":len"))
                {
                    var name = t.Split('/')[0];
                    if (Providers.All.ContainsKey(name)) vfail.Add(name);
                }
        if (failed.Count > 0 || uncovered.Count > 0 || st.Fatal != null)
            detail.Append(";v=fail:" + Clip(failed.FirstOrDefault()?.Result?.Failures.FirstOrDefault() ?? "cancelled", 120));
        else detail.Append(";v=ok");
        var allTrail = recs.SelectMany(r => r.Trail).ToList();
        var outcome = new EnrichOutcome
        {
            Outs = outs, By = by, FailedSeqs = failedSeqs,
            ValidationFailure = failed.Any(r => r.Result!.ValidationFailure),
            Fatal = st.Fatal, DeadlineHit = st.DeadlineHit > 0, Calls = st.Calls, Probes = st.Probes,
            NoCandidate = okLeaves.Count == 0 && allTrail.Count > 0 && allTrail.All(t => t.Contains(":open-") || t == "probe"),
            Detail = detail.ToString(), ChunksJson = chunksJson, Usage = usage, AllTrail = allTrail,
            ValidationFailedProviders = vfail,
            OkProviders = okLeaves.Select(r => r.Result!.Provider!).ToHashSet(),
            FailedDetails = new JsonArray(failed.Select(r => (JsonNode)new JsonObject
            {
                ["chunk"] = r.Label, ["seq"] = Rng(r),
                ["failures"] = new JsonArray(r.Result!.Failures.Take(20).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            }).ToArray()),
        };
        return (outcome, st.NonFourxx);
    }

    private static async Task<JsonObject> RunEnrich(Req req, CallOutcome oc, CancellationToken ct)
    {
        var cands = ResolveCandidates(req);
        if (cands.Count == 0) { oc.Detail = "m=enrich;no_provider"; throw NoProvider(); }
        var (o, nonFourxx) = await RunEnrichCore(req.Sentences!, cands, req.Gloss, EngineOptions.Direct(), ct);
        oc.Detail = o.Detail;
        if (!o.Complete)
        {
            oc.Model = o.Usage.Count > 0 ? o.Usage[0].Key : cands[0].P.Name + ":" + cands[0].Model;
            oc.Refund = nonFourxx == 0;
            var extra = new JsonObject
            {
                ["attempts"] = o.Calls,
                ["chunks"] = o.ChunksJson,
                ["details"] = o.FailedDetails,
                ["failed_seqs"] = new JsonArray(o.FailedSeqs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["partial"] = SentencesJson(o.Outs),   // usable results of the chunks that passed
            };
            if (o.Fatal is { } f)
            {
                extra["provider_error"] = f.Message;
                throw new ApiError(502, "analyze upstream rejected the request") { Code = "upstream_rejected", UpstreamStatus = f.UpstreamStatus, UpstreamBody = f.UpstreamBody, Extra = extra };
            }
            throw o.ValidationFailure
                ? new ApiError(502, "model output failed validation after repair and fallback") { Code = "analyze_validation_failed", Extra = extra }
                : new ApiError(502, "all analyze providers failed") { Code = "upstream_failed", Extra = extra };
        }
        var main = o.Usage[0].Key.Split(':', 2);
        oc.Model = o.Usage[0].Key;
        var res = new JsonObject
        {
            ["mode"] = "enrich", ["provider"] = main[0], ["model"] = main[1],
            ["prompt_version"] = EnrichPromptVersion, ["attempts"] = o.Calls,
            ["chunks"] = o.ChunksJson,
            ["sentences"] = SentencesJson(o.Outs),
        };
        var warnings = new JsonArray();
        foreach (var s in o.Outs)
        {
            if (s.MissingReadings.Count > 0)
                warnings.Add(new JsonObject { ["code"] = "readings_missing", ["seq"] = s.Seq, ["toks"] = new JsonArray(s.MissingReadings.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) });
            if (s.DroppedFixes > 0)
                warnings.Add(new JsonObject { ["code"] = "fixes_dropped", ["seq"] = s.Seq, ["count"] = s.DroppedFixes });
        }
        if (warnings.Count > 0) res["warnings"] = warnings;
        return res;
    }

    private static JsonArray SentencesJson(List<SentOut> outs) =>
        new(outs.Select(o =>
        {
            var j = new JsonObject
            {
                ["seq"] = o.Seq, ["tr"] = o.Tr,
                ["fixes"] = new JsonArray(o.Fixes.Select(f => (JsonNode)new JsonObject { ["tok_i"] = f.I, ["s"] = f.S, ["reading"] = f.R }).ToArray()),
            };
            if (o.Gloss != null) j["gloss"] = o.Gloss.DeepClone();
            return (JsonNode)j;
        }).ToArray());

    // ---------------------------------------------------------------- hidden self-test (no test project: csproj excludes tests/**)

    public static int SelfTest()
    {
        var fails = 0;
        var n = 0;
        void Check(string name, bool ok, string? info = null)
        {
            n++;
            if (!ok) fails++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(info != null ? "  -> " + info : "")}");
        }
        string RubyStr(string s, string? r) => Ruby(s, r)?.ToJsonString(Json.Options) ?? "null";

        // ruby (TASK6 §6 / §11): okurigana, pure kana, number words, no suffix, whole-word fallback
        Check("ruby 送假名 買っ/かっ", RubyStr("買っ", "かっ") == "[{\"t\":\"買\",\"r\":\"か\"},{\"t\":\"っ\"}]", RubyStr("買っ", "かっ"));
        Check("ruby 送假名 食べる/たべる", RubyStr("食べる", "たべる") == "[{\"t\":\"食\",\"r\":\"た\"},{\"t\":\"べる\"}]", RubyStr("食べる", "たべる"));
        Check("ruby 送假名 静かな/しずかな", RubyStr("静かな", "しずかな") == "[{\"t\":\"静\",\"r\":\"しず\"},{\"t\":\"かな\"}]", RubyStr("静かな", "しずかな"));
        Check("ruby 送假名 読ん/よん", RubyStr("読ん", "よん") == "[{\"t\":\"読\",\"r\":\"よ\"},{\"t\":\"ん\"}]", RubyStr("読ん", "よん"));
        Check("ruby 纯平假名 これ → null", RubyStr("これ", "これ") == "null");
        Check("ruby 纯片假名 テスト → null", RubyStr("テスト", "てすと") == "null");
        Check("ruby 符号 。 → null", RubyStr("。", "") == "null");
        Check("ruby 拉丁 quiet → null", RubyStr("quiet", "くわいえっと") == "null");
        Check("ruby r 空 → null", RubyStr("図書館", null) == "null" && RubyStr("図書館", "") == "null");
        Check("ruby 无后缀 図書館", RubyStr("図書館", "としょかん") == "[{\"t\":\"図書館\",\"r\":\"としょかん\"}]");
        Check("ruby 数字词 18/じゅうはち", RubyStr("18", "じゅうはち") == "[{\"t\":\"18\",\"r\":\"じゅうはち\"}]");
        Check("ruby 数字词 一本/いっぽん", RubyStr("一本", "いっぽん") == "[{\"t\":\"一本\",\"r\":\"いっぽん\"}]");
        Check("ruby 数字+假名尾 3つ/みっつ", RubyStr("3つ", "みっつ") == "[{\"t\":\"3\",\"r\":\"みっ\"},{\"t\":\"つ\"}]", RubyStr("3つ", "みっつ"));
        Check("ruby 整词注音 お茶/おちゃ", RubyStr("お茶", "おちゃ") == "[{\"t\":\"お茶\",\"r\":\"おちゃ\"}]");
        Check("ruby 送假名 来た/きた",RubyStr("来た", "きた") == "[{\"t\":\"来\",\"r\":\"き\"},{\"t\":\"た\"}]", RubyStr("来た", "きた"));
        Check("ruby 词中假名 値上げ/ねあげ（只剥尾部送假名）",RubyStr("値上げ", "ねあげ") == "[{\"t\":\"値上\",\"r\":\"ねあ\"},{\"t\":\"げ\"}]", RubyStr("値上げ", "ねあげ"));
        Check("ruby 后缀吃尽读音 → 整词", RubyStr("々か", "か") == "[{\"t\":\"々か\",\"r\":\"か\"}]", RubyStr("々か", "か"));

        // pos map (B3)
        Check("pos 代名詞→pronoun", PosMap["代名詞"] == "pronoun");
        Check("pos 形容動詞→adj", PosMap["形容動詞"] == "adj");
        Check("pos 接頭辞/接尾辞→affix", PosMap["接頭辞"] == "affix" && PosMap["接尾辞"] == "affix");
        Check("pos 数詞→num", PosMap["数詞"] == "num");
        var readingEnum = new HashSet<string> { "noun", "pronoun", "verb", "adj", "adv", "conj", "particle", "aux", "affix", "sym", "num", "other" };
        Check("pos 映射值全部在 TtsWeb 枚举内", PosMap.Values.All(readingEnum.Contains));

        // content parsing
        Check("parse ```json fence", ParseContent("```json\n{\"a\":1}\n```", out _) is { } p1 && p1["a"]!.GetValue<int>() == 1);
        Check("parse chatter around JSON", ParseContent("好的：{\"a\":2} 完", out _) is { } p2 && p2["a"]!.GetValue<int>() == 2);
        Check("parse invalid → null", ParseContent("{\"a\":", out var perr) == null && perr != null);

        // raw validation
        var text = "彼女は本を読む。";
        var good = JsonNode.Parse("{\"tokens\":[{\"word\":\"彼女\",\"pos\":\"代名詞\",\"furigana\":\"かのじょ\"},{\"word\":\"は\",\"pos\":\"助詞\",\"furigana\":\"は\"},{\"word\":\"本\",\"pos\":\"名詞\",\"furigana\":\"ほん\"},{\"word\":\"を\",\"pos\":\"助詞\",\"furigana\":\"を\"},{\"word\":\"読む\",\"pos\":\"動詞\",\"furigana\":\"よむ\"},{\"word\":\"。\",\"pos\":\"記号\",\"furigana\":\"\"}]}")!.AsObject();
        Check("raw valid", ValidateRaw(good, text, out _).Count == 0);
        var badConcat = (JsonObject)good.DeepClone(); ((JsonArray)badConcat["tokens"]!).RemoveAt(3);
        var e1 = ValidateRaw(badConcat, text, out _);
        Check("raw concat mismatch reported with position", e1.Count == 1 && e1[0].Contains("第 4 个字符"), e1.FirstOrDefault());
        var badPos = (JsonObject)good.DeepClone(); badPos["tokens"]![0]!["pos"] = "pron";
        Check("raw pos outside closed set", ValidateRaw(badPos, text, out _).Any(x => x.Contains("pos")));
        var kataFu = (JsonObject)good.DeepClone(); kataFu["tokens"]![0]!["furigana"] = "カノジョ";
        Check("raw katakana furigana normalised", ValidateRaw(kataFu, text, out var kt).Count == 0 && kt[0].Furigana == "かのじょ");
        var stemOnly = JsonNode.Parse("{\"tokens\":[{\"word\":\"静かな\",\"pos\":\"形容動詞\",\"furigana\":\"しずか\"}]}")!.AsObject();
        Check("raw furigana must cover okurigana (静かな/しずか rejected)", ValidateRaw(stemOnly, "静かな", out _).Any(x => x.Contains("没有覆盖整个词")));
        var whole = JsonNode.Parse("{\"tokens\":[{\"word\":\"静かな\",\"pos\":\"形容動詞\",\"furigana\":\"しずかな\"}]}")!.AsObject();
        Check("raw 静かな/しずかな accepted", ValidateRaw(whole, "静かな", out _).Count == 0);
        Check("coverage: 三ヶ月/さんかげつ ok (ヶ not okurigana)", ReadingCoverageError("三ヶ月", "さんかげつ") == null);
        Check("coverage: 行った/いった ok, 行った/い rejected", ReadingCoverageError("行った", "いった") == null && ReadingCoverageError("行った", "い") != null);
        var romaji = (JsonObject)good.DeepClone(); romaji["tokens"]![0]!["furigana"] = "kanojo";
        Check("raw non-kana furigana rejected", ValidateRaw(romaji, text, out _).Any(x => x.Contains("平假名")));

        // enrich validation
        var sent = new Sent(7, "1本のquiet。", new List<Tok>
        {
            new(0, "1", null, true, "num"), new(1, "本", "ほん", false, "affix"), new(2, "の", "の", false, "particle"),
            new(3, "quiet", null, true, "noun"), new(4, "。", "", false, "sym"),
        });
        var chunk = new List<Sent> { sent };
        var eOk = JsonNode.Parse("{\"sentences\":[{\"seq\":7,\"tr\":\"一支的quiet。\",\"fixes\":[{\"i\":0,\"s\":\"1\",\"r\":\"いっ\"},{\"i\":1,\"s\":\"本\",\"r\":\"ぽん\"},{\"i\":3,\"s\":\"quiet\",\"r\":\"くわいえっと\"}]}]}")!.AsObject();
        var ee = ValidateEnrich(eOk, chunk, out var eo);
        Check("enrich valid; Latin unk fix dropped (gap 6)", ee.Count == 0 && eo[0].Fixes.Count == 2 && eo[0].DroppedFixes == 1, string.Join("; ", ee));
        var eOob = JsonNode.Parse("{\"sentences\":[{\"seq\":7,\"tr\":\"x\",\"fixes\":[{\"i\":9,\"s\":\"本\",\"r\":\"ほん\"}]}]}")!.AsObject();
        Check("enrich out-of-range i rejected", ValidateEnrich(eOob, chunk, out _).Any(x => x.Contains("越界")));
        var eMis = JsonNode.Parse("{\"sentences\":[{\"seq\":7,\"tr\":\"x\",\"fixes\":[{\"i\":2,\"s\":\"本\",\"r\":\"ほん\"}]}]}")!.AsObject();
        Check("enrich s mismatch rejected (O8)", ValidateEnrich(eMis, chunk, out _).Any(x => x.Contains("不一致")));
        var verb = new List<Sent> { new(3, "読ん", new List<Tok> { new(0, "読ん", "どくん", false, "verb") }) };
        var eCov = JsonNode.Parse("{\"sentences\":[{\"seq\":3,\"tr\":\"读\",\"fixes\":[{\"i\":0,\"s\":\"読ん\",\"r\":\"よ\"}]}]}")!.AsObject();
        Check("enrich fix must cover okurigana (読ん/よ rejected)", ValidateEnrich(eCov, verb, out _).Any(x => x.Contains("没有覆盖整个词")));
        var eSeq = JsonNode.Parse("{\"sentences\":[{\"seq\":8,\"tr\":\"x\"}]}")!.AsObject();
        Check("enrich seq set mismatch rejected", ValidateEnrich(eSeq, chunk, out _).Count >= 2);
        var eTr = JsonNode.Parse("{\"sentences\":[{\"seq\":7,\"tr\":\"  \"}]}")!.AsObject();
        Check("enrich empty tr rejected", ValidateEnrich(eTr, chunk, out _).Any(x => x.Contains("tr 为空")));
        var eMiss = JsonNode.Parse("{\"sentences\":[{\"seq\":7,\"tr\":\"x\",\"fixes\":[]}]}")!.AsObject();
        Check("enrich missing reading reported as warning", ValidateEnrich(eMiss, chunk, out var em).Count == 0 && em[0].MissingReadings.SequenceEqual(new[] { "0:1" }));
        var proj = Project(chunk).ToJsonString(Json.Options);
        Check("projection drops sym, keeps i/unk", proj == "{\"sentences\":[{\"seq\":7,\"text\":\"1本のquiet。\",\"toks\":[{\"i\":0,\"s\":\"1\",\"r\":null,\"unk\":true},{\"i\":1,\"s\":\"本\",\"r\":\"ほん\"},{\"i\":2,\"s\":\"の\",\"r\":\"の\"},{\"i\":3,\"s\":\"quiet\",\"r\":null,\"unk\":true}]}]}", proj);

        // chunking (B6)
        var many = Enumerable.Range(0, 45).Select(i => new Sent(i, "あいうえお", new List<Tok>())).ToList();
        Check("chunks by 20 sentences", MakeChunks(many).Select(c => c.Count).SequenceEqual(new[] { 20, 20, 5 }));
        var longs = Enumerable.Range(0, 5).Select(i => new Sent(i, new string('あ', 600), new List<Tok>())).ToList();
        Check("chunks by ~1500 chars", MakeChunks(longs).Select(c => c.Count).SequenceEqual(new[] { 2, 2, 1 }));

        // candidate order (O1: same-provider model fallback before switching provider)
        string Cs(List<(ProviderDef P, string Model)> l) => string.Join(",", l.Select(c => c.Model));
        Func<ProviderDef, bool> all = _ => true, agnesOnly = p => p.Name == "agnes";
        Check("cands default", Cs(Providers.Candidates(null, null, "agnes", new[] { "agnes", "gemini" }, all)) == "agnes-3.0-flash,agnes-2.5-flash,gemini-flash-lite-latest,gemini-flash-latest");
        Check("cands requested provider first", Cs(Providers.Candidates("gemini", null, "agnes", new[] { "agnes", "gemini" }, all)) == "gemini-flash-lite-latest,gemini-flash-latest,agnes-3.0-flash,agnes-2.5-flash");
        Check("cands requested model first", Cs(Providers.Candidates(null, "agnes-2.5-flash", "agnes", new[] { "agnes", "gemini" }, all)) == "agnes-2.5-flash,agnes-3.0-flash,gemini-flash-lite-latest,gemini-flash-latest");
        Check("cands skip not-ready", Cs(Providers.Candidates(null, null, "agnes", new[] { "agnes", "gemini" }, agnesOnly)) == "agnes-3.0-flash,agnes-2.5-flash");
        Check("order parse ignores junk", string.Join(",", Providers.ParseOrder(" Gemini, foo ,agnes,gemini")) == "gemini,agnes");
        Check("ready: key '#…' rejected", !Providers.KeyLooksValid("# fill me") && Providers.KeyLooksValid("abc123"));
        Check("ready: url must be absolute http(s)", Providers.UrlLooksValid(Providers.DefaultGeminiUrl) && !Providers.UrlLooksValid("# x") && !Providers.UrlLooksValid("ftp://x/y"));

        // redaction covers the new sensitive keys (B7) — names only, no values printed
        Check("redact key set", Upstream.SensitiveKeys.SequenceEqual(new[] { "AGNES_API_KEY", "JWT_SECRET", "GEMINI_API_KEY", "TTSWEB_TOKEN" }));

        // ---------------- TASK6 phase 2
        // fixes triage (§2.3): fill-in applied; rewrite rejected unless counter table / dictionary
        var tsent = new List<FixTriage.Tok>
        {
            new("大雪山", "たいせつざん", false, "noun"), new("の", "の", false, "particle"), new("一", "いち", false, "num"),
            new("本", "ほん", false, "affix"), new("と", "と", false, "particle"), new("18", null, true, "num"), new("年", "ねん", false, "affix"),
        };
        var tmap = new Dictionary<long, (string, List<FixTriage.Tok>)> { [0] = ("大雪山の一本と18年", tsent) };
        var (tri, pend) = FixTriage.Triage(tmap, new[] { (0L, 0, "大雪山", "だいせつざん"), (0L, 2, "一", "いっ"), (0L, 3, "本", "ぽん"), (0L, 5, "18", "じゅうはち") });
        Check("triage: 18 / 一 (pos=num) fill-in applied, 本 counter rewrite applied", tri.Applied.Count == 3 && tri.Applied.Any(x => x.Kind == "fill" && x.S == "18")
              && tri.Applied.Any(x => x.Kind == "fill" && x.S == "一") && tri.Applied.Single(x => x.Kind == "counter").S == "本", tri.Fx);
        Check("triage: 大雪山 rewrite pending (not applied)", pend.Count == 1 && pend[0].S == "大雪山" && !tri.Apply[0].Any(x => x.I == 0));
        FixTriage.Resolve(tri, pend, null);
        Check("triage: no dictionary verdict → suspect, fx=a3/r0/s1", tri.Fx == "a3/r0/s1", tri.Fx);
        var (tri2, pend2) = FixTriage.Triage(tmap, new[] { (0L, 0, "大雪山", "だいせつざん") });
        FixTriage.Resolve(tri2, pend2, new bool?[] { false });
        Check("triage: dictionary says no → rejected", tri2.Fx == "a0/r1/s0", tri2.Fx);
        var (tri3, pend3) = FixTriage.Triage(tmap, new[] { (0L, 0, "大雪山", "だいせつざん") });
        FixTriage.Resolve(tri3, pend3, new bool?[] { true });
        Check("triage: dictionary supports → applied (dict)", tri3.Fx == "a1/r0/s0" && tri3.Applied[0].Kind == "dict", tri3.Fx);
        Check("counter table: 本 after non-numeral not allowed", !FixTriage.CounterAllows(new List<FixTriage.Tok> { new("日", "ひ", false, "noun"), new("本", "ほん", false, "noun") }, 1, "ぽん"));
        Check("counter table: 六杯 → ろっ/ぱい", FixTriage.CounterAllows(new List<FixTriage.Tok> { new("六", "ろく", false, "num"), new("杯", "はい", false, "affix") }, 0, "ろっ")
              && FixTriage.CounterAllows(new List<FixTriage.Tok> { new("六", "ろく", false, "num"), new("杯", "はい", false, "affix") }, 1, "ぱい"));

        // upstream cool-down hints (§2.2)
        Check("gemini RetryInfo retryDelay parsed", Upstream.RetryDelayOf("[{\"error\":{\"code\":429,\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"37s\"}]}}]") == 37);
        Check("no retryDelay → null", Upstream.RetryDelayOf("{\"error\":\"x\"}") == null);
        Check("bucket of labels", Upstream.BucketOf("Agnes API") == "agnes" && Upstream.BucketOf("Gemini API") == "gemini" && Upstream.BucketOf("x") == null);

        // provider chunking (P2R-7) + full-text context (P2R-6)
        var ag = Providers.All["agnes"]; var gm = Providers.All["gemini"];
        var twenty = Enumerable.Range(0, 20).Select(i => new Sent(i, "あいうえおかきくけこ", new List<Tok>())).ToList();
        Check("agnes 8/600, gemini 20/1500 defaults", ag.ChunkSents() == 8 && ag.ChunkChars() == 600 && gm.ChunkSents() == 20 && gm.ChunkChars() == 1500);
        Check("20-sentence gemini chunk does not fit agnes → re-chunk 8/8/4", gm.Fits(twenty) && !ag.Fits(twenty)
              && MakeChunks(twenty, ag.ChunkSents(), ag.ChunkChars()).Select(c => c.Count).SequenceEqual(new[] { 8, 8, 4 }));
        Check("ingest per-model timeouts lite 45 / flash 90 / agnes 120", gm.CallTimeoutS("gemini-flash-lite-latest") == 45 && gm.CallTimeoutS("gemini-flash-latest") == 90 && ag.CallTimeoutS("agnes-3.0-flash") == 120);
        var withCtx = EnrichUser(twenty.Take(2).ToList(), "全文テキスト");
        var noCtx = EnrichUser(twenty.Take(2).ToList(), null);
        Check("enrich-v2 user message carries context.full_text", withCtx.Contains("\"context\":{\"full_text\":\"全文テキスト\"}") && !noCtx.Contains("context"), withCtx[..Math.Min(80, withCtx.Length)]);
        Check("enrich prompt version v2", EnrichPromptVersion == "enrich-v2" && EnrichSystemFor(false).Contains("context.full_text"));

        // D8 kana ratio
        Check("kana ratio: Japanese high, Chinese 0", IngestWorker.KanaRatio("彼女は静かな図書館で本を読んでいる。") > 0.3 && IngestWorker.KanaRatio("我们今天去图书馆看书。") == 0);

        // reserved usernames (plan §1.2)
        Check("A001-style usernames reserved", AdminApi.ReservedUsername("A001") && AdminApi.ReservedUsername("a123") && !AdminApi.ReservedUsername("A0011") && !AdminApi.ReservedUsername("MuseAgent1"));

        // circuit (§2.2) — uses a private provider name so the real ones are untouched
        Circuit.Reset();
        Circuit.Failure("selftest", "m", "429", 30);
        Check("circuit 429 with retryDelay opens", Circuit.OpenReason("selftest", "m") == "rate");
        Circuit.Success("selftest", "m");
        Check("circuit success closes", Circuit.OpenReason("selftest", "m") == null);
        Circuit.Failure("selftest", "m", "transient"); Circuit.Failure("selftest", "m", "transient");
        Check("circuit transient: 2 failures still closed", Circuit.OpenReason("selftest", "m") == null);
        Circuit.Failure("selftest", "m", "transient");
        Check("circuit transient: 3rd failure opens", Circuit.OpenReason("selftest", "m") == "transient");
        Circuit.Failure("selftest", "q", "quota");
        Check("circuit quota (403) opens model only", Circuit.OpenReason("selftest", "q") == "quota" && Circuit.OpenReason("selftest", "z") == null);
        Circuit.Failure("selftest", "q", "auth");
        Check("circuit auth opens whole provider", Circuit.OpenReason("selftest", "z") == "auth");
        Circuit.Reset();
        Circuit.ArticleQuality("selftest", true); Circuit.ArticleQuality("selftest", true);
        Check("E7: 2 bad articles keep it closed", Circuit.OpenReason("selftest", "m") == null);
        Circuit.ArticleQuality("selftest", true);
        Check("E7: 3rd bad article in a row opens the provider (quality)", Circuit.OpenReason("selftest", "m") == "quality");
        Circuit.Reset();

        // upstream token bucket (P2R-4): interactive fails fast, ingest has its own sub-quota
        Environment.SetEnvironmentVariable("UPSTREAM_RPM_AGNES", "3");
        Environment.SetEnvironmentVariable("INGEST_UPSTREAM_RPM_AGNES", "2");
        var b1 = UpstreamLimiter.TryTake("agnes", true); var b2 = UpstreamLimiter.TryTake("agnes", true); var b3 = UpstreamLimiter.TryTake("agnes", true);
        var b4 = UpstreamLimiter.TryTake("agnes", false); var b5 = UpstreamLimiter.TryTake("agnes", false);
        Check("bucket: ingest 2/min sub-quota, total 3/min", b1 == 0 && b2 == 0 && b3 > 0 && b4 == 0 && b5 > 0, $"{b1} {b2} {b3:F1} {b4} {b5:F1}");
        Environment.SetEnvironmentVariable("UPSTREAM_RPM_AGNES", null);
        Environment.SetEnvironmentVariable("INGEST_UPSTREAM_RPM_AGNES", null);

        Console.WriteLine($"self-test: {n - fails}/{n} passed");
        return fails == 0 ? 0 : 1;
    }
}
