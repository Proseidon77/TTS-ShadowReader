using System.Text.Json.Nodes;

namespace AgnesService;

/// <summary>
/// fixes quality gate (TASK6 phase 2 §2.3, P2R-9 layer 1, zero dependencies). enrich fixes never go to the
/// database unchecked:
///   * fill-in  (original token r == null, or unk=true, or pos=num): already passed ValidateEnrich (hiragana,
///     okurigana coverage) → applied.
///   * rewrite  (original r non-empty): NOT applied by default. Released only when
///       ① numeral + counter sound change matches the local counter table (一本→いっ/ぽん, 六杯→ろっ/ぱい …), or
///       ② the TtsWeb dictionary check (POST /api/v1/ingest/readings_check, layer 2) supports the reading.
/// Counts go to the job and call_log detail as fx=a{applied}/r{rejected}/s{suspect}:
///   a = applied (fill-in + counter-table + dictionary-supported rewrites)
///   r = rewrites the dictionary check explicitly did NOT support (rejected)
///   s = rewrites left unapplied without a dictionary verdict (checker unavailable / not asked) = suspect
/// The check is deliberately NOT part of TtsWeb's commit validation (commit also serves L2 agy and humans).
/// </summary>
public static class FixTriage
{
    public sealed record Tok(string S, string? R, bool Unk, string? Pos);

    public sealed record Item(long Seq, int I, string S, string From, string To, string Kind, string Sentence);

    public sealed class Result
    {
        public readonly Dictionary<long, List<(int I, string S, string R)>> Apply = new();
        public readonly List<Item> Applied = new();
        public readonly List<Item> Rejected = new();
        public readonly List<Item> Suspect = new();
        public string Fx => $"a{Applied.Count}/r{Rejected.Count}/s{Suspect.Count}";

        public void Add(Item it)
        {
            Applied.Add(it);
            if (!Apply.TryGetValue(it.Seq, out var l)) Apply[it.Seq] = l = new();
            l.Add((it.I, it.S, it.To));
        }

        public JsonObject Json() => new()
        {
            ["fx"] = Fx,
            ["applied"] = Arr(Applied), ["rejected"] = Arr(Rejected), ["suspect"] = Arr(Suspect),
        };

        private static JsonArray Arr(IEnumerable<Item> items) => new(items.Take(50).Select(x => (JsonNode)new JsonObject
        {
            ["seq"] = x.Seq, ["tok"] = x.I, ["s"] = x.S, ["from"] = x.From, ["to"] = x.To, ["kind"] = x.Kind,
        }).ToArray());
    }

    // ---- local counter table (numeral + counter sound change). Numerals: allowed rewritten readings.
    private static readonly Dictionary<string, string[]> NumeralForms = new()
    {
        ["一"] = new[] { "いっ", "いち", "ひと" }, ["1"] = new[] { "いっ", "いち" },
        ["三"] = new[] { "さん" }, ["3"] = new[] { "さん" },
        ["六"] = new[] { "ろっ", "ろく" }, ["6"] = new[] { "ろっ", "ろく" },
        ["八"] = new[] { "はっ", "はち" }, ["8"] = new[] { "はっ", "はち" },
        ["十"] = new[] { "じゅっ", "じっ", "じゅう" }, ["10"] = new[] { "じゅっ", "じっ", "じゅう" },
        ["百"] = new[] { "ひゃっ", "ひゃく" }, ["100"] = new[] { "ひゃっ", "ひゃく" },
        ["何"] = new[] { "なん" },
    };

    /// <summary>Counters with their sound-change variants (h→b/p, k→g, s→z, t→d …).</summary>
    private static readonly Dictionary<string, string[]> CounterForms = new()
    {
        ["本"] = new[] { "ほん", "ぼん", "ぽん" }, ["匹"] = new[] { "ひき", "びき", "ぴき" },
        ["杯"] = new[] { "はい", "ばい", "ぱい" }, ["分"] = new[] { "ふん", "ぷん", "ぶん" },
        ["発"] = new[] { "はつ", "ぱつ" }, ["階"] = new[] { "かい", "がい" }, ["回"] = new[] { "かい" },
        ["百"] = new[] { "ひゃく", "びゃく", "ぴゃく" }, ["千"] = new[] { "せん", "ぜん" },
        ["軒"] = new[] { "けん", "げん" }, ["冊"] = new[] { "さつ" }, ["個"] = new[] { "こ" },
        ["歳"] = new[] { "さい" }, ["才"] = new[] { "さい" }, ["足"] = new[] { "そく", "ぞく" },
        ["泊"] = new[] { "はく", "ぱく" }, ["頭"] = new[] { "とう" }, ["杯目"] = new[] { "はいめ", "ばいめ", "ぱいめ" },
        ["枚"] = new[] { "まい" }, ["件"] = new[] { "けん" }, ["分間"] = new[] { "ふんかん", "ぷんかん" },
        ["回目"] = new[] { "かいめ" }, ["曲"] = new[] { "きょく" }, ["キロ"] = new[] { "きろ" },
        ["週間"] = new[] { "しゅうかん" }, ["か月"] = new[] { "かげつ" }, ["ヶ月"] = new[] { "かげつ" },
        ["カ月"] = new[] { "かげつ" }, ["ページ"] = new[] { "ぺーじ" }, ["点"] = new[] { "てん" },
        ["袋"] = new[] { "ふくろ", "ぶくろ" }, ["箱"] = new[] { "はこ", "ばこ" }, ["羽"] = new[] { "わ", "ば", "ぱ" },
        ["拍"] = new[] { "はく", "ぱく" }, ["敗"] = new[] { "はい", "ぱい" }, ["版"] = new[] { "はん", "ぱん" },
    };

    private static bool IsNumeral(Tok t) =>
        NumeralForms.ContainsKey(t.S) || t.Pos == "num";

    /// <summary>Rewrite allowed by the counter table? (numeral before a counter, or counter after a numeral)</summary>
    public static bool CounterAllows(IReadOnlyList<Tok> toks, int i, string to)
    {
        var t = toks[i];
        var next = i + 1 < toks.Count ? toks[i + 1] : null;
        var prev = i > 0 ? toks[i - 1] : null;
        if (NumeralForms.TryGetValue(t.S, out var nf) && next != null && CounterForms.ContainsKey(next.S) && nf.Contains(to)) return true;
        if (CounterForms.TryGetValue(t.S, out var cf) && prev != null && IsNumeral(prev) && cf.Contains(to)) return true;
        return false;
    }

    /// <summary>A fix fills a gap (r null / unk / pos=num) as opposed to rewriting an existing reading.</summary>
    public static bool IsFillIn(Tok t) => string.IsNullOrEmpty(t.R) || t.Unk || t.Pos == "num";

    /// <summary>
    /// Layer 1 split. <paramref name="toksBySeq"/>: preview skeleton; <paramref name="fixes"/>: (seq, i, s, r) after
    /// ValidateEnrich. Rewrites not released by the counter table are returned in <c>pending</c> for layer 2.
    /// </summary>
    public static (Result res, List<Item> pending) Triage(IReadOnlyDictionary<long, (string Text, List<Tok> Toks)> toksBySeq,
        IEnumerable<(long Seq, int I, string S, string R)> fixes)
    {
        var res = new Result();
        var pending = new List<Item>();
        foreach (var (seq, i, s, r) in fixes)
        {
            if (!toksBySeq.TryGetValue(seq, out var sent) || i < 0 || i >= sent.Toks.Count || sent.Toks[i].S != s) continue;
            var t = sent.Toks[i];
            if (IsFillIn(t)) { res.Add(new Item(seq, i, s, t.R ?? "", r, "fill", sent.Text)); continue; }
            if (r == t.R) continue;
            if (CounterAllows(sent.Toks, i, r)) { res.Add(new Item(seq, i, s, t.R!, r, "counter", sent.Text)); continue; }
            pending.Add(new Item(seq, i, s, t.R!, r, "rewrite", sent.Text));
        }
        return (res, pending);
    }

    /// <summary>Layer 2 verdicts: supported → applied, explicitly unsupported → rejected, no verdict → suspect.</summary>
    public static void Resolve(Result res, List<Item> pending, IReadOnlyList<bool?>? verdicts)
    {
        for (var k = 0; k < pending.Count; k++)
        {
            var v = verdicts != null && k < verdicts.Count ? verdicts[k] : null;
            var it = pending[k];
            if (v == true) res.Add(it with { Kind = "dict" });
            else if (v == false) res.Rejected.Add(it);
            else res.Suspect.Add(it);
        }
    }
}
