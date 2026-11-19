using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Npgsql;

namespace AgnesService;

public sealed record EffectiveLimits(int PerMinute, int PerDay, int VideoPerDay, int Concurrent, List<string> Overridden);

/// <summary>
/// Per-account limits on the generation endpoints (TASK4). See DESIGN.md "Per-account limits".
///
///  * counted endpoints: POST /api/text, /api/image, /api/video, /api/analyze, /api/reading/ingest (kind = text|image|video|analyze|ingest;
///    ingest (phase 2): ONE admission per job, its concurrency ticket is released right after admission (P2R-2);
///    one analyze admission per request regardless of internal repair/fallback calls; no separate sub-quota).
///    Video status polls, /api/models, /api/auth/* are NOT counted.
///  * per-minute rate: sliding 60 s window (log of admission timestamps), in memory.
///  * daily quota (all kinds together) + video sub-quota: calendar day in Asia/Tokyo, reset at 00:00 JST.
///    Stored in agnes_auth.usage_daily (survives restarts).
///  * concurrency: in-flight generation calls per account, plus one global cap. In memory.
///  * accounting: counted at admission; refunded (daily counter only) when Agnes rejects the request
///    with a 4xx, i.e. it cost nothing upstream. 5xx / timeouts / missing key still count.
///
/// Race safety: admission for one account runs under a per-account async lock (single instance)
/// AND inside a PostgreSQL transaction that row-locks the user (FOR UPDATE) before reading and
/// incrementing usage_daily, so concurrent requests can never both take the last unit of quota.
/// </summary>
public static class LimitService
{
    public static readonly string[] Kinds = { "text", "image", "video", "analyze", "ingest" };
    public const int ConcurrencyRetryAfterSeconds = 5;

    public static int DefaultPerMinute => Settings.GetInt("LIMITS_DEFAULT_PER_MINUTE", 10, 0);
    public static int DefaultPerDay => Settings.GetInt("LIMITS_DEFAULT_PER_DAY", 200, 0);
    public static int DefaultVideoPerDay => Settings.GetInt("LIMITS_DEFAULT_VIDEO_PER_DAY", 20, 0);
    public static int DefaultConcurrent => Settings.GetInt("LIMITS_DEFAULT_CONCURRENT", 2, 0);
    public static int GlobalConcurrent => Settings.GetInt("MAX_CONCURRENT", 6, 1);

    // ---------------------------------------------------------------- JST day boundary

    /// <summary>Asia/Tokyo (UTC+9, no DST). Fixed-offset fallback if tzdata is missing.</summary>
    public static readonly TimeZoneInfo Tokyo = LoadTokyo();

    private static TimeZoneInfo LoadTokyo()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST"); }
    }

    /// <summary>The quota day (JST calendar date) for an instant, and when it ends (next JST midnight).</summary>
    public static (DateOnly day, DateTimeOffset resetAt) QuotaDay(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Tokyo);
        var day = DateOnly.FromDateTime(local.DateTime);
        var nextMidnight = local.Date.AddDays(1);
        var reset = new DateTimeOffset(nextMidnight, Tokyo.GetUtcOffset(nextMidnight));
        return (day, reset);
    }

    // ---------------------------------------------------------------- in-memory state

    private sealed class UserWindow
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Queue<DateTimeOffset> Minute = new();   // admission times, last 60 s
        public int InFlight;
    }

    private static readonly ConcurrentDictionary<long, UserWindow> Windows = new();
    private static int _globalInFlight;

    private static UserWindow W(long uid) => Windows.GetOrAdd(uid, _ => new UserWindow());

    private static void Prune(UserWindow w, DateTimeOffset now)
    {
        while (w.Minute.Count > 0 && now - w.Minute.Peek() >= TimeSpan.FromSeconds(60)) w.Minute.Dequeue();
    }

    public static int GlobalInFlight => Volatile.Read(ref _globalInFlight);

    // ---------------------------------------------------------------- DB helpers

    private static int? NullableInt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    private static async Task<EffectiveLimits> ReadLimits(NpgsqlConnection c, NpgsqlTransaction? tx, long uid, bool lockRow, CancellationToken ct)
    {
        await using var cmd = Db.Cmd(c,
            "SELECT rate_per_minute, daily_quota, video_daily_quota, max_concurrent FROM agnes_auth.users WHERE id = $1" +
            (lockRow ? " FOR UPDATE" : ""), tx, uid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw AuthService.Unauthorized("invalid token: unknown user");
        var over = new List<string>();
        int Pick(int i, string name, int def)
        {
            var v = NullableInt(r, i);
            if (v == null) return def;
            over.Add(name);
            return v.Value;
        }
        return new EffectiveLimits(
            Pick(0, "rate_per_minute", DefaultPerMinute),
            Pick(1, "daily_quota", DefaultPerDay),
            Pick(2, "video_daily_quota", DefaultVideoPerDay),
            Pick(3, "max_concurrent", DefaultConcurrent),
            over);
    }

    private static async Task<Dictionary<string, int>> ReadUsage(NpgsqlConnection c, NpgsqlTransaction? tx, long uid, DateOnly day, CancellationToken ct)
    {
        var used = Kinds.ToDictionary(k => k, _ => 0);
        await using var cmd = Db.Cmd(c, "SELECT kind, count FROM agnes_auth.usage_daily WHERE user_id = $1 AND day = $2", tx, uid, day);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) used[r.GetString(0)] = r.GetInt32(1);   // unknown kinds are added, not lost
        return used;
    }

    private static ApiError LimitsUnavailable(Exception e)
    {
        Log.Warn($"limits db query failed: {e.GetType().Name}: {e.Message}");
        return new ApiError(503, "usage-limit storage unavailable (is the TASK4 migration in auth_schema.sql applied?)")
            { Code = "limits_unavailable" };
    }

    // ---------------------------------------------------------------- admission

    public sealed class Ticket : IAsyncDisposable
    {
        private readonly long _uid;
        private readonly string _kind;
        private readonly DateOnly _day;
        private int _released;
        internal Ticket(long uid, string kind, DateOnly day) { _uid = uid; _kind = kind; _day = day; }

        /// <summary>Give the daily unit back (Agnes rejected the request with 4xx → no upstream cost).</summary>
        public async Task Refund()
        {
            try
            {
                await using var c = await Db.Open();
                await using var cmd = Db.Cmd(c,
                    "UPDATE agnes_auth.usage_daily SET count = GREATEST(count - 1, 0), updated_at = now() " +
                    "WHERE user_id = $1 AND day = $2 AND kind = $3", null, _uid, _day, _kind);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception e) { Log.Warn($"quota refund failed: {e.GetType().Name}"); }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                var w = W(_uid);
                Interlocked.Decrement(ref w.InFlight);
                Interlocked.Decrement(ref _globalInFlight);
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Admit one generation call of <paramref name="kind"/> for the user, or throw a 429 ApiError.</summary>
    public static async Task<Ticket> Admit(long uid, string kind, CancellationToken ct = default)
    {
        var w = W(uid);
        await w.Gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var (day, resetAt) = QuotaDay(now);
            NpgsqlConnection c;
            try { c = await Db.Open(ct); }
            catch (ApiError) { throw; }
            await using (c)
            {
                await using var tx = await c.BeginTransactionAsync(ct);
                EffectiveLimits lim;
                Dictionary<string, int> used;
                try
                {
                    lim = await ReadLimits(c, tx, uid, lockRow: true, ct);
                    used = await ReadUsage(c, tx, uid, day, ct);
                }
                catch (ApiError) { throw; }
                catch (Exception e) when (e is NpgsqlException or InvalidCastException) { throw LimitsUnavailable(e); }

                var total = used.Values.Sum();
                // 1. daily quota (checked first: it has the longest wait, so it is the most useful answer)
                if (total >= lim.PerDay)
                    throw DailyExceeded("total", lim.PerDay, total, resetAt, now);
                if (kind == "video" && used["video"] >= lim.VideoPerDay)
                    throw DailyExceeded("video", lim.VideoPerDay, used["video"], resetAt, now);

                // 2. per-minute sliding window
                Prune(w, now);
                if (w.Minute.Count >= lim.PerMinute)
                {
                    var oldest = w.Minute.Count > 0 ? w.Minute.Peek() : now;
                    var reset = oldest.AddSeconds(60);
                    var secs = Math.Max(1, (int)Math.Ceiling((reset - now).TotalSeconds));
                    throw Limit429("rate_limited",
                        $"Too many generation requests: limit is {lim.PerMinute} per minute (sliding window). Retry in {secs} s.",
                        lim.PerMinute, w.Minute.Count, "user", reset, secs, retryAfterHeader: true,
                        extra: new JsonObject { ["window_seconds"] = 60 });
                }

                // 3. concurrency: per account, then global
                if (Volatile.Read(ref w.InFlight) >= lim.Concurrent)
                    throw Limit429("concurrency_limit",
                        $"Too many generations in progress for this account (max {lim.Concurrent} at once). Wait for one to finish.",
                        lim.Concurrent, w.InFlight, "user", null, ConcurrencyRetryAfterSeconds, retryAfterHeader: true);
                if (Interlocked.Increment(ref _globalInFlight) > GlobalConcurrent)
                {
                    Interlocked.Decrement(ref _globalInFlight);
                    throw Limit429("concurrency_limit",
                        $"The service is busy (max {GlobalConcurrent} generations in progress for all users). Try again shortly.",
                        GlobalConcurrent, GlobalConcurrent, "global", null, ConcurrencyRetryAfterSeconds, retryAfterHeader: true);
                }

                // 4. admitted: persist the daily unit, then record in-memory state
                try
                {
                    await using (var up = Db.Cmd(c,
                        "INSERT INTO agnes_auth.usage_daily (user_id, day, kind, count) VALUES ($1, $2, $3, 1) " +
                        "ON CONFLICT (user_id, day, kind) DO UPDATE SET count = agnes_auth.usage_daily.count + 1, updated_at = now()",
                        tx, uid, day, kind))
                        await up.ExecuteNonQueryAsync(ct);
                    await tx.CommitAsync(ct);
                }
                catch (Exception e)
                {
                    Interlocked.Decrement(ref _globalInFlight);
                    if (e is NpgsqlException) throw LimitsUnavailable(e);
                    throw;
                }
                w.Minute.Enqueue(now);
                Interlocked.Increment(ref w.InFlight);
                return new Ticket(uid, kind, day);
            }
        }
        finally { w.Gate.Release(); }
    }

    private static ApiError DailyExceeded(string scope, int limit, int used, DateTimeOffset resetAt, DateTimeOffset now)
    {
        var what = scope == "video" ? "Daily video quota" : "Daily generation quota";
        var e = Limit429("daily_quota_exceeded",
            $"{what} exhausted ({limit} per day). Resets at {resetAt:yyyy-MM-dd'T'HH:mm:sszzz} (00:00 Asia/Tokyo).",
            limit, used, scope, resetAt, null, retryAfterHeader: false);
        e.Body!["reset_in_seconds"] = Math.Max(0, (int)Math.Ceiling((resetAt - now).TotalSeconds));
        e.Body!["timezone"] = "Asia/Tokyo";
        return e;
    }

    private static ApiError Limit429(string code, string message, int limit, int used, string scope,
        DateTimeOffset? resetAt, int? retryAfter, bool retryAfterHeader, JsonObject? extra = null)
    {
        var body = new JsonObject
        {
            ["error"] = code,
            ["code"] = code,
            ["message"] = message,
            ["scope"] = scope,
            ["limit"] = limit,
            ["used"] = used,
            ["remaining"] = Math.Max(0, limit - used),
            ["reset_at"] = resetAt == null ? null : TimeZoneInfo.ConvertTime(resetAt.Value, Tokyo).ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["reset_at_unix"] = resetAt?.ToUnixTimeSeconds(),
            ["retry_after"] = retryAfter,
        };
        if (extra != null) foreach (var kv in extra) body[kv.Key] = kv.Value?.DeepClone();
        var err = new ApiError(429, message) { Code = code, Body = body };
        if (retryAfterHeader && retryAfter != null) err.Headers.Add(new("Retry-After", retryAfter.Value.ToString()));
        return err;
    }

    // ---------------------------------------------------------------- visibility

    public static async Task<JsonObject> View(long uid, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var (day, resetAt) = QuotaDay(now);
        EffectiveLimits lim;
        Dictionary<string, int> used;
        try
        {
            await using var c = await Db.Open(ct);
            lim = await ReadLimits(c, null, uid, lockRow: false, ct);
            used = await ReadUsage(c, null, uid, day, ct);
        }
        catch (ApiError) { throw; }
        catch (Exception e) when (e is NpgsqlException or InvalidCastException) { throw LimitsUnavailable(e); }

        var w = W(uid);
        int minuteUsed;
        await w.Gate.WaitAsync(ct);
        try { Prune(w, now); minuteUsed = w.Minute.Count; }
        finally { w.Gate.Release(); }

        var total = used.Values.Sum();
        var overridden = new JsonArray();
        foreach (var o in lim.Overridden) overridden.Add(o);
        return new JsonObject
        {
            ["limits"] = new JsonObject
            {
                ["per_minute"] = lim.PerMinute,
                ["per_day"] = lim.PerDay,
                ["video_per_day"] = lim.VideoPerDay,
                ["concurrent"] = lim.Concurrent,
                ["global_concurrent"] = GlobalConcurrent,
                ["overridden"] = overridden,
            },
            ["usage"] = new JsonObject
            {
                ["day"] = day.ToString("yyyy-MM-dd"),
                ["timezone"] = "Asia/Tokyo",
                ["reset_at"] = resetAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                ["reset_at_unix"] = resetAt.ToUnixTimeSeconds(),
                ["used"] = new JsonObject
                {
                    ["text"] = used["text"], ["image"] = used["image"], ["video"] = used["video"], ["analyze"] = used["analyze"], ["ingest"] = used["ingest"], ["total"] = total,
                },
                ["remaining"] = new JsonObject
                {
                    ["total"] = Math.Max(0, lim.PerDay - total),
                    ["video"] = Math.Max(0, Math.Min(lim.VideoPerDay - used["video"], lim.PerDay - total)),
                },
                ["last_minute"] = new JsonObject
                {
                    ["used"] = minuteUsed, ["remaining"] = Math.Max(0, lim.PerMinute - minuteUsed),
                },
                ["in_flight"] = Volatile.Read(ref w.InFlight),
            },
            ["counted_endpoints"] = new JsonArray("POST /api/text", "POST /api/image", "POST /api/video", "POST /api/analyze", "POST /api/reading/ingest"),
        };
    }
}
