using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Npgsql;
using NpgsqlTypes;

namespace AgnesService;

// =====================================================================================================
// TASK6 phase 2 (TASK6-PHASE2-PLAN.md §2): reading ingest orchestration.
//
//   POST /api/reading/ingest            202 + job (async: the public gateway cuts requests at ~20 s, §1.4)
//   GET  /api/reading/jobs/{id}         job state (owner or admin)
//   GET  /api/reading/jobs              list (admin: all; others: own) ?status=&channel=&share_id=&limit=&mine=1
//   GET  /api/reading/l2                L2 to-do list (admin or agent accounts); reconciles before answering
//   POST /api/reading/l2/{id}/report    optional back-fill (note / give_up) — completion itself is reconciled
//
// Callers (PHASE2-AUTH-DESIGN §5.1): agent channel = MuseAgent JWT (users.reading_ingest='agent' + users.muse_id →
// added_by); web channel = the user's own 8793 JWT from the same-origin「添加文章」tab / GET /add
// (users.reading_ingest='web'): title + text + lang only, added_by = added_via = web; agnes-service shares the text
// with its own TTSWEB_TOKEN over 127.0.0.1. No CORS; the TtsWeb token never reaches the browser.
// Admission (P2R-2): Admit once at POST (429 / daily quota answered right away), ticket released at once;
// jobs run on their own worker pool (INGEST_WORKERS=2). One call_log row (accepted) updated at the end.
// Jobs are persisted in agnes_auth.ingest_jobs (P2R-1): restart recovery, delayed retries via the scanner.
// =====================================================================================================

public static class IngestApi
{
    public const long BodyMax = 64 * 1024;
    public const int MaxText = 5000;
    public static int Workers => Settings.GetInt("INGEST_WORKERS", 2, 1, 16);
    public static int KeepDays => Settings.GetInt("INGEST_JOB_KEEP_DAYS", 30, 1, 3650);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/reading/ingest", Submit);
        app.MapGet("/api/reading/jobs/{id}", GetJob).RequireAuthorization();
        app.MapGet("/api/reading/jobs", ListJobs).RequireAuthorization();
        app.MapGet("/api/reading/l2", ListL2).RequireAuthorization();
        app.MapPost("/api/reading/l2/{id}/report", ReportL2).RequireAuthorization();
    }

    private static ApiError Forbidden(string code, string msg) => new(403, msg) { Code = code };
    private static ApiError Bad(string code, string msg) => new(400, msg) { Code = code };

    /// <summary>Caller identity + channel, decided BEFORE admission (401/403 never cost quota, P2R-16/17).</summary>
    private static async Task<(AuthUser u, string channel, string addedBy)> Caller(HttpContext ctx)
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            var hdr = ctx.Request.Headers.Authorization.ToString();
            throw AuthService.Unauthorized(hdr.Length > 7 ? "invalid token" : "authentication required (Authorization: Bearer <token>)",
                hdr.Length > 7 ? "invalid_token" : null);
        }
        var u = await AuthService.RequireUser(ctx);
        switch (u.ReadingIngest)
        {
            case "agent":
                if (string.IsNullOrEmpty(u.MuseId))
                    throw Forbidden("ingest_agent_unmapped", "agent account has no muse_id (A00x) mapping; ask an admin");
                return (u, "agent", u.MuseId);
            case "web":
                // reading.articles records the channel only (added_by = added_via = web, §1.2: no cross-system mapping);
                // who clicked「保存」is kept on the agnes side (ingest_jobs.user_id/username, call_log)
                return (u, "web", "web");
            default:
                throw Forbidden("ingest_forbidden", "this account may not save reading articles (reading_ingest is 'none')");
        }
    }

    /// <summary>TTSWEB_PUBLIC_URL (8791 public root) → absolute short link for the 8793 page; relative when unset.</summary>
    public static string? PublicUrl => Settings.Get("TTSWEB_PUBLIC_URL").Trim().TrimEnd('/') is { Length: > 0 } s ? s : null;

    public static string? ShortUrl(string? shareId) => shareId == null ? null : (PublicUrl ?? "") + "/v3?s=" + shareId;

    private static string? Str(JsonObject b, string k, int max)
    {
        var n = b[k];
        if (n == null) return null;
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s)) throw Bad("invalid_request", $"'{k}' must be a string");
        s = s.Trim();
        if (s.Length == 0) return null;
        if (s.Length > max) throw Bad("invalid_request", $"'{k}' is too long (max {max})");
        return s;
    }

    private static async Task Submit(HttpContext ctx)
    {
        var (u, channel, addedBy) = await Caller(ctx);
        Http.RequireJsonContentType(ctx);
        var body = await Json.ReadBody(ctx, BodyMax);
        if (channel == "web")
        {
            // web channel (design §4.5): title + text + lang only; share_id / rate (replace, a chosen article) are off-limits
            foreach (var (k, _) in body)
                if (k is not ("text" or "title" or "lang"))
                    throw Bad("unknown_field", $"unknown field '{k}' (web channel accepts: title, text, lang)");
        }
        else
            foreach (var (k, _) in body)
                if (k is not ("text" or "share_id" or "title" or "lang" or "rate"))
                    throw Bad("unknown_field", $"unknown field '{k}' (allowed: text, share_id, title, lang, rate)");
        var text = body["text"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts.Replace("\r\n", "\n").Trim() : null;
        if (body["text"] != null && text == null) throw Bad("invalid_text", "'text' must be a string");
        var shareId = Str(body, "share_id", 64);
        var title = Str(body, "title", 120);
        var lang = (Str(body, "lang", 4) ?? "ja").ToLowerInvariant();
        var rate = Str(body, "rate", 8);
        if (lang is not ("ja" or "zh" or "en")) throw Bad("invalid_lang", "'lang' must be ja, zh or en");
        if (rate != null && !System.Text.RegularExpressions.Regex.IsMatch(rate, @"^[+-]\d+%$")) throw Bad("invalid_rate", "'rate' must look like -20%");
        if (shareId != null && !System.Text.RegularExpressions.Regex.IsMatch(shareId, "^[0-9a-f]{10}$")) throw Bad("invalid_share_id", "'share_id' must be a 10-hex short id");
        if (channel == "web")
        {
            if (title == null) throw Bad("title_required", "web channel: 'title' is required");
            if (string.IsNullOrEmpty(text)) throw Bad("invalid_text", "web channel: 'text' is required");
        }
        else if ((shareId == null) == string.IsNullOrEmpty(text))
            throw Bad("invalid_request", "send exactly one of 'text' (new article) or 'share_id' (already shared)");
        if (text != null && text.Length > MaxText) throw Bad("invalid_text", $"'text' is too long (max {MaxText} chars)");

        // same article already being worked on → hand back that job, no admission (P2R-14 lock key = share_id)
        if (shareId != null && await IngestStore.ActiveByShare(shareId, ctx.RequestAborted) is { } active)
        {
            await Json.Write(ctx, 200, new JsonObject { ["duplicate"] = true, ["job"] = IngestStore.View(active) });
            return;
        }

        var sw = Stopwatch.StartNew();
        var preview = title ?? text ?? shareId;
        LimitService.Ticket ticket;
        try { ticket = await LimitService.Admit(u.Id, "ingest", ctx.RequestAborted); }
        catch (ApiError e)
        {
            var scope = e.Body?["scope"] is JsonValue sv && sv.TryGetValue<string>(out var sc) ? sc : null;
            await CallLog.Write(u.Id, u.Username, "ingest", null, e.Code ?? "rejected", e.Status, null, sw.ElapsedMilliseconds, preview, scope);
            throw;
        }
        await ticket.DisposeAsync();   // P2R-2: the job does not hold a Generate concurrency slot

        var logId = await CallLog.WriteReturningId(u.Id, u.Username, "ingest", null, "accepted", 202, preview,
            $"ch={channel};by={addedBy}" + (shareId != null ? ";share=" + shareId : ""));
        IngestStore.Job job;
        try
        {
            job = await IngestStore.Insert(u, channel, addedBy, shareId, title, lang, rate, text, logId, ctx.RequestAborted);
        }
        catch (PostgresException e) when (e.SqlState == "23505" && shareId != null)
        {
            await ticket.Refund();
            var other = await IngestStore.ActiveByShare(shareId, ctx.RequestAborted);
            if (logId != null) await CallLog.Update(logId.Value, null, "duplicate", 200, sw.ElapsedMilliseconds, "refund;dup_of=" + other?.Id);
            await Json.Write(ctx, 200, new JsonObject { ["duplicate"] = true, ["job"] = other == null ? null : IngestStore.View(other) });
            return;
        }

        // share-first (DF-3): a new text is shared right away so the caller gets the short link in the 202
        // (≈2 s); if TtsWeb is unavailable the job keeps the text and the worker retries (T state).
        if (job.ShareId == null && job.SourceText != null)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var sid = await TtsWebClient.Share(job.SourceText, job.Lang, job.Rate, job.Title, job.AddedBy, job.Channel, cts.Token);
                var (stored, dup) = await IngestStore.SetShare(job.Id, sid, CancellationToken.None);
                if (!stored && dup != null)
                {
                    await ticket.Refund();
                    await IngestStore.Finish(job.Id, "cancelled", "done", "duplicate", null, new JsonObject { ["dup_of"] = dup.Id.ToString() });
                    if (logId != null) await CallLog.Update(logId.Value, null, "duplicate", 200, sw.ElapsedMilliseconds, "refund;dup_of=" + dup.Id);
                    await Json.Write(ctx, 200, new JsonObject { ["duplicate"] = true, ["job"] = IngestStore.View(dup) });
                    return;
                }
                job = job with { ShareId = sid };
            }
            catch (Exception e) when (e is TtsWebClient.TtsWebError or OperationCanceledException)
            {
                Log.Warn($"ingest {job.Id}: synchronous /share failed ({(e as TtsWebClient.TtsWebError)?.Kind ?? "timeout"}); the worker retries");
            }
        }
        IngestWorker.Enqueue(job.Id);
        var res = IngestStore.View(job);
        await Json.Write(ctx, 202, new JsonObject { ["job"] = res, ["job_id"] = job.Id.ToString(), ["share_id"] = job.ShareId,
            ["url"] = ShortUrl(job.ShareId) });
    }

    private static async Task GetJob(HttpContext ctx)
    {
        var u = await AuthService.RequireUser(ctx);
        if (!Guid.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id)) throw new ApiError(404, "no such job") { Code = "not_found" };
        var job = await IngestStore.Get(id, ctx.RequestAborted);
        if (job == null || (u.Role != "admin" && job.UserId != u.Id)) throw new ApiError(404, "no such job") { Code = "not_found" };
        await Json.Write(ctx, 200, new JsonObject { ["job"] = IngestStore.View(job) });
    }

    private static async Task ListJobs(HttpContext ctx)
    {
        var u = await AuthService.RequireUser(ctx);
        var q = ctx.Request.Query;
        var limit = int.TryParse(q["limit"].FirstOrDefault(), out var n) ? Math.Clamp(n, 1, 500) : 100;
        var status = q["status"].FirstOrDefault();
        if (!string.IsNullOrEmpty(status) && status is not ("queued" or "running" or "retry" or "done" or "l2" or "failed" or "cancelled"))
            throw Bad("invalid_status", "status must be queued, running, retry, done, l2, failed or cancelled");
        var channel = q["channel"].FirstOrDefault();
        if (!string.IsNullOrEmpty(channel) && channel is not ("agent" or "web")) throw Bad("invalid_channel", "channel must be agent or web");
        var mine = u.Role != "admin" || q["mine"].FirstOrDefault() is "1" or "true";
        var jobs = await IngestStore.List(mine ? u.Id : null, NullIfEmpty(status), NullIfEmpty(channel), NullIfEmpty(q["share_id"].FirstOrDefault()), limit, ctx.RequestAborted);
        await Json.Write(ctx, 200, new JsonObject { ["jobs"] = new JsonArray(jobs.Select(j => (JsonNode)IngestStore.View(j)).ToArray()) });
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static async Task<AuthUser> RequirePatrol(HttpContext ctx)
    {
        var u = await AuthService.RequireUser(ctx);
        if (u.Role != "admin" && u.ReadingIngest != "agent")
            throw Forbidden("forbidden", "the L2 list is for admins and agent accounts");
        return u;
    }

    /// <summary>L2 to-do list (read-only for the patrol agent). Reconciles first: ready + newer version → closed.</summary>
    private static async Task ListL2(HttpContext ctx)
    {
        await RequirePatrol(ctx);
        var closed = await IngestWorker.Reconcile(ctx.RequestAborted);
        var pending = await IngestStore.List(null, "l2", null, null, 500, ctx.RequestAborted);
        await Json.Write(ctx, 200, new JsonObject
        {
            ["executor"] = "agy-gemini",
            ["reconciled_now"] = closed,
            ["pending"] = new JsonArray(pending.Select(j => (JsonNode)new JsonObject
            {
                ["job_id"] = j.Id.ToString(), ["share_id"] = j.ShareId, ["url"] = IngestApi.ShortUrl(j.ShareId),
                ["title"] = j.Title, ["channel"] = j.Channel, ["added_by"] = j.AddedBy, ["base_version"] = j.BaseVersion,
                ["l2_reason"] = j.L2Reason, ["error_code"] = j.ErrorCode, ["trail"] = j.Trail,
                ["suggested_executor"] = "agy-gemini", ["created_at"] = AdminApi.Iso(j.CreatedAt), ["updated_at"] = AdminApi.Iso(j.UpdatedAt),
            }).ToArray()),
        });
    }

    private static async Task ReportL2(HttpContext ctx)
    {
        var u = await RequirePatrol(ctx);
        if (!Guid.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var id)) throw new ApiError(404, "no such job") { Code = "not_found" };
        Http.RequireJsonContentType(ctx);
        var body = await Json.ReadBody(ctx, 8 * 1024);
        var note = Str(body, "note", 500);
        var giveUp = body["give_up"] is JsonValue g && g.TryGetValue<bool>(out var gb) && gb;
        var job = await IngestStore.Get(id, ctx.RequestAborted);
        if (job == null || job.Status != "l2") throw new ApiError(404, "no pending L2 job with this id") { Code = "not_found" };
        await IngestStore.AddL2Report(id, u.Username, note, giveUp, ctx.RequestAborted);
        await Json.Write(ctx, 200, new JsonObject { ["job"] = IngestStore.View((await IngestStore.Get(id, ctx.RequestAborted))!) });
    }
}

// ---------------------------------------------------------------------------------------------------- store

public static class IngestStore
{
    public sealed record Job(Guid Id, long UserId, string Username, string Channel, string? AddedBy, string? ShareId,
        int? BaseVersion, string? Title, string Lang, string? Rate, string? SourceText, string Stage, string Status,
        string? ProviderModel, JsonNode? Chunks, string? Trail, string? ErrorCode, string? L2Reason, int Attempt,
        int TAttempts, DateTime? TSince, DateTime? NextRetryAt, JsonObject? Result, long? CallLogId, DateTime? L2ClosedAt,
        DateTime CreatedAt, DateTime UpdatedAt, DateTime? FinishedAt);

    private const string Cols =
        "id, user_id, username, channel, added_by, share_id, base_version, title, lang, rate, source_text, stage, status, " +
        "provider_model, chunks, trail, error_code, l2_reason, attempt, t_attempts, t_since, next_retry_at, result, call_log_id, " +
        "l2_closed_at, created_at, updated_at, finished_at";

    private static Job Read(NpgsqlDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        DateTime? T(int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
        return new Job(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetString(3), S(4), S(5), r.IsDBNull(6) ? null : r.GetInt32(6),
            S(7), r.GetString(8), S(9), S(10), r.GetString(11), r.GetString(12), S(13), S(14) is { } cj ? JsonNode.Parse(cj) : null,
            S(15), S(16), S(17), r.GetInt32(18), r.GetInt32(19), T(20), T(21), S(22) is { } rj ? JsonNode.Parse(rj) as JsonObject : null,
            r.IsDBNull(23) ? null : r.GetInt64(23), T(24), r.GetDateTime(25), r.GetDateTime(26), T(27));
    }

    private static async Task<List<Job>> Query(string sql, CancellationToken ct, params object?[] args)
    {
        await using var c = await Db.Open(ct);
        await using var cmd = Db.Cmd(c, sql, null, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Job>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    private static async Task<int> Exec(string sql, CancellationToken ct, params object?[] args)
    {
        await using var c = await Db.Open(ct);
        await using var cmd = Db.Cmd(c, sql, null, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlParameter JsonParam(JsonNode? n) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = n == null ? DBNull.Value : n.ToJsonString(Json.Options) };

    public static async Task<Job> Insert(AuthUser u, string channel, string? addedBy, string? shareId, string? title, string lang,
        string? rate, string? text, long? callLogId, CancellationToken ct) =>
        (await Query($"INSERT INTO agnes_auth.ingest_jobs (user_id, username, channel, added_by, share_id, title, lang, rate, source_text, " +
                     $"stage, status, call_log_id) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'queued', $11) RETURNING {Cols}", ct,
            u.Id, u.Username, channel, addedBy, shareId, title, lang, rate, text, shareId == null ? "share" : "preview", callLogId))[0];

    public static async Task<Job?> Get(Guid id, CancellationToken ct) =>
        (await Query($"SELECT {Cols} FROM agnes_auth.ingest_jobs WHERE id = $1", ct, id)).FirstOrDefault();

    public static async Task<Job?> ActiveByShare(string shareId, CancellationToken ct) =>
        (await Query($"SELECT {Cols} FROM agnes_auth.ingest_jobs WHERE share_id = $1 AND status IN ('queued','running','retry')", ct, shareId)).FirstOrDefault();

    public static async Task<List<Job>> List(long? userId, string? status, string? channel, string? shareId, int limit, CancellationToken ct)
    {
        var where = new List<string>();
        var args = new List<object?>();
        void W(string col, object? v) { if (v == null) return; args.Add(v); where.Add($"{col} = ${args.Count}"); }
        W("user_id", userId); W("status", status); W("channel", channel); W("share_id", shareId);
        args.Add(limit);
        return await Query($"SELECT {Cols} FROM agnes_auth.ingest_jobs " + (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "") +
                           $"ORDER BY created_at DESC LIMIT ${args.Count}", ct, args.ToArray());
    }

    /// <summary>Atomically take a queued / due job (worker). Returns null if someone else has it.</summary>
    public static async Task<Job?> Claim(Guid id, CancellationToken ct) =>
        (await Query($"UPDATE agnes_auth.ingest_jobs SET status = 'running', updated_at = now() WHERE id = $1 " +
                     $"AND status IN ('queued','retry') RETURNING {Cols}", ct, id)).FirstOrDefault();

    /// <summary>Store the share id; false + the other active job if the partial unique index refuses (same article running).</summary>
    public static async Task<(bool ok, Job? other)> SetShare(Guid id, string shareId, CancellationToken ct)
    {
        try
        {
            await Exec("UPDATE agnes_auth.ingest_jobs SET share_id = $2, stage = 'preview', updated_at = now() WHERE id = $1", ct, id, shareId);
            return (true, null);
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            return (false, await ActiveByShare(shareId, ct));
        }
    }

    public static Task Progress(Guid id, string stage, int? baseVersion = null, string? title = null, string? providerModel = null,
        JsonNode? chunks = null, string? trail = null) =>
        Exec("UPDATE agnes_auth.ingest_jobs SET stage = $2, base_version = coalesce($3, base_version), title = coalesce($4, title), " +
             "provider_model = coalesce($5, provider_model), chunks = coalesce($6, chunks), trail = coalesce($7, trail), updated_at = now() WHERE id = $1",
            CancellationToken.None, id, stage, baseVersion, title, providerModel, JsonParam(chunks), trail);

    /// <summary>Back to the queue later (delayed whole-article retry, or T state); status retry keeps the share lock.</summary>
    public static Task Retry(Guid id, DateTime at, string stage, string? errorCode, bool tState, int attemptInc, string? trail, JsonObject? result) =>
        Exec("UPDATE agnes_auth.ingest_jobs SET status = 'retry', stage = $3, next_retry_at = $2, error_code = $4, " +
             "t_attempts = CASE WHEN $5 THEN t_attempts + 1 ELSE 0 END, t_since = CASE WHEN $5 THEN coalesce(t_since, now()) ELSE NULL END, " +
             "attempt = attempt + $6, trail = coalesce($7, trail), result = coalesce($8, result), updated_at = now() WHERE id = $1",
            CancellationToken.None, id, at, stage, errorCode, tState, attemptInc, trail, JsonParam(result));

    /// <summary>Terminal (done / failed / cancelled) or L2-pending state.</summary>
    public static Task Finish(Guid id, string status, string stage, string? errorCode, string? l2Reason, JsonObject? result, string? trail = null) =>
        Exec("UPDATE agnes_auth.ingest_jobs SET status = $2, stage = $3, error_code = $4, l2_reason = $5, result = coalesce($6, result), " +
             "trail = coalesce($7, trail), next_retry_at = NULL, source_text = CASE WHEN share_id IS NOT NULL THEN NULL ELSE source_text END, " +
             "finished_at = CASE WHEN $2 = 'l2' THEN NULL ELSE now() END, " +
             "expires_at = CASE WHEN $2 = 'l2' THEN NULL ELSE now() + make_interval(days => $8) END, updated_at = now() WHERE id = $1",
            CancellationToken.None, id, status, stage, errorCode, l2Reason, JsonParam(result), trail, IngestApi.KeepDays);

    public static Task CloseL2(Guid id, JsonObject l2) =>
        Exec("UPDATE agnes_auth.ingest_jobs SET status = 'done', stage = 'done', l2_closed_at = now(), finished_at = now(), " +
             "expires_at = now() + make_interval(days => $3), result = coalesce(result, '{}'::jsonb) || jsonb_build_object('l2', $2::jsonb), " +
             "updated_at = now() WHERE id = $1 AND status = 'l2'", CancellationToken.None, id, JsonParam(l2), IngestApi.KeepDays);

    public static Task AddL2Report(Guid id, string by, string? note, bool giveUp, CancellationToken ct) =>
        Exec("UPDATE agnes_auth.ingest_jobs SET result = coalesce(result, '{}'::jsonb) || jsonb_build_object('l2_reports', " +
             "coalesce(result->'l2_reports', '[]'::jsonb) || jsonb_build_array(jsonb_build_object('at', now(), 'by', $2::text, 'note', $3::text, 'give_up', $4))), " +
             "status = CASE WHEN $4 THEN 'failed' ELSE status END, stage = CASE WHEN $4 THEN 'failed' ELSE stage END, " +
             "error_code = CASE WHEN $4 THEN 'l2_failed' ELSE error_code END, finished_at = CASE WHEN $4 THEN now() ELSE finished_at END, " +
             "expires_at = CASE WHEN $4 THEN now() + make_interval(days => $5) ELSE expires_at END, updated_at = now() WHERE id = $1 AND status = 'l2'",
            ct, id, by, note, giveUp, IngestApi.KeepDays);

    public static async Task<List<Guid>> Ids(string where, CancellationToken ct)
    {
        await using var c = await Db.Open(ct);
        await using var cmd = Db.Cmd(c, "SELECT id FROM agnes_auth.ingest_jobs WHERE " + where + " ORDER BY created_at", null);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        return ids;
    }

    /// <summary>Startup recovery: jobs that were running when the process stopped go back to retry (due now).</summary>
    public static Task<int> RecoverRunning(CancellationToken ct) =>
        Exec("UPDATE agnes_auth.ingest_jobs SET status = 'retry', next_retry_at = now(), updated_at = now() WHERE status = 'running'", ct);

    public static Task<int> Purge(CancellationToken ct) =>
        Exec("DELETE FROM agnes_auth.ingest_jobs WHERE expires_at < now() AND status IN ('done','failed','cancelled')", ct);

    public static JsonObject View(Job j) => new()
    {
        ["id"] = j.Id.ToString(), ["status"] = j.Status, ["stage"] = j.Stage, ["channel"] = j.Channel,
        ["username"] = j.Username, ["added_by"] = j.AddedBy, ["share_id"] = j.ShareId,
        ["url"] = IngestApi.ShortUrl(j.ShareId), ["title"] = j.Title, ["lang"] = j.Lang,
        ["base_version"] = j.BaseVersion, ["provider_model"] = j.ProviderModel, ["chunks"] = j.Chunks?.DeepClone(),
        ["trail"] = j.Trail, ["error_code"] = j.ErrorCode, ["l2_reason"] = j.L2Reason, ["attempt"] = j.Attempt,
        ["t_attempts"] = j.TAttempts, ["next_retry_at"] = j.NextRetryAt == null ? null : AdminApi.Iso(j.NextRetryAt.Value),
        ["result"] = j.Result?.DeepClone(), ["l2_closed_at"] = j.L2ClosedAt == null ? null : AdminApi.Iso(j.L2ClosedAt.Value),
        ["created_at"] = AdminApi.Iso(j.CreatedAt), ["updated_at"] = AdminApi.Iso(j.UpdatedAt),
        ["finished_at"] = j.FinishedAt == null ? null : AdminApi.Iso(j.FinishedAt.Value),
    };
}

// ---------------------------------------------------------------------------------------------------- worker

/// <summary>
/// Ingest worker pool (INGEST_WORKERS, default 2) + scanner (every INGEST_SCAN_S=60 s: due retries; every
/// INGEST_RECONCILE_S=3600 s: L2 reconciliation; daily: purge expired jobs). Startup: running → retry, queue all due.
/// </summary>
public sealed class IngestWorker : BackgroundService
{
    private static readonly Channel<Guid> Queue = Channel.CreateUnbounded<Guid>();
    private static readonly ConcurrentDictionary<Guid, byte> Queued = new();

    public static void Enqueue(Guid id)
    {
        if (Queued.TryAdd(id, 0)) Queue.Writer.TryWrite(id);
    }

    public static double ScanS => Settings.GetInt("INGEST_SCAN_S", 60, 1, 3600);
    public static double ReconcileS => Settings.GetInt("INGEST_RECONCILE_S", 3600, 10, 86400);
    public static int[] RetryScheduleS => ParseList(Settings.Get("INGEST_RETRY_SCHEDULE_S", "900,3600,14400"));
    public static int[] TBackoffS => ParseList(Settings.Get("INGEST_T_BACKOFF_S", "60,300,900,3600"));
    public static int TMaxS => Settings.GetInt("INGEST_T_MAX_S", 86400, 10, 7 * 86400);
    public static double KanaMinRatio => Settings.GetInt("INGEST_KANA_MIN_PERCENT", 10, 0, 100) / 100.0;

    private static int[] ParseList(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n > 0).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!Db.Configured) { Log.Warn("ingest worker: no database configured; disabled"); return; }
        try
        {
            var n = await IngestStore.RecoverRunning(stop);
            if (n > 0) Log.Info($"ingest worker: {n} interrupted job(s) set back to retry");
        }
        catch (Exception e) { Log.Warn($"ingest worker: startup recovery skipped ({e.GetType().Name}: {e.Message.Split('\n')[0]})"); }
        var workers = Enumerable.Range(0, IngestApi.Workers).Select(_ => Task.Run(() => WorkLoop(stop), stop)).ToList();
        var lastReconcile = DateTime.UtcNow;
        var lastPurge = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                foreach (var id in await IngestStore.Ids("status = 'queued' OR (status = 'retry' AND next_retry_at <= now())", stop)) Enqueue(id);
                if ((DateTime.UtcNow - lastReconcile).TotalSeconds >= ReconcileS)
                {
                    lastReconcile = DateTime.UtcNow;
                    await Reconcile(stop);
                }
                if (DateTime.UtcNow - lastPurge > TimeSpan.FromDays(1))
                {
                    lastPurge = DateTime.UtcNow;
                    await IngestStore.Purge(stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) { Log.Warn($"ingest scanner: {e.GetType().Name}: {Upstream.Redact(e.Message.Split('\n')[0])}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(ScanS), stop); } catch (OperationCanceledException) { break; }
        }
        await Task.WhenAll(workers.Select(t => t.ContinueWith(_ => { })));
    }

    private static async Task WorkLoop(CancellationToken stop)
    {
        try
        {
            await foreach (var id in Queue.Reader.ReadAllAsync(stop))
            {
                Queued.TryRemove(id, out _);
                try { await RunJob(id, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception e) { Log.Error($"ingest job {id}: {e.GetType().Name}: {Upstream.Redact(e.Message.Split('\n')[0])}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Kana share of kana+kanji characters; low → not Japanese (D8 fallback against a wrong lang choice).</summary>
    public static double KanaRatio(string text)
    {
        int kana = 0, kanji = 0;
        foreach (var c in text)
        {
            if (Analyze.IsKana(c)) kana++;
            else if (Analyze.IsKanji(c)) kanji++;
        }
        return kana + kanji == 0 ? 0 : (double)kana / (kana + kanji);
    }

    private sealed class Ctx
    {
        public required IngestStore.Job Job;
        public readonly Stopwatch Sw = Stopwatch.StartNew();
        public readonly List<string> Notes = new();
    }

    private static DateTime TNext(IngestStore.Job j)
    {
        var b = TBackoffS;
        var s = b.Length == 0 ? 3600 : b[Math.Min(j.TAttempts, b.Length - 1)];
        return DateTime.UtcNow.AddSeconds(s);
    }

    public static async Task RunJob(Guid id, CancellationToken stop)
    {
        var job = await IngestStore.Claim(id, stop);
        if (job == null) return;
        var cx = new Ctx { Job = job };
        var scope = new Upstream.IngestCallScope();
        Upstream.IngestScope.Value = scope;
        string stage = job.Stage;
        try
        {
            await Pipeline(cx, scope, s => stage = s, stop);
        }
        catch (TtsWebClient.TtsWebError e) when (e.Kind == "t")
        {
            // T state (E14): TtsWeb / PG unavailable → back-off retry, never L2; give up after INGEST_T_MAX_S
            var since = job.TSince ?? DateTime.UtcNow;
            if ((DateTime.UtcNow - since).TotalSeconds >= TMaxS)
                await End(cx, "failed", "failed", "failed_ttsweb", null, new JsonObject { ["error"] = e.Message });
            else
            {
                await IngestStore.Retry(id, TNext(job), stage is "done" or "l2-pending" or "failed" ? "preview" : stage, "ttsweb_unavailable", true, 0, null, null);
                await Interim(cx, "retry:ttsweb", e.Message);
            }
        }
        catch (TtsWebClient.TtsWebError e) when (e.Kind == "auth")
        {
            // E13: our TtsWeb write token is refused → the API path cannot write; L2 (agy uses its own token)
            await End(cx, "l2", "l2-pending", "ttsweb_auth_failed", "api-down", new JsonObject { ["error"] = e.Message });
        }
        catch (TtsWebClient.TtsWebError e) when (e.Kind == "gone")
        {
            await End(cx, "cancelled", "done", "article_gone", null, new JsonObject { ["error"] = e.Message });   // E16
        }
        catch (TtsWebClient.TtsWebError e)
        {
            await End(cx, "failed", "failed", "ttsweb_" + (e.Code ?? "invalid"), null, new JsonObject { ["error"] = e.Message });   // E15
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await IngestStore.Retry(id, DateTime.UtcNow, stage is "done" or "l2-pending" or "failed" ? "preview" : stage, "interrupted", false, 0, null, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // unexpected (DB hiccup, bug): keep the job alive on the T back-off schedule instead of leaving it "running"
            Log.Error($"ingest job {id}: {e.GetType().Name}: {Upstream.Redact(e.Message.Split('\n')[0])}");
            var since = job.TSince ?? DateTime.UtcNow;
            if ((DateTime.UtcNow - since).TotalSeconds >= TMaxS)
                await End(cx, "failed", "failed", "internal_error", null, new JsonObject { ["error"] = e.GetType().Name });
            else
                await IngestStore.Retry(id, TNext(job), stage is "done" or "l2-pending" or "failed" ? "preview" : stage, "internal_error", true, 0, null, null);
        }
        finally { Upstream.IngestScope.Value = null; }
    }

    private static async Task Interim(Ctx cx, string outcome, string? note)
    {
        if (cx.Job.CallLogId is { } lid)
            await CallLog.Update(lid, null, outcome, 202, (long)(DateTime.UtcNow - cx.Job.CreatedAt).TotalMilliseconds,
                $"ch={cx.Job.Channel};by={cx.Job.AddedBy};share={cx.Job.ShareId};{outcome};" + note);
    }

    private static async Task End(Ctx cx, string status, string stage, string? error, string? l2, JsonObject? result,
        string? providerModel = null, string? detail = null, string? trail = null)
    {
        await IngestStore.Finish(cx.Job.Id, status, stage, error, l2, result, trail);
        if (cx.Job.CallLogId is { } lid)
        {
            var outcome = status switch
            {
                "done" => "done:" + ((string?)result?["article_status"] ?? (string?)result?["outcome"] ?? "ok"),
                "l2" => "l2:" + l2,
                _ => status + ":" + error,
            };
            var det = $"ch={cx.Job.Channel};by={cx.Job.AddedBy};share={cx.Job.ShareId}" +
                      (cx.Job.Attempt > 0 ? $";retry={cx.Job.Attempt}" : "") + (cx.Notes.Count > 0 ? ";" + string.Join(";", cx.Notes) : "") +
                      (detail != null ? ";" + detail : "");
            await CallLog.Update(lid, providerModel, outcome, status == "done" ? 200 : status == "l2" ? 202 : 502,
                (long)(DateTime.UtcNow - cx.Job.CreatedAt).TotalMilliseconds, det);
        }
    }

    private static string L2ReasonOf(Analyze.EnrichOutcome? o, bool readingsOnly)
    {
        if (o == null) return "api-down";
        if (o.DeadlineHit) return "deadline";
        if (o.ValidationFailure || readingsOnly) return "quality";
        if (o.AllTrail.Any(t => t.EndsWith(":403") || t.EndsWith(":429") || t.Contains("open-quota") || t.Contains("open-rate"))) return "quota";
        return "api-down";
    }

    /// <summary>Not ready after this run: web channel → delayed whole-article retry (15 min / 1 h / 4 h), else L2.</summary>
    private static async Task NotReady(Ctx cx, string reason, JsonObject result, string? providerModel, string? detail, string? trail)
    {
        var sched = RetryScheduleS;
        if (cx.Job.Channel == "web" && cx.Job.Attempt < sched.Length)
        {
            var at = DateTime.UtcNow.AddSeconds(sched[cx.Job.Attempt]);
            result["next_reason"] = reason;
            await IngestStore.Retry(cx.Job.Id, at, "preview", "l1_incomplete", false, 1, trail, result);
            await Interim(cx, "retry:" + reason, detail);
            return;
        }
        await End(cx, "l2", "l2-pending", "l1_exhausted", reason, result, providerModel, detail, trail);
    }

    private static async Task Pipeline(Ctx cx, Upstream.IngestCallScope scope, Action<string> setStage, CancellationToken stop)
    {
        var job = cx.Job;
        // ---- share (share-first, DF-3)
        var shareId = job.ShareId;
        if (shareId == null)
        {
            setStage("share");
            if (string.IsNullOrEmpty(job.SourceText)) { await End(cx, "failed", "failed", "no_text", null, null); return; }
            shareId = await TtsWebClient.Share(job.SourceText, job.Lang, job.Rate, job.Title, job.AddedBy, job.Channel, stop);
            var (ok, other) = await IngestStore.SetShare(job.Id, shareId, stop);
            if (!ok)
            {
                await End(cx, "cancelled", "done", "duplicate", null, new JsonObject { ["dup_of"] = other?.Id.ToString() });
                return;
            }
            cx.Job = job = job with { ShareId = shareId };
        }

        // ---- read back the article (P2R-13) and branch on its state (DF-3)
        setStage("preview");
        var art = await TtsWebClient.GetArticle(shareId, stop);
        var status = (string?)art["status"];
        var curV = art["current_version"] is JsonValue cvv && cvv.TryGetValue<int>(out var cv) ? cv : 1;
        if (status == "ready")
        {
            var other = job.BaseVersion != null && curV > job.BaseVersion;
            await End(cx, "done", "done", null, null, new JsonObject
            {
                ["article_status"] = "ready", ["version"] = curV, ["outcome"] = other ? "done_by_other" : "already_ready",
            });
            return;
        }
        var text = (string?)art["text"] ?? job.SourceText ?? "";
        var lang = (string?)art["lang"] ?? job.Lang;
        var rate = (string?)art["rate"] ?? job.Rate ?? "-20%";
        var title = (string?)art["title"];
        if (string.IsNullOrWhiteSpace(title)) title = job.Title;
        if (string.IsNullOrWhiteSpace(title)) { title = new string(text.Replace("\n", " ").Trim().Take(20).ToArray()); cx.Notes.Add("title=auto"); }
        await IngestStore.Progress(job.Id, "preview", curV, title);

        // ---- D8: non-Japanese text stays as shared original (partial is final), no enrich
        if (lang != "ja" || KanaRatio(text) < KanaMinRatio)
        {
            await End(cx, "done", "done", null, null, new JsonObject
            {
                ["article_status"] = status, ["version"] = curV, ["outcome"] = "non_ja", ["lang"] = lang,
                ["kana_ratio"] = Math.Round(KanaRatio(text), 3),
            });
            return;
        }

        // ---- preview skeleton
        var pv = await TtsWebClient.Preview(text, lang, stop);
        if (pv["sentences"] is not JsonArray psents || psents.Count == 0)
            throw new TtsWebClient.TtsWebError("preview returned no sentences") { Kind = "invalid", Code = "preview_empty" };
        var sents = new List<Analyze.Sent>();
        var triageToks = new Dictionary<long, (string, List<FixTriage.Tok>)>();
        foreach (var sn in psents.OfType<JsonObject>())
        {
            var seq = sn["seq"]!.GetValue<long>();
            var st = (string?)sn["text"] ?? "";
            var toks = new List<Analyze.Tok>();
            var tt = new List<FixTriage.Tok>();
            if (sn["toks"] is JsonArray ta)
                for (var i = 0; i < ta.Count; i++)
                {
                    var to = ta[i] as JsonObject;
                    var s = (string?)to?["s"] ?? "";
                    var r = to?["r"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : null;
                    var unk = to?["unk"] is JsonValue uv && uv.TryGetValue<bool>(out var ub) && ub;
                    var pos = (string?)to?["pos"];
                    toks.Add(new Analyze.Tok(i, s, r, unk, pos));
                    tt.Add(new FixTriage.Tok(s, r, unk, pos));
                }
            sents.Add(new Analyze.Sent(seq, st, toks));
            triageToks[seq] = (st, tt);
        }

        // ---- enrich (L1 ladder inside the engine; INGEST_DEADLINE_S from here)
        setStage("enrich");
        await IngestStore.Progress(job.Id, "enrich");
        var cands = Providers.Candidates(null, null, Providers.DefaultProvider, Providers.Order, Providers.Ready);
        if (cands.Count == 0)
        {
            await NotReady(cx, "api-down", new JsonObject { ["article_status"] = status, ["version"] = curV, ["error"] = "no provider ready" }, null, "no_provider", null);
            return;
        }
        var opt = Analyze.EngineOptions.ForIngest();
        scope.WaitUntil = DateTime.UtcNow.AddSeconds(opt.DeadlineSeconds);
        var (o, _) = await Analyze.RunEnrichCore(sents, cands, false, opt, stop);
        foreach (var p in o.ValidationFailedProviders.Where(p => !o.OkProviders.Contains(p))) Circuit.ArticleQuality(p, true);
        foreach (var p in o.OkProviders) Circuit.ArticleQuality(p, false);
        var mainKey = o.Usage.Count > 0 ? o.Usage[0].Key : null;
        var trailStr = string.Join(">", o.AllTrail);
        if (trailStr.Length > 2000) trailStr = trailStr[..2000];
        await IngestStore.Progress(job.Id, "enrich", providerModel: mainKey, chunks: o.ChunksJson, trail: trailStr);
        var detail = o.Detail + (scope.ThrottleWaits > 0 ? $";tb={scope.ThrottleWaits}" : "") + (o.Probes > 0 ? ";probe" : "");

        if (o.Outs.Count == 0)
        {
            await NotReady(cx, L2ReasonOf(o, false), new JsonObject
            {
                ["article_status"] = status, ["version"] = curV, ["failed_seqs"] = o.FailedSeqs.Count,
            }, mainKey, detail, trailStr);
            return;
        }

        // ---- fixes gate (§2.3): fill-ins applied, rewrites only via counter table / dictionary check
        var fixes = o.Outs.SelectMany(s => s.Fixes.Select(f => (s.Seq, f.I, f.S, f.R)));
        var (tri, pending) = FixTriage.Triage(triageToks, fixes);
        var verdicts = await TtsWebClient.ReadingsCheck(pending, stop);
        FixTriage.Resolve(tri, pending, verdicts);
        cx.Notes.Add("fx=" + tri.Fx);

        // ---- commit (replace, P2R-13: title / lang / rate / base_version carried back)
        setStage("commit");
        await IngestStore.Progress(job.Id, "commit");
        var outBySeq = o.Outs.ToDictionary(s => s.Seq);
        var items = new JsonArray();
        foreach (var s in sents)
        {
            var it = new JsonObject { ["text"] = s.Text };
            if (outBySeq.TryGetValue(s.Seq, out var so))
            {
                it["tr"] = so.Tr;
                var (pn, mn) = o.By[s.Seq];
                it["tr_via"] = pn + "-api";
                it["tr_model"] = mn;
            }
            if (tri.Apply.TryGetValue(s.Seq, out var rl))
                it["readings"] = new JsonArray(rl.Select(x => (JsonNode)new JsonObject { ["tok"] = x.I, ["s"] = x.S, ["r"] = x.R }).ToArray());
            items.Add(it);
        }
        var main = mainKey!.Split(':', 2);
        var body = new JsonObject
        {
            ["replace"] = true, ["share_id"] = shareId, ["base_version"] = curV,
            ["text"] = text, ["title"] = title, ["lang"] = lang, ["rate"] = rate,
            ["executor"] = "agnes-service", ["tr_via"] = main[0] + "-api", ["tr_model"] = main[1],
            ["prompt_version"] = Analyze.EnrichPromptVersion, ["allow_partial"] = true, ["sentences"] = items,
        };
        JsonObject cres;
        try { cres = await TtsWebClient.Commit(body, stop); }
        catch (TtsWebClient.TtsWebError e) when (e.Kind == "conflict")
        {
            // VERSION_CONFLICT: someone else (L2 / another executor) committed first — convergence, not failure
            await End(cx, "done", "done", null, null, new JsonObject
            {
                ["outcome"] = "version_conflict_converged", ["base_version"] = curV,
                ["current_version"] = e.Body?["error"]?["details"]?["current_version"]?.DeepClone(), ["fixes"] = tri.Json(),
            }, mainKey, detail);
            return;
        }
        catch (TtsWebClient.TtsWebError e) when (e.Kind == "invalid")
        {
            // our commit was refused (BODY_INVALID / SPLIT_MISMATCH …): nothing the API path can fix → L2
            await End(cx, "l2", "l2-pending", "commit_" + (e.Code ?? "invalid"), "quality",
                new JsonObject { ["error"] = e.Message, ["fixes"] = tri.Json() }, mainKey, detail, trailStr);
            return;
        }
        var newStatus = (string?)cres["status"];
        var newV = cres["version"] is JsonValue nv && nv.TryGetValue<int>(out var nvi) ? nvi : (int?)null;
        var result = new JsonObject
        {
            ["article_status"] = newStatus, ["version"] = newV, ["base_version"] = curV,
            ["tr_via"] = main[0] + "-api", ["tr_model"] = main[1], ["fx"] = tri.Fx, ["fixes"] = tri.Json(),
            ["sentences"] = sents.Count, ["translated"] = o.Outs.Count, ["calls"] = o.Calls,
            ["mixed_providers"] = o.Usage.Count > 1,
        };
        if (newStatus == "ready")
        {
            await End(cx, "done", "done", null, null, result, mainKey, detail, trailStr);
            return;
        }
        // committed partial (L1e): untranslated sentences or missing readings
        if (newV != null) await IngestStore.Progress(job.Id, "commit", newV);   // L2 / next retry builds on our partial version
        var readingsOnly = o.Complete;
        await NotReady(cx, L2ReasonOf(o, readingsOnly), result, mainKey, detail, trailStr);
    }

    /// <summary>
    /// L2 auto-reconciliation (P2R-15): every pending L2 job whose article is now ready with a version newer than the
    /// job's base_version is closed (new version + tr_via recorded). Returns the number closed.
    /// </summary>
    public static async Task<int> Reconcile(CancellationToken ct)
    {
        var closed = 0;
        foreach (var j in await IngestStore.List(null, "l2", null, null, 500, ct))
        {
            if (j.ShareId == null) continue;
            try
            {
                var art = await TtsWebClient.GetArticle(j.ShareId, ct);
                var v = art["current_version"] is JsonValue cvv && cvv.TryGetValue<int>(out var cv) ? cv : 0;
                if ((string?)art["status"] == "ready" && v > (j.BaseVersion ?? 0))
                {
                    var trVia = art["sentences"] is JsonArray ss && ss.Count > 0 ? (string?)ss[0]?["tr_via"] : null;
                    await IngestStore.CloseL2(j.Id, new JsonObject { ["version"] = v, ["tr_via"] = trVia, ["closed_by"] = "reconcile" });
                    if (j.CallLogId is { } lid)
                        await CallLog.Update(lid, null, "done:l2", 200, (long)(DateTime.UtcNow - j.CreatedAt).TotalMilliseconds,
                            $"ch={j.Channel};by={j.AddedBy};share={j.ShareId};l2={j.L2Reason};closed_v={v};tr_via={trVia}");
                    closed++;
                }
            }
            catch (TtsWebClient.TtsWebError e) when (e.Kind == "gone")
            {
                await IngestStore.Finish(j.Id, "cancelled", "done", "article_gone", j.L2Reason, null);
            }
            catch (TtsWebClient.TtsWebError e)
            {
                Log.Warn($"L2 reconcile {j.Id}: {e.Kind} {e.Code}");
                if (e.Kind == "t") break;   // TtsWeb down: try again next round
            }
        }
        return closed;
    }
}
