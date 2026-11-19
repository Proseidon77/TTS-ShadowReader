// agnes-service (.NET 10 / ASP.NET Core minimal API): JWT-protected wrapper around the Agnes AI API
// for text / image / video generation, plus the single-page frontend (index.html).
// Design: DESIGN.md. The previous Python implementation is archived in python-legacy/.
//
//   dotnet publish/AgnesService.dll                 run the service (HOST/PORT, default 0.0.0.0:8793)
//   dotnet publish/AgnesService.dll --create-user …  operator user management (see --help-users)

using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using AgnesService;
using Microsoft.AspNetCore.Authentication.JwtBearer;

args = Settings.ConsumeEnvFileArgs(args);
if (args.Length > 0 && args[0] == "--init-env") return Settings.InitEnv();
if (args.Length > 0 && args[0] == "--self-test") return Analyze.SelfTest();   // TASK6: ruby/pos/validation unit checks
if (UserCli.IsCli(args)) return await UserCli.Run(args);
if (args.Length > 0) { Console.Error.WriteLine($"unknown argument {args[0]} (see --help-users)"); return 2; }

var started = DateTimeOffset.UtcNow;
var host = Settings.Get("HOST", "0.0.0.0");
var port = Settings.GetInt("PORT", 8793, 1, 65535);
var indexFile = Path.Combine(AppContext.BaseDirectory, "index.html");

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = Agnes.MaxBody + 1024 * 1024;
    k.Listen(IPAddress.Parse(host), port);
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(AuthService.ConfigureJwtBearer);
// "Admin" policy (dbapi): JWT role claim must be "admin". Handlers additionally re-check the role in the
// DB (AdminApi.RequireAdmin), so a demoted admin's still-unexpired token is refused too.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AdminApi.Policy, p => p.RequireAuthenticatedUser().RequireRole("admin"));
// TASK6 phase 2: ingest worker pool + retry / L2-reconcile scanner (src/IngestService.cs)
builder.Services.AddHostedService<IngestWorker>();

var app = builder.Build();

// ---- request log (method/path/status only; never headers or bodies) + JSON errors + security headers
app.Use(async (ctx, next) =>
{
    var sw = Stopwatch.StartNew();
    var h = ctx.Response.Headers;
    h.CacheControl = "no-store";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h.XFrameOptions = "DENY";
    try { await next(ctx); }
    catch (ApiError e) { await Json.Write(ctx, e.Status, e.Payload(), e.Headers); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* client went away */ }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException e) when (e.StatusCode == 413)
    { await Json.Write(ctx, 413, new JsonObject { ["error"] = "request body too large" }); }
    catch (Exception e)
    {
        Log.Error($"unhandled error on {ctx.Request.Method} {ctx.Request.Path}: {e.GetType().Name}: {Agnes.Redact(e.Message)}");
        await Json.Write(ctx, 500, new JsonObject { ["error"] = "internal server error" });
    }
    Log.Info($"{Http.ClientIp(ctx)} \"{ctx.Request.Method} {ctx.Request.Path}\" {ctx.Response.StatusCode} {sw.ElapsedMilliseconds}ms");
});
app.UseAuthentication();
app.UseAuthorization();

// ---- public
async Task ServeIndex(HttpContext ctx)
{
    if (!File.Exists(indexFile)) throw new ApiError(500, "index.html missing");
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src https: http: data: blob:; " +
        "media-src https: http: blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await ctx.Response.SendFileAsync(indexFile);
}
app.MapMethods("/", new[] { "GET", "HEAD" }, ServeIndex);
app.MapMethods("/index.html", new[] { "GET", "HEAD" }, ServeIndex);
// TASK6 phase 2 (PHASE2-AUTH-DESIGN §5.3): bookmarkable「添加文章」entry — same page, the front end opens that tab
app.MapMethods("/add", new[] { "GET", "HEAD" }, ServeIndex);

app.MapGet("/health", async ctx =>
{
    var db = await Db.Status();
    var dbJson = new JsonObject();
    foreach (var (k, v) in db) dbJson[k] = v is bool b ? JsonValue.Create(b) : null;
    await Json.Write(ctx, 200, new JsonObject
    {
        ["status"] = "ok",
        ["version"] = Agnes.Version,
        ["stack"] = "dotnet",
        ["key_configured"] = !string.IsNullOrEmpty(Settings.AgnesApiKey),
        ["auth"] = new JsonObject
        {
            ["type"] = "jwt", ["jwt_secret"] = AuthService.SecretStatus(),
            ["access_token_minutes"] = AuthService.AccessTtl / 60, ["db"] = dbJson,
        },
        ["limits_default"] = new JsonObject
        {
            ["per_minute"] = LimitService.DefaultPerMinute, ["per_day"] = LimitService.DefaultPerDay,
            ["video_per_day"] = LimitService.DefaultVideoPerDay, ["concurrent"] = LimitService.DefaultConcurrent,
            ["global_concurrent"] = LimitService.GlobalConcurrent, ["day_boundary"] = "00:00 Asia/Tokyo",
        },
        ["upstream"] = Settings.AgnesBase,
        ["analyze"] = Providers.HealthView(),
        ["ingest"] = new JsonObject
        {
            ["workers"] = IngestApi.Workers, ["deadline_s"] = Providers.IngestDeadline,
            ["ttsweb_token"] = !string.IsNullOrEmpty(TtsWebClient.Token),
            ["public_url_configured"] = IngestApi.PublicUrl != null,
        },
        ["uptime_s"] = (long)(DateTimeOffset.UtcNow - started).TotalSeconds,
    });
});

// ---- auth (cookie endpoints: JSON content-type required = CSRF guard)
const long AuthBodyMax = 16 * 1024;

app.MapPost("/api/auth/login", async ctx =>
{
    Http.RequireJsonContentType(ctx);
    var body = await Json.ReadBody(ctx, AuthBodyMax);
    var res = await AuthService.Login(Http.OptString(body, "username"), Http.OptString(body, "password"),
        Http.ClientIp(ctx), ctx.Request.Headers.UserAgent.ToString());
    Http.SetRefreshCookie(ctx, res.RefreshPlain, res.MaxAge);
    await Json.Write(ctx, 200, res.Body);
});

app.MapPost("/api/auth/refresh", async ctx =>
{
    Http.RequireJsonContentType(ctx);
    await Json.ReadBody(ctx, AuthBodyMax, allowEmpty: true);
    LoginResult res;
    try { res = await AuthService.Refresh(Http.RefreshCookie(ctx), Http.ClientIp(ctx), ctx.Request.Headers.UserAgent.ToString()); }
    // dead cookie: clear it — except a concurrent refresh from another tab within the 10 s rotation grace
    // (refresh_rotated): the browser already holds the new cookie, clearing it would log both tabs out
    catch (ApiError e) when (e.Status == 401 && e.Code != "refresh_rotated") { Http.SetRefreshCookie(ctx, "", 0); throw; }
    Http.SetRefreshCookie(ctx, res.RefreshPlain, res.MaxAge);
    await Json.Write(ctx, 200, res.Body);
});

app.MapPost("/api/auth/logout", async ctx =>
{
    Http.RequireJsonContentType(ctx);
    await Json.ReadBody(ctx, AuthBodyMax, allowEmpty: true);
    var revoked = await AuthService.Logout(Http.RefreshCookie(ctx));
    Http.SetRefreshCookie(ctx, "", 0);
    await Json.Write(ctx, 200, new JsonObject { ["ok"] = true, ["revoked"] = revoked });
});

// ---- protected (JwtBearer via RequireAuthorization, then per-request user-state check)
app.MapGet("/api/auth/me", async ctx =>
{
    var u = await AuthService.RequireUser(ctx);
    var res = new JsonObject
    {
        // reading_ingest (phase 2) lets the page show the「添加文章」tab; cosmetic — POST /api/reading/ingest decides
        ["user"] = new JsonObject { ["id"] = u.Id, ["username"] = u.Username, ["role"] = u.Role, ["reading_ingest"] = u.ReadingIngest },
        ["expires_at"] = u.ExpiresAt,
    };
    try
    {
        var lim = await LimitService.View(u.Id, ctx.RequestAborted);
        res["limits"] = lim["limits"]!.DeepClone();
        res["usage"] = lim["usage"]!.DeepClone();
    }
    catch (ApiError e) { res["limits_error"] = e.Message; }   // /me still works if the limits migration is missing
    await Json.Write(ctx, 200, res);
}).RequireAuthorization();

// "My account": change own password (verifies the current one). Re-issues tokens + cookie.
app.MapPost("/api/auth/change-password", AdminApi.W(async ctx =>
{
    var u = await AuthService.RequireUser(ctx);
    Http.RequireJsonContentType(ctx);
    var body = await Json.ReadBody(ctx, AuthBodyMax);
    var res = await AuthService.ChangePassword(u, body, Http.ClientIp(ctx), ctx.Request.Headers.UserAgent.ToString());
    Http.SetRefreshCookie(ctx, res.RefreshPlain, res.MaxAge);
    await Json.Write(ctx, 200, res.Body);
})).RequireAuthorization();

app.MapGet("/api/limits", async ctx =>
{
    var u = await AuthService.RequireUser(ctx);
    await Json.Write(ctx, 200, await LimitService.View(u.Id, ctx.RequestAborted));
}).RequireAuthorization();

app.MapGet("/api/models", async ctx =>
{
    await AuthService.RequireUser(ctx);
    await Json.Write(ctx, 200, await Agnes.Models(ctx.RequestAborted));
}).RequireAuthorization();

app.MapGet("/api/video/status/{id}", async (HttpContext ctx, string id) =>
{
    await AuthService.RequireUser(ctx);           // polling is NOT counted against quota
    await Json.Write(ctx, 200, await Agnes.DoVideoStatus(id, ctx.Request.Query["model"].FirstOrDefault(), ctx.RequestAborted));
}).RequireAuthorization();

// Generation: auth → body/validation (400 costs nothing) → limiter admission (counts) → upstream.
// Every call that reaches the limiter (admitted or 429-rejected) is recorded in agnes_auth.call_log
// for the admin 「调用记录」 view (CallLog.cs); pre-admission validation 400s are not logged.
// TASK6 B9: optional `preview` selects call_log.prompt_preview from the body (default: body.prompt);
// optional `outcome` is filled by the run (actual provider:model, compact trail, refund decision) and,
// when given, replaces the "any upstream 4xx → refund" rule used by text/image/video.
async Task Generate(HttpContext ctx, string kind, Func<JsonObject, Func<CancellationToken, Task<JsonObject>>> prepare,
    Func<JsonObject, string?>? preview = null, CallOutcome? outcome = null)
{
    var u = await AuthService.RequireUser(ctx);
    var body = await Json.ReadBody(ctx, Agnes.MaxBody);
    var run = prepare(body);
    var prompt = preview != null ? preview(body)
        : body["prompt"] is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps : null;
    var reqModel = body["model"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;
    var sw = Stopwatch.StartNew();
    LimitService.Ticket ticket;
    try { ticket = await LimitService.Admit(u.Id, kind, ctx.RequestAborted); }
    catch (ApiError e)
    {
        var scope = e.Body?["scope"] is JsonValue sv && sv.TryGetValue<string>(out var sc) ? sc : null;
        await CallLog.Write(u.Id, u.Username, kind, reqModel, e.Code ?? "rejected", e.Status, null, sw.ElapsedMilliseconds, prompt, scope);
        throw;
    }
    await using var _ticket = ticket;
    JsonObject result;
    try { result = await run(ctx.RequestAborted); }
    catch (ApiError e) when (outcome != null)
    {
        if (outcome.Refund) await ticket.Refund();   // every upstream call answered 4xx: nothing was spent upstream
        await CallLog.Write(u.Id, u.Username, kind, outcome.Model ?? reqModel, e.Code ?? "error", e.Status, e.UpstreamStatus,
            sw.ElapsedMilliseconds, prompt, (outcome.Refund ? "refund;" : "") + (outcome.Detail ?? e.Message));
        throw;
    }
    catch (ApiError e) when (e.Code == "upstream_throttled")
    {
        await ticket.Refund();   // phase 2 (P2R-4): our own upstream token bucket refused it, nothing was sent upstream
        await CallLog.Write(u.Id, u.Username, kind, reqModel, "upstream_throttled", e.Status, null, sw.ElapsedMilliseconds, prompt, "refund;" + e.Message);
        throw;
    }
    catch (ApiError e) when (e.UpstreamStatus is >= 400 and < 500)
    {
        await ticket.Refund();   // Agnes rejected the request: no upstream cost, give the daily unit back
        await CallLog.Write(u.Id, u.Username, kind, reqModel, "upstream_rejected", e.Status, e.UpstreamStatus, sw.ElapsedMilliseconds, prompt, e.Message);
        throw;
    }
    catch (ApiError e)
    {
        await CallLog.Write(u.Id, u.Username, kind, reqModel, e.Code ?? "error", e.Status, e.UpstreamStatus, sw.ElapsedMilliseconds, prompt, e.Message);
        throw;
    }
    catch (Exception e)
    {
        await CallLog.Write(u.Id, u.Username, kind, reqModel, "error", 500, null, sw.ElapsedMilliseconds, prompt, e.GetType().Name);
        throw;
    }
    var resModel = outcome?.Model ?? (result["model"] is JsonValue rv && rv.TryGetValue<string>(out var rm) ? rm : reqModel);
    await CallLog.Write(u.Id, u.Username, kind, resModel, "success", 200, null, sw.ElapsedMilliseconds, prompt, outcome?.Detail);
    await Json.Write(ctx, 200, result);
}

// ASP0016 misfires on the inner `ct => …` upstream lambdas below (they are not RequestDelegates).
#pragma warning disable ASP0016
app.MapPost("/api/text", ctx => Generate(ctx, "text", body =>
{
    var (payload, model) = Agnes.BuildText(body);
    return ct => Agnes.DoText(payload, model, ct);
})).RequireAuthorization();

app.MapPost("/api/image", ctx => Generate(ctx, "image", body =>
{
    var req = Agnes.BuildImage(body);
    return ct => Agnes.DoImage(req, ct);
})).RequireAuthorization();

app.MapPost("/api/video", ctx => Generate(ctx, "video", body =>
{
    var (payload, model) = Agnes.BuildVideo(body);
    return ct => Agnes.DoVideoCreate(payload, model, ct);
})).RequireAuthorization();

// TASK6 phase 1: Japanese analysis (raw) / skeleton translation + reading proofing (enrich). kind=analyze,
// one admission per request however many upstream calls the fallback ladder makes (src/Analyze.cs).
app.MapPost("/api/analyze", ctx =>
{
    var oc = new CallOutcome();
    return Generate(ctx, "analyze", body =>
    {
        var req = Analyze.Parse(body);
        return ct => Analyze.Run(req, oc, ct);
    }, Analyze.Preview, oc);
}).RequireAuthorization();
#pragma warning restore ASP0016

// ---- admin backend (TASK5): /api/admin/*, Admin policy; see src/AdminApi.cs
AdminApi.Map(app);

// ---- TASK6 phase 2: reading ingest orchestration (POST /api/reading/ingest, jobs, L2 list); src/IngestService.cs
IngestApi.Map(app);

app.MapFallback(ctx => Json.Write(ctx, 404, new JsonObject { ["error"] = "not found" }));

var dbs = await Db.Status();
Log.Info($"agnes-service {Agnes.Version} (.NET {Environment.Version}) listening on {host}:{port} " +
         $"(agnes key: {!string.IsNullOrEmpty(Settings.AgnesApiKey)}, jwt secret: {AuthService.SecretStatus()}, " +
         $"db configured: {dbs["dsn_configured"]}, db reachable: {dbs["reachable"]}, schema ready: {dbs["schema_ready"]}, " +
         $"limits ready: {dbs["limits_ready"]}, env file(s): {string.Join(", ", Settings.EnvFiles)})");
await app.RunAsync();
return 0;

namespace AgnesService
{
    internal static class Http
    {
        /// <summary>Peer address; with TRUST_PROXY=1 the right-most X-Forwarded-For entry.</summary>
        public static string ClientIp(HttpContext ctx)
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (Settings.GetBool("TRUST_PROXY"))
            {
                var parts = ctx.Request.Headers["X-Forwarded-For"].ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0 && IPAddress.TryParse(parts[^1], out var fwd)) ip = fwd;
            }
            if (ip == null) return "127.0.0.1";
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return ip.ToString();
        }

        public static void RequireJsonContentType(HttpContext ctx)
        {
            // Browsers cannot send application/json cross-site without a CORS preflight (never approved).
            var ct = (ctx.Request.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            if (ct != "application/json") throw new ApiError(415, "Content-Type must be application/json");
        }

        public static string? OptString(JsonObject body, string name) =>
            body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        private static bool IsHttps(HttpContext ctx)
        {
            var mode = Settings.Get("COOKIE_SECURE", "auto").ToLowerInvariant();
            if (mode is "1" or "true" or "yes") return true;
            if (mode is "0" or "false" or "no") return false;
            return ctx.Request.Headers["X-Forwarded-Proto"].ToString().Equals("https", StringComparison.OrdinalIgnoreCase);
        }

        public static void SetRefreshCookie(HttpContext ctx, string value, int maxAge)
        {
            var c = $"{AuthService.CookieName}={value}; Path={AuthService.CookiePath}; Max-Age={maxAge}; HttpOnly; SameSite=Strict";
            if (IsHttps(ctx)) c += "; Secure";
            ctx.Response.Headers.Append("Set-Cookie", c);
        }

        /// <summary>Parsed by hand: other services on this host may send cookies a strict parser rejects.</summary>
        public static string? RefreshCookie(HttpContext ctx)
        {
            foreach (var part in ctx.Request.Headers.Cookie.ToString().Split(';'))
            {
                var p = part.Trim();
                var eq = p.IndexOf('=');
                if (eq > 0 && p[..eq] == AuthService.CookieName && eq + 1 < p.Length) return p[(eq + 1)..].Trim('"');
            }
            return null;
        }
    }
}
