using System.Text.Json.Nodes;

namespace AgnesService;

/// <summary>
/// Circuit breakers for the ingest API path (TASK6 phase 2 §2.2 / DF-6, fallback design §2.2). In memory only
/// (a restart closes everything; worst case one extra call on a dead model). Fed and consulted by ingest runs;
/// direct POST /api/analyze is decoupled from it (plan §2.2: direct analyze unchanged).
///
///   per provider:model, transient (5xx / timeout / network)  3 consecutive failures within 10 min → open
///                       5 min, doubling on every re-open, max 60 min
///   per provider:model, 429                                   open for retryDelay / Retry-After, else 2 min
///   per provider:model, 403 with "quota" in the body          open 60 min
///   per provider, auth (401 / key missing)                    open 6 h (all models) → at most 4 probes a day (≤ 6)
///   per provider, quality (E7)                                3 articles in a row ending analyze_validation_failed
///                                                             on that provider → open 24 h (all models)
/// After the cool-down the next real call is the half-open probe: success closes, failure re-opens.
/// Quality *fixes* breaker: statistics only (fx= in call_log), no automatic opening (DF-6).
/// </summary>
public static class Circuit
{
    private sealed class State
    {
        public DateTime OpenUntil = DateTime.MinValue;
        public string? Reason;
        public int Consecutive;                 // transient failures in a row
        public DateTime FirstFailAt = DateTime.MinValue;
        public int TransientOpens;              // for the doubling cool-down
        public int QualityStreak;               // provider level: articles in a row that failed validation
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, State> States = new();

    private static State S(string key)
    {
        if (!States.TryGetValue(key, out var s)) States[key] = s = new State();
        return s;
    }

    private static string K(string provider, string model) => provider + ":" + model;

    public static int QuotaCooldownS => Settings.GetInt("CIRCUIT_QUOTA_COOLDOWN_S", 3600, 1, 86400);
    public static int RateCooldownS => Settings.GetInt("CIRCUIT_429_COOLDOWN_S", 120, 1, 86400);
    public static int AuthCooldownS => Settings.GetInt("CIRCUIT_AUTH_COOLDOWN_S", 6 * 3600, 1, 7 * 86400);
    public static int QualityCooldownS => Settings.GetInt("CIRCUIT_QUALITY_COOLDOWN_S", 24 * 3600, 1, 7 * 86400);
    public static int TransientBaseS => Settings.GetInt("CIRCUIT_TRANSIENT_COOLDOWN_S", 300, 1, 86400);
    public const int TransientThreshold = 3;
    public const int QualityThreshold = 3;

    /// <summary>Is provider:model (or the whole provider) currently open? Returns the reason when open.</summary>
    public static string? OpenReason(string provider, string model)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            var p = S(provider);
            if (p.OpenUntil > now) return p.Reason;
            var m = S(K(provider, model));
            return m.OpenUntil > now ? m.Reason : null;
        }
    }

    /// <summary>True when the half-open probe of provider:model is due (cool-down ran out after an open).</summary>
    public static bool IsProbe(string provider, string model)
    {
        lock (Gate)
        {
            var m = S(K(provider, model));
            var p = S(provider);
            return (m.Reason != null && m.OpenUntil <= DateTime.UtcNow) || (p.Reason != null && p.OpenUntil <= DateTime.UtcNow);
        }
    }

    public static void Success(string provider, string model)
    {
        lock (Gate)
        {
            var m = S(K(provider, model));
            m.Consecutive = 0; m.Reason = null; m.OpenUntil = DateTime.MinValue; m.TransientOpens = 0;
            var p = S(provider);
            if (p.OpenUntil <= DateTime.UtcNow && p.Reason is "auth") { p.Reason = null; }
        }
    }

    /// <summary>Record a failed upstream call. <paramref name="kind"/>: 429 | quota | auth | transient.</summary>
    public static void Failure(string provider, string model, string kind, double? retryAfterS = null)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            var m = S(K(provider, model));
            switch (kind)
            {
                case "429":
                    m.OpenUntil = now.AddSeconds(retryAfterS is > 0 and < 86400 ? retryAfterS.Value : RateCooldownS);
                    m.Reason = "rate";
                    break;
                case "quota":
                    m.OpenUntil = now.AddSeconds(QuotaCooldownS); m.Reason = "quota";
                    break;
                case "auth":
                    var p = S(provider);
                    p.OpenUntil = now.AddSeconds(AuthCooldownS); p.Reason = "auth";
                    break;
                default:
                    if (m.Consecutive == 0 || now - m.FirstFailAt > TimeSpan.FromMinutes(10)) { m.Consecutive = 0; m.FirstFailAt = now; }
                    m.Consecutive++;
                    if (m.Consecutive >= TransientThreshold || (m.Reason == "transient" && m.OpenUntil <= now))
                    {
                        var secs = Math.Min(3600, TransientBaseS * (1 << Math.Min(m.TransientOpens, 6)));
                        m.TransientOpens++;
                        m.OpenUntil = now.AddSeconds(secs); m.Reason = "transient"; m.Consecutive = 0;
                    }
                    break;
            }
        }
    }

    /// <summary>E7: one article finished; <paramref name="validationFailed"/> = it ended analyze_validation_failed on this provider.</summary>
    public static void ArticleQuality(string provider, bool validationFailed)
    {
        lock (Gate)
        {
            var p = S(provider);
            if (!validationFailed) { p.QualityStreak = 0; if (p.Reason == "quality" && p.OpenUntil <= DateTime.UtcNow) p.Reason = null; return; }
            p.QualityStreak++;
            if (p.QualityStreak >= QualityThreshold)
            {
                p.OpenUntil = DateTime.UtcNow.AddSeconds(QualityCooldownS); p.Reason = "quality"; p.QualityStreak = 0;
            }
        }
    }

    /// <summary>Test/ops helper: close every breaker.</summary>
    public static void Reset() { lock (Gate) States.Clear(); }

    /// <summary>/health "analyze.circuit": open breakers with reason and expiry only.</summary>
    public static JsonObject View()
    {
        var o = new JsonObject();
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            foreach (var (k, s) in States.OrderBy(x => x.Key))
                if (s.OpenUntil > now)
                    o[k] = new JsonObject { ["reason"] = s.Reason, ["open_until"] = AdminApi.Iso(s.OpenUntil) };
        }
        return o;
    }
}
