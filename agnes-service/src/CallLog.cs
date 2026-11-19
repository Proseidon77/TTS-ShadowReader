using System.Text.Json.Nodes;
using Npgsql;

namespace AgnesService;

/// <summary>
/// Per-call generation log (operator addition after TASK5): one row in agnes_auth.call_log for every
/// generation call that reached the limiter — admitted (whatever its outcome) or rejected by it (429).
/// Validation 400s before admission are not calls and are not logged. prompt_preview is truncated to
/// 120 chars; nothing else from the request is stored. Writes are best-effort: a logging failure must
/// never fail the generation itself, so <see cref="Write"/> swallows everything and warns to the log.
/// Rows older than 90 days are pruned opportunistically (checked at most every 6 hours).
/// </summary>
public static class CallLog
{
    private static DateTime _lastPrune = DateTime.MinValue;

    public static async Task Write(long userId, string username, string kind, string? model, string outcome,
        int httpStatus, int? upstreamStatus, long durationMs, string? prompt, string? detail)
    {
        try
        {
            var preview = prompt == null ? null : prompt.Length <= 120 ? prompt : prompt[..120];
            // detail: compact trail for analyze (e.g. "m=raw;a=2;fb=agnes/3.0-flash:403>agnes/2.5-flash:ok;v=ok"),
            // redacted defensively, cut at ~1000 chars (TASK6 B9; column is text, no DDL change)
            var det = detail == null ? null : Upstream.Redact(detail.Length <= 1000 ? detail : detail[..1000]);
            await using var c = await Db.Open();
            await using var cmd = Db.Cmd(c,
                "INSERT INTO agnes_auth.call_log (user_id, username, kind, model, outcome, http_status, upstream_status, duration_ms, prompt_preview, detail) " +
                "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)", null,
                userId, username, kind, model, outcome, httpStatus, upstreamStatus,
                (int)Math.Min(durationMs, int.MaxValue), preview, det);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception e) { Log.Warn($"call_log write failed: {e.GetType().Name}"); }

        if (DateTime.UtcNow - _lastPrune > TimeSpan.FromHours(6))
        {
            _lastPrune = DateTime.UtcNow;
            _ = Prune();
        }
    }

    /// <summary>
    /// TASK6 phase 2 (P2R-2): the ingest job writes ONE row when it is accepted (outcome=accepted) and fills it in
    /// with UPDATE when it ends — no second row, no second quota unit. Returns the row id (null on failure).
    /// </summary>
    public static async Task<long?> WriteReturningId(long userId, string username, string kind, string? model, string outcome,
        int httpStatus, string? prompt, string? detail)
    {
        try
        {
            var preview = prompt == null ? null : prompt.Length <= 120 ? prompt : prompt[..120];
            var det = detail == null ? null : Upstream.Redact(detail.Length <= 1000 ? detail : detail[..1000]);
            await using var c = await Db.Open();
            await using var cmd = Db.Cmd(c,
                "INSERT INTO agnes_auth.call_log (user_id, username, kind, model, outcome, http_status, upstream_status, duration_ms, prompt_preview, detail) " +
                "VALUES ($1, $2, $3, $4, $5, $6, NULL, 0, $7, $8) RETURNING id", null,
                userId, username, kind, model, outcome, httpStatus, preview, det);
            return (long?)await cmd.ExecuteScalarAsync();
        }
        catch (Exception e) { Log.Warn($"call_log write failed: {e.GetType().Name}"); return null; }
    }

    /// <summary>Back-fill an ingest row at the end of the job (model / outcome / duration / detail).</summary>
    public static async Task Update(long id, string? model, string outcome, int httpStatus, long durationMs, string? detail)
    {
        try
        {
            var det = detail == null ? null : Upstream.Redact(detail.Length <= 1000 ? detail : detail[..1000]);
            await using var c = await Db.Open();
            await using var cmd = Db.Cmd(c,
                "UPDATE agnes_auth.call_log SET model = coalesce($2, model), outcome = $3, http_status = $4, duration_ms = $5, detail = $6 WHERE id = $1",
                null, id, model, outcome, httpStatus, (int)Math.Min(durationMs, int.MaxValue), det);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception e) { Log.Warn($"call_log update failed: {e.GetType().Name}"); }
    }

    private static async Task Prune()
    {
        try
        {
            await using var c = await Db.Open();
            await using var cmd = Db.Cmd(c,
                "DELETE FROM agnes_auth.call_log WHERE created_at < now() - interval '90 days'", null);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception e) { Log.Warn($"call_log prune failed: {e.GetType().Name}"); }
    }

    public static async Task<JsonArray> Recent(int limit, long? userId, string? kind, CancellationToken ct)
    {
        var where = new List<string>();
        var args = new List<object?>();
        if (userId != null) { args.Add(userId.Value); where.Add($"user_id = ${args.Count}"); }
        if (kind != null) { args.Add(kind); where.Add($"kind = ${args.Count}"); }
        args.Add(limit);
        var sql = "SELECT id, created_at, user_id, username, kind, model, outcome, http_status, upstream_status, duration_ms, prompt_preview, detail " +
                  "FROM agnes_auth.call_log " +
                  (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "") +
                  $"ORDER BY id DESC LIMIT ${args.Count}";
        await using var c = await Db.Open(ct);
        await using var cmd = Db.Cmd(c, sql, null, args.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new JsonArray();
        while (await r.ReadAsync(ct))
        {
            rows.Add(new JsonObject
            {
                ["id"] = r.GetInt64(0),
                ["created_at"] = AdminApi.Iso(r.GetDateTime(1)),
                ["user_id"] = r.GetInt64(2),
                ["username"] = r.GetString(3),
                ["kind"] = r.GetString(4),
                ["model"] = r.IsDBNull(5) ? null : r.GetString(5),
                ["outcome"] = r.GetString(6),
                ["http_status"] = r.GetInt32(7),
                ["upstream_status"] = r.IsDBNull(8) ? null : r.GetInt32(8),
                ["duration_ms"] = r.GetInt32(9),
                ["prompt_preview"] = r.IsDBNull(10) ? null : r.GetString(10),
                ["detail"] = r.IsDBNull(11) ? null : r.GetString(11),
            });
        }
        return rows;
    }
}
