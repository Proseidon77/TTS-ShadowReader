using System.Diagnostics;
using System.Text.Json.Nodes;
using Npgsql;

namespace AgnesService;

// =====================================================================================================
// 2026-11-20 delete / edit (DELETE-EDIT-REPORT.md): article management for the「添加文章」page (/add).
//
//   GET    /api/reading/articles          all live articles (any logged-in user) + per-article can_manage
//   GET    /api/reading/articles/{sid}    one article incl. original text (edit form prefill)
//   DELETE /api/reading/articles/{sid}    soft delete (owner or admin) → TtsWeb DELETE /api/v1/articles/{sid}
//   PATCH  /api/reading/articles/{sid}    {title?, lang?, text?} (owner or admin):
//            text unchanged → TtsWeb PATCH (title / lang only, no new version)               200 mode=meta
//            text changed   → ingest job op=edit on the same share_id (preview → enrich → fixes → commit
//                             replace; old versions kept; non-Japanese: original only, D8)         202 mode=job
//
// Same-origin JWT only (no CORS); TtsWeb is called over 127.0.0.1 with TTSWEB_TOKEN, which never reaches the browser.
// Ownership (enforced here, the page only mirrors it): admin → every article; web account (reading_ingest='web') →
// added_by='web'; agent account (reading_ingest='agent' + muse_id) → added_by = its muse_id; anything else → 403.
// =====================================================================================================

public static class ArticleApi
{
    public static void Map(WebApplication app)
    {
        // AdminApi.W: DB hiccups → 503 db_unavailable (same shape as the admin API)
        app.MapGet("/api/reading/articles", AdminApi.W(List)).RequireAuthorization();
        app.MapGet("/api/reading/articles/{sid}", AdminApi.W(Get)).RequireAuthorization();
        app.MapDelete("/api/reading/articles/{sid}", AdminApi.W(Delete)).RequireAuthorization();
        app.MapPatch("/api/reading/articles/{sid}", AdminApi.W(Patch)).RequireAuthorization();
    }

    private static readonly System.Text.RegularExpressions.Regex SidRe = new("^[0-9a-f]{10}$");

    /// <summary>The single ownership rule (list flag + delete + edit).</summary>
    public static bool CanManage(AuthUser u, string? addedBy)
    {
        if (u.Role == "admin") return true;
        if (string.IsNullOrEmpty(addedBy)) return false;
        return u.ReadingIngest switch
        {
            "web" => addedBy == "web",
            "agent" => !string.IsNullOrEmpty(u.MuseId) && addedBy == u.MuseId,
            _ => false,
        };
    }

    private static string Sid(HttpContext ctx)
    {
        var sid = ctx.Request.RouteValues["sid"]?.ToString() ?? "";
        if (!SidRe.IsMatch(sid)) throw new ApiError(404, "no such article") { Code = "not_found" };
        return sid;
    }

    /// <summary>TtsWeb failure → our error shape (never leaks the token; message is already redacted/snipped).</summary>
    private static ApiError Map(TtsWebClient.TtsWebError e) => e.Kind switch
    {
        "gone" => new ApiError(404, "article not found or already deleted") { Code = "not_found" },
        "t" => new ApiError(503, "reading service (TtsWeb) is unavailable, try again later") { Code = "ttsweb_unavailable" },
        "auth" => new ApiError(502, "reading service refused agnes-service's token") { Code = "ttsweb_auth_failed" },
        "conflict" => new ApiError(409, "the article changed meanwhile, reload and try again") { Code = "version_conflict" },
        _ => new ApiError(400, "reading service rejected the request: " + e.Message) { Code = "ttsweb_invalid" },
    };

    private static async Task<T> Tts<T>(Func<Task<T>> f)
    {
        try { return await f(); }
        catch (TtsWebClient.TtsWebError e) { throw Map(e); }
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static async Task List(HttpContext ctx)
    {
        var u = await AuthService.RequireUser(ctx);
        var arts = await Tts(() => TtsWebClient.ListArticles(ctx.RequestAborted));
        Dictionary<string, (Guid id, string status, string op)> active;
        try { active = await IngestStore.ActiveByShares(ctx.RequestAborted); }
        catch (Exception e) when (e is NpgsqlException or ApiError) { active = new(); }
        var list = arts.OfType<JsonObject>()
            .Where(a => a["deleted"] is null)
            .OrderByDescending(a => (string?)a["added_at"] ?? "", StringComparer.Ordinal)
            .Select(a =>
            {
                var sid = (string?)a["share_id"];
                var by = (string?)a["added_by"];
                var o = new JsonObject
                {
                    ["share_id"] = sid, ["title"] = (string?)a["title"], ["lang"] = (string?)a["lang"], ["status"] = (string?)a["status"],
                    ["added_by"] = by, ["added_via"] = (string?)a["added_via"], ["added_at"] = (string?)a["added_at"],
                    ["updated_at"] = (string?)a["updated_at"], ["sentences"] = Int(a["sentences"]),
                    ["current_version"] = Int(a["current_version"]), ["url"] = IngestApi.ShortUrl(sid),
                    ["can_manage"] = CanManage(u, by),
                };
                if (sid != null && active.TryGetValue(sid, out var j))
                    o["active_job"] = new JsonObject { ["id"] = j.id.ToString(), ["status"] = j.status, ["op"] = j.op };
                return (JsonNode)o;
            }).ToArray();
        await Json.Write(ctx, 200, new JsonObject { ["articles"] = new JsonArray(list) });
    }

    private static async Task Get(HttpContext ctx)
    {
        var u = await AuthService.RequireUser(ctx);
        var sid = Sid(ctx);
        var a = await Tts(() => TtsWebClient.GetArticle(sid, ctx.RequestAborted));
        var by = (string?)a["added_by"];
        await Json.Write(ctx, 200, new JsonObject
        {
            ["article"] = new JsonObject
            {
                ["share_id"] = (string?)a["share_id"], ["title"] = (string?)a["title"], ["lang"] = (string?)a["lang"],
                ["text"] = (string?)a["text"], ["status"] = (string?)a["status"], ["added_by"] = by,
                ["added_via"] = (string?)a["added_via"], ["added_at"] = (string?)a["added_at"],
                ["current_version"] = Int(a["current_version"]),
                ["sentences"] = (a["sentences"] as JsonArray)?.Count, ["url"] = IngestApi.ShortUrl((string?)a["share_id"]),
                ["can_manage"] = CanManage(u, by),
            },
        });
    }

    /// <summary>Owner-or-admin gate (403 article_forbidden); returns the TtsWeb article (404 when missing / deleted).</summary>
    private static async Task<JsonObject> RequireOwned(HttpContext ctx, AuthUser u, string sid)
    {
        var a = await Tts(() => TtsWebClient.GetArticle(sid, ctx.RequestAborted));
        if (!CanManage(u, (string?)a["added_by"]))
            throw new ApiError(403, "you may only delete or edit articles added through your own channel (admins: all)") { Code = "article_forbidden" };
        return a;
    }

    private static async Task Audit(HttpContext ctx, AuthUser u, string action, string sid, JsonObject detail)
    {
        try
        {
            await using var c = await Db.Open(ctx.RequestAborted);
            await using var tx = await c.BeginTransactionAsync(ctx.RequestAborted);
            await AuditService.Write(c, tx, u.Id, u.Username, Http.ClientIp(ctx), action, null, sid, detail);
            await tx.CommitAsync(ctx.RequestAborted);
        }
        catch (Exception e) when (e is NpgsqlException or ApiError)
        {
            Log.Warn($"article audit {action} {sid} not written: {e.GetType().Name}");   // the change itself already happened
        }
    }

    private static async Task Delete(HttpContext ctx)
    {
        var u = await AuthService.RequireUser(ctx);
        var sid = Sid(ctx);
        var a = await RequireOwned(ctx, u, sid);
        var res = await Tts(() => TtsWebClient.DeleteArticle(sid, ctx.RequestAborted));
        var cancelled = 0;
        try { cancelled = await IngestStore.CancelForShare(sid, CancellationToken.None); }
        catch (Exception e) when (e is NpgsqlException or ApiError) { Log.Warn($"article {sid} deleted; pending jobs not cancelled: {e.GetType().Name}"); }
        await Audit(ctx, u, "delete_article", sid, new JsonObject
        {
            ["title"] = (string?)a["title"], ["added_by"] = (string?)a["added_by"], ["version"] = Int(a["current_version"]),
            ["jobs_cancelled"] = cancelled,
        });
        await Json.Write(ctx, 200, new JsonObject
        {
            ["ok"] = true, ["share_id"] = (string?)res["share_id"] ?? sid, ["deleted_at"] = (string?)res["deleted_at"], ["jobs_cancelled"] = cancelled,
        });
    }

    private static ApiError Bad(string code, string msg) => new(400, msg) { Code = code };

    private static async Task Patch(HttpContext ctx)
    {
        var u = await AuthService.RequireUser(ctx);
        var sid = Sid(ctx);
        Http.RequireJsonContentType(ctx);
        var body = await Json.ReadBody(ctx, IngestApi.BodyMax);
        foreach (var (k, _) in body)
            if (k is not ("title" or "lang" or "text"))
                throw new ApiError(400, $"unknown field '{k}' (allowed: title, lang, text)") { Code = "unknown_field", Extra = new JsonObject { ["field"] = k } };
        string? title = null, lang = null, text = null;
        if (body["title"] != null)
        {
            if (body["title"] is not JsonValue tv || !tv.TryGetValue<string>(out var ts)) throw Bad("invalid_request", "'title' must be a string");
            title = ts.Trim();
            if (title.Length == 0) throw Bad("title_required", "'title' must not be empty");
            if (title.Length > 120) throw Bad("invalid_request", "'title' is too long (max 120)");
        }
        if (body["lang"] != null)
        {
            if (body["lang"] is not JsonValue lv || !lv.TryGetValue<string>(out var ls)) throw Bad("invalid_lang", "'lang' must be ja, zh or en");
            lang = ls.Trim().ToLowerInvariant();
            if (lang is not ("ja" or "zh" or "en")) throw Bad("invalid_lang", "'lang' must be ja, zh or en");
        }
        if (body["text"] != null)
        {
            if (body["text"] is not JsonValue xv || !xv.TryGetValue<string>(out var xs)) throw Bad("invalid_text", "'text' must be a string");
            text = xs.Replace("\r\n", "\n").Trim();
            if (text.Length == 0) throw Bad("invalid_text", "'text' must not be empty");
            if (text.Length > IngestApi.MaxText) throw Bad("invalid_text", $"'text' is too long (max {IngestApi.MaxText} chars)");
        }
        if (title == null && lang == null && text == null) throw Bad("invalid_request", "send at least one of title, lang, text");

        var a = await RequireOwned(ctx, u, sid);
        var curTitle = (string?)a["title"];
        var curLang = (string?)a["lang"] ?? "ja";
        var curText = (string?)a["text"] ?? "";
        var curV = Int(a["current_version"]) ?? 1;
        var newLang = lang ?? curLang;

        // normalized new text (TtsWeb's own normalization, via preview) decides "text changed?"
        string? normText = null;
        if (text != null)
        {
            var pv = await Tts(() => TtsWebClient.Preview(text, newLang, ctx.RequestAborted));
            normText = (string?)pv["text"] ?? text;
            if (normText == curText) normText = null;   // same original → metadata only
        }

        if (normText == null)
        {
            var tChange = title != null && title != curTitle ? title : null;
            var lChange = lang != null && lang != curLang ? lang : null;
            if (tChange == null && lChange == null)
            {
                await Json.Write(ctx, 200, new JsonObject { ["ok"] = true, ["mode"] = "noop", ["share_id"] = sid, ["version"] = curV });
                return;
            }
            var art = await Tts(() => TtsWebClient.PatchArticle(sid, tChange, lChange, ctx.RequestAborted));
            await Audit(ctx, u, "update_article", sid, new JsonObject
            {
                ["fields"] = string.Join(",", new[] { tChange != null ? "title" : null, lChange != null ? "lang" : null }.Where(x => x != null)),
                ["added_by"] = (string?)a["added_by"], ["version"] = curV,
            });
            await Json.Write(ctx, 200, new JsonObject
            {
                ["ok"] = true, ["mode"] = "meta", ["share_id"] = sid, ["version"] = Int(art["current_version"]) ?? curV,
                ["title"] = (string?)art["title"], ["lang"] = (string?)art["lang"], ["url"] = IngestApi.ShortUrl(sid),
            });
            return;
        }

        // ---- text changed: one ingest job (op=edit) on the same share_id, same admission / call_log as POST /api/reading/ingest
        if (await IngestStore.ActiveByShare(sid, ctx.RequestAborted) is { } busy)
            throw new ApiError(409, "this article is being processed right now; edit it after the job finishes")
                { Code = "job_active", Extra = new JsonObject { ["job"] = IngestStore.View(busy) } };
        var addedBy = (string?)a["added_by"];
        var channel = addedBy == "web" ? "web" : "agent";   // job channel follows the article (web → delayed retries, else L2)
        var sw = Stopwatch.StartNew();
        var preview = title ?? curTitle ?? normText;
        LimitService.Ticket ticket;
        try { ticket = await LimitService.Admit(u.Id, "ingest", ctx.RequestAborted); }
        catch (ApiError e)
        {
            var scope = e.Body?["scope"] is JsonValue sv && sv.TryGetValue<string>(out var sc) ? sc : null;
            await CallLog.Write(u.Id, u.Username, "ingest", null, e.Code ?? "rejected", e.Status, null, sw.ElapsedMilliseconds, preview, scope);
            throw;
        }
        await ticket.DisposeAsync();
        var logId = await CallLog.WriteReturningId(u.Id, u.Username, "ingest", null, "accepted", 202, preview,
            $"ch={channel};by={addedBy};share={sid};op=edit;base_v={curV}");
        IngestStore.Job job;
        try
        {
            job = await IngestStore.InsertEdit(u, channel, addedBy, sid, curV, title ?? curTitle, newLang, (string?)a["rate"], normText, logId, ctx.RequestAborted);
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            await ticket.Refund();
            if (logId != null) await CallLog.Update(logId.Value, null, "duplicate", 409, sw.ElapsedMilliseconds, "refund;job_active");
            throw new ApiError(409, "this article is being processed right now; edit it after the job finishes") { Code = "job_active" };
        }
        await Audit(ctx, u, "edit_article", sid, new JsonObject
        {
            ["job_id"] = job.Id.ToString(), ["added_by"] = addedBy, ["base_version"] = curV,
            ["title_changed"] = title != null && title != curTitle, ["lang_changed"] = lang != null && lang != curLang,
        });
        IngestWorker.Enqueue(job.Id);
        await Json.Write(ctx, 202, new JsonObject
        {
            ["ok"] = true, ["mode"] = "job", ["job"] = IngestStore.View(job), ["job_id"] = job.Id.ToString(), ["share_id"] = sid,
            ["url"] = IngestApi.ShortUrl(sid),
        });
    }
}
