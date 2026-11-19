# agnes-service — design note

Revision 4 (2026-11-18):
- rev 2 (TASK2): JWT authentication replaced the optional `SERVICE_TOKEN` of rev 1.
- rev 3 (TASK3): backend rebuilt in **.NET 10 / ASP.NET Core**; same paths, JSON shapes, auth
  design and schema. The Python implementation is **archived under `python-legacy/`** (reference
  only, not deployed).
- rev 4 (TASK4): **per-account API limits** (per-minute rate, daily quota + video sub-quota,
  concurrency) — see "Per-account limits".

## Stack

**.NET 10 (SDK 10.0.404, ASP.NET Core 10.0.15) minimal API**, run with
`/data/dotnet-sdk/dotnet publish/AgnesService.dll`. Packages (the same ones dbapi uses, both
restored from the local NuGet cache, no network needed): **Npgsql 9.0.3** (PostgreSQL) and
**Microsoft.AspNetCore.Authentication.JwtBearer 10.0.0** (access-token validation; tokens are
issued with `Microsoft.IdentityModel.JsonWebTokens`, which it brings in). PBKDF2 is
`Rfc2898DeriveBytes.Pbkdf2` (BCL). Kestrel serves everything; no reverse proxy required.

| File | Role |
|---|---|
| `AgnesService.csproj` | project; ships `index.html` + `auth_schema.sql` into `bin/` and `publish/` |
| `Program.cs` | host, middleware (JSON errors, security headers, request log), all routes, CLI dispatch |
| `src/AuthService.cs` | JWT issue + JwtBearer config, login / refresh / logout / lockout, per-request user-state check |
| `src/LimitService.cs` | per-account limits (TASK4) |
| `src/Agnes.cs` | Agnes upstream client, input validation, text / image / video handlers |
| `src/Passwords.cs` | dbapi-format PBKDF2 hashes |
| `src/Db.cs` | Npgsql data source (pooled), `/health` DB probe, logger |
| `src/Settings.cs` | env + `.env` (re-read on change), `--env-file`, `--init-env` |
| `src/UserCli.cs` | operator CLI (`--create-user` …) |
| `index.html` | frontend (unchanged shapes; TASK4 added quota display + 429 messages) |
| `auth_schema.sql` | idempotent DDL incl. TASK4 migration |
| `smoke.sh`, `install.sh`, `agnes-service.service` | smoke test, installer, systemd unit |
| `tests/MockAgnes/`, `tests/mock.env`, `tests/run_dev.sh` | local fake Agnes API for limiter / proxy tests |
| `python-legacy/` | archived Python implementation (rev 2) |

## Endpoints (port 8793, bind 0.0.0.0)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/` | public | frontend page |
| GET | `/health` | public | status, `key_configured`, auth readiness (no secrets) |
| POST | `/api/auth/login` | public | `{username,password}` → access token + refresh cookie |
| POST | `/api/auth/refresh` | refresh cookie | rotate refresh token → new access token |
| POST | `/api/auth/logout` | refresh cookie (optional) | revoke session, clear cookie |
| GET | `/api/auth/me` | **JWT** | current user (+ effective limits and today's usage) |
| GET | `/api/limits` | **JWT** | effective limits and today's usage (TASK4) |
| GET | `/api/models` | **JWT** | model list grouped by type |
| POST | `/api/text` | **JWT** | text generation |
| POST | `/api/image` | **JWT** | image generation (synchronous) |
| POST | `/api/video` | **JWT** | create video job (async) |
| GET | `/api/video/status/<id>?model=…` | **JWT** | poll video job |

**JWT** = header `Authorization: Bearer <access token>`. Missing/invalid/expired/revoked → **401**
JSON with `WWW-Authenticate: Bearer`. `/api/models` is protected too (TASK2 default): it costs
nothing, but when the key is set it proxies Agnes with the user's key, and nothing on the page is
usable before login anyway. Auth is checked *before* the body is read/validated, so an anonymous
`POST /api/text {}` is 401, not 400.

### Request / response shapes

`POST /api/auth/login` (Content-Type must be `application/json`)
```json
{"username":"alice","password":"…"}
→ 200 {"access_token":"<jwt>","token_type":"Bearer","expires_in":1800,
       "user":{"id":1,"username":"alice","role":"admin"}}
   Set-Cookie: agnes_rt=<opaque>; Path=/api/auth; Max-Age=604800; HttpOnly; SameSite=Strict[; Secure]
```
Errors: 400 missing fields · 401 `invalid_credentials` (same for unknown user and wrong password)
· 423 `locked` (+`retry_after` s, `Retry-After`) · 403 `disabled` · 403 `ip_blocked` · 429
`ip_throttled` · 503 `jwt_secret_missing` / `db_unavailable`. Every error body has `error`
(English) and `code`; the frontend maps `code` to Chinese messages.

`POST /api/auth/refresh` (body `{}` or empty, cookie `agnes_rt`) → same 200 body as login plus a
**new** cookie; 401 `invalid_refresh_token` / `no_refresh_token` (cookie cleared).
`POST /api/auth/logout` → `{"ok":true,"revoked":bool}` and the cookie cleared.
`GET /api/auth/me` → `{"user":{id,username,role},"expires_at":<unix>, "limits":{…}, "usage":{…}}`
(`limits`/`usage` as in `/api/limits`, added in rev 4; if the limits tables are missing, `/me`
still answers with `limits_error` instead).

`POST /api/text`
```json
{"prompt":"…", "model":"agnes-2.5-flash", "system":"…", "temperature":0.7, "max_tokens":1024}
→ {"text":"…", "reasoning":"…", "usage":{…}, "model":"agnes-2.5-flash", "finish_reason":"stop"}
```
Only `prompt` required. Upstream: `POST /v1/chat/completions {model, messages}`; `reasoning`
comes from `message.reasoning_content`.

`POST /api/image`
```json
{"prompt":"…", "model":"agnes-image-2.5-flash", "size":"1K", "ratio":"16:9",
 "response_format":"url", "image":"https://…  (or data: URI, or list) for image-to-image"}
→ {"url":"https://…png", "model":"…", "size":"1K", "ratio":"16:9"}       (or "b64":"…")
```
Defaults: model `agnes-image-2.5-flash`, size `1K`, format `url`. Upstream body:
`{model, prompt, size, ratio?, extra_body:{response_format, image?}}` — `response_format`
inside `extra_body` as verified by the operator and stated in the docs.

`POST /api/video`
```json
{"prompt":"…", "model":"agnes-video-2.5-flash", "seconds":"5", "size":"720P",
 "aspect_ratio":"16:9", "seed":1, "mode":"text|keyframe|reference",
 "first_frame":"url", "last_frame":"url", "images":["url"], "audios":["url"],
 "videos":[{"url":"…","start_seconds":0,"require_audio":false}]}
→ {"id":"video_…", "task_id":"task_…", "model":"…", "status":"queued", "progress":0,
   "url":null, "error":null, "seconds":"5", "size":"720P",
   "poll":"/api/video/status/video_…?model=agnes-video-2.5-flash"}
```
`mode` is inferred if omitted (frames → keyframe, images/audios/videos → reference, else text).
Local validation mirrors the docs: seconds 4–12 (sent as string), size ∈ 720P/1080P/1K/2K,
`-flash` models only 720P, aspect_ratio ∈ 21:9/16:9/4:3/1:1/3:4/9:16.

`GET /api/video/status/<id>?model=…` → same shape; `url` set only when `status=="completed"`;
`error` (string) when `failed`. The frontend always sends `model` so polling survives restarts.

`GET /health`
```json
{"status":"ok","version":"3.0.0","stack":"dotnet","key_configured":false,
 "auth":{"type":"jwt","jwt_secret":"ok|missing|too_short","access_token_minutes":30,
         "db":{"driver":true,"dsn_configured":true,"reachable":true,"schema_ready":true,"limits_ready":true}},
 "limits_default":{"per_minute":10,"per_day":200,"video_per_day":20,"concurrent":2,
                   "global_concurrent":6,"day_boundary":"00:00 Asia/Tokyo"},
 "upstream":"https://apihub.agnes-ai.com","uptime_s":12}
```
(`stack`, `limits_ready` and `limits_default` are additions; existing fields unchanged.)

### Errors
All errors are JSON: `{"error":"…", "code"?, "upstream_status"?, "upstream_body"?(first 600 chars)}`.

| Case | HTTP |
|---|---|
| bad/missing input | 400 |
| missing / invalid / expired / revoked access token | **401** |
| disabled account, banned IP (login) | 403 |
| login/refresh/logout without `Content-Type: application/json` | 415 |
| account locked | 423 |
| body > 20 MB (16 KB for auth endpoints) | 413 |
| per-account limit hit (`rate_limited` / `daily_quota_exceeded` / `concurrency_limit`), global cap (`concurrency_limit`, scope `global`) | **429** (body: see "Per-account limits") |
| IP login throttle | 429 `ip_throttled` |
| `AGNES_API_KEY` not configured | **503** `{"error":"AGNES_API_KEY not configured"}` |
| `JWT_SECRET` missing/short, DB not configured/unreachable | 503 (`jwt_secret_missing` / `db_unavailable`) |
| limits tables missing / unreadable (fail closed) | 503 `limits_unavailable` |
| Agnes 400/401/402/403/404/409/413/422/429 | passed through with same code |
| Agnes 5xx / non-JSON / unexpected shape / unreachable | 502 |
| Agnes timeout | 504 |

## Authentication (TASK2, ported to .NET in TASK3)

### .NET implementation notes (rev 3)
- **Behaviour is unchanged from rev 2** (everything below still applies); `python-legacy/auth.py`
  was ported 1:1 to `src/AuthService.cs` — same SQL, same transaction boundaries (failure counters
  and audit rows are committed even when the login fails), same error codes.
- Access tokens are **validated by ASP.NET Core's JwtBearer handler**: `ValidAlgorithms=[HS256]`
  (blocks `alg=none`/switching), issuer + audience pinned to `agnes-service`, 30 s clock skew,
  `exp` required, `MapInboundClaims=false` (raw claim names). The signing key is resolved per
  request from the current `JWT_SECRET`, so `.env` changes need no restart. `OnTokenValidated`
  rejects tokens whose `typ` claim isn't `access`; `OnChallenge` writes the same JSON 401 +
  `WWW-Authenticate: Bearer realm="agnes-service"[, error="invalid_token"]` as before, or 503
  `jwt_secret_missing` if a bearer token arrives while the secret is missing/short.
- After JwtBearer accepts the token, every protected handler runs the DB user-state check
  (`is_active`, `token_version` vs `ver`, role; cached 10 s) — 401 `token revoked` / `account
  disabled`, 503 `db_unavailable` (fail closed).
- Unauthenticated requests are rejected by the authorization middleware **before the body is
  read**, so anonymous `POST /api/text {}` is still 401, not 400.
- **10 s rotation grace: kept** (verified: a rotated cookie re-presented within 10 s → 401
  "already rotated", session intact; after 10 s → 401 "reuse detected", whole family revoked).
- Tokens issued by the Python round and the .NET build are interchangeable for the same
  `JWT_SECRET` (same claims, HS256); stored refresh tokens and password hashes are unchanged.
- **Password hashes**: `.NET` writes `pbkdf2-sha512$210000$<std b64 16 B>$<std b64 64 B>` (134
  chars — verified in the DB, identical shape to rev 2/dbapi); verification additionally accepts
  url-safe / unpadded base64 in case dbapi uses that flavour. `/data/src/db-api` was **still not
  readable** in this round, so byte-level compatibility with dbapi's code remains inferred from
  its stored hashes, not confirmed from source.

### What dbapi does, and how I learned it
I was **not permitted to read `/data/src/db-api`** (read access outside the project dir is
denied in this session). Instead I inspected dbapi's **live auth tables** read-only via `psql`
(they are in database `dbapi`, not `db77database`): `app_user(id, username, password_hash, role,
is_disabled, token_version, failed_logins, locked_until, last_login_at, created_at, updated_at)`,
`refresh_token(id uuid, user_id, family_id, token_hash bytea unique, issued_at, expires_at,
family_expires_at, used_at, revoked_at, replaced_by, user_agent, ip)`,
`ip_block(ip PK, lockout_count, blocked_at, updated_at)`, `audit_log(…)`. I also checked the
*shape* of its stored hashes (with the salt and hash redacted): `pbkdf2-sha512$210000$<b64>$<b64>`, 134 chars
→ 16-byte salt, 64-byte derived key. The behaviour (JWT bearer + refresh cookie rotation, PBKDF2,
5 failures → 15 min lock, repeated locks per IP → IP ban) is from TASK2's description. Its JWT
claim names, access-token lifetime and exact rotation code could **not** be verified.

### Mirrored from dbapi
- **Password format** identical: `pbkdf2-sha512$210000$<salt b64>$<hash b64>`, 16-byte random
  salt, 64-byte key, constant-time compare. (Format inferred from the stored hashes; whether
  dbapi uses standard or url-safe base64 isn't verified, so cross-verification isn't promised.)
- **Tables**: `users` ≈ `app_user` (with `is_active` instead of `is_disabled`, as TASK2 asks),
  incl. `token_version`, `failed_logins`, `locked_until`; `refresh_tokens` = `refresh_token`
  column-for-column (+`revoke_reason`); `ip_block` (+`blocked_until`); `login_audit` is a
  login-only cut of `audit_log`.
- **Access token**: HS256 JWT, Bearer header, short-lived; contains `ver` = `token_version`, so
  bumping the version (password reset, disable, `--logout-all`) invalidates every outstanding
  access token at once.
- **Refresh token**: opaque random (`secrets.token_urlsafe(48)`) in an **httpOnly cookie**,
  stored only as **SHA-256 bytea**; **rotated on every use** (old row gets `used_at`,
  `revoked_at`, `replaced_by`); **reuse detection**: presenting an already-rotated token revokes
  the whole **family** (one family per login); **sliding expiry** 7 days per token, capped by an
  **absolute family lifetime** of 30 days (`family_expires_at`).
- **Lockout**: 5 consecutive failures → account locked 15 min (counter reset on lock and on
  success); each lock is counted against the source IP in `ip_block.lockout_count`; 3 lockouts
  → IP banned (`blocked_at`).

### Simplified / changed, and why
- **Roles**: `user` and `admin` only (dbapi: viewer/editor/admin — table-editing roles that make
  no sense here). Both may generate; admin has no extra API powers yet (user management is the
  local CLI, so there is no admin HTTP surface to attack).
- **IP ban expires** after 24 h by default (`IP_BAN_HOURS`, `0` = permanent like dbapi) and
  loopback is never banned. Reason: if the page is published through a reverse proxy without
  `TRUST_PROXY=1`, every visitor shares one IP, and a permanent ban would lock everyone out.
  The lockout counter also decays after 24 h of quiet.
- **Extra per-IP throttle**: ≥ 20 failed logins from one IP in 15 min → 429 (catches
  password spraying across many usernames, which per-account lockout alone doesn't);
  reaching the threshold also counts as an IP lockout event.
- **No audit of generation calls** (only logins); request logs carry method/path/status.
- **Rotation grace**: a token rotated < 10 s ago and presented again is refused with 401 but
  does *not* trigger family revocation (two tabs refreshing at once is not theft).
- **User state check**: each authenticated request checks `is_active` + `token_version` in the
  DB, cached 10 s per user, so disabling a user takes effect within ~10 s instead of at token
  expiry. If the DB is down, protected endpoints fail closed with 503.

### Token lifetimes
| Token | Lifetime | Env |
|---|---|---|
| access JWT | **30 min** (clock leeway 30 s) | `JWT_ACCESS_MINUTES` |
| refresh token | **7 days** sliding, rotated on each use | `REFRESH_TOKEN_DAYS` |
| session family | **30 days** absolute | `REFRESH_FAMILY_DAYS` |

JWT claims: `iss`=`aud`=`agnes-service`, `sub` (user id), `name`, `role`, `ver`,
`typ:"access"`, `iat`, `nbf`, `exp`, `jti`. Verification pins `alg` to HS256 (rejects `none` and
algorithm switching), checks signature (constant-time), `exp`/`nbf`, `iss`, `aud`, `typ`.
`JWT_SECRET` must be ≥ 32 chars; if it is missing the service refuses to issue or verify tokens
(503 with an explanation), while `/` and `/health` keep working. Rotating `JWT_SECRET` logs
everyone out (access tokens fail; refresh still works, so in practice users just get a new
token — to force re-login, use `--logout-all <name>`).

### Refresh design choice: httpOnly cookie (dbapi style), not refresh-in-body
The page is served from the same origin as the API, so a cookie costs nothing and keeps the
long-lived credential out of JavaScript (an XSS can't exfiltrate it). The access token is kept
**only in memory** (never localStorage); on page load the frontend calls `/api/auth/refresh` to
resume the session from the cookie. Cookie: `HttpOnly; SameSite=Strict; Path=/api/auth`
(sent only to the three auth endpoints), `Secure` when `X-Forwarded-Proto: https` or
`COOKIE_SECURE=1`. CSRF: SameSite=Strict, plus login/refresh/logout require
`Content-Type: application/json` (cross-site forms can't send it without a CORS preflight, and
the service never answers CORS). Generation endpoints use the Bearer header, which browsers
never attach automatically, so they are not CSRF-able.

Logout revokes the whole family of the presented cookie. An access token already issued stays
valid until its 30 min expiry (stateless JWT; same trade-off as dbapi); `--logout-all` bumps
`token_version` for immediate effect.

### Schema `agnes_auth` (in `db77database`)
Name as suggested by TASK2: clearly owned by this service and separate from `public`, `drama`
and `jpdb`. Tables: `users` (+ rev 4 limit-override columns), `refresh_tokens`, `ip_block`,
`login_audit`, and (rev 4) `usage_daily` — see
`auth_schema.sql` (idempotent: `CREATE … IF NOT EXISTS`, one transaction; also grants rights to
an optional `agnes_service` role if it exists). Usernames are unique case-insensitively
(`lower(username)` index) and follow dbapi's pattern `^[a-zA-Z0-9_.-]{3,32}$`.

### Users / passwords
No seed or default account. The operator CLI is built into the service binary (no HTTP surface):
`dotnet publish/AgnesService.dll --create-user <name> [user|admin] [--rate-per-minute N]
[--daily-quota N] [--video-daily-quota N] [--max-concurrent N]` prompts twice without echo
(`Console.ReadKey(intercept)`), enforces ≥ 10 chars / ≥ 4 distinct chars, and inserts the PBKDF2
hash; also `--reset-password`, `--disable/--enable`, `--unlock`, `--logout-all`, `--list-users`,
`--unblock-ip`. `--password-stdin` exists for scripted test accounts (never pass passwords as
arguments; refused if stdin is not a terminal and the flag is absent). Unknown usernames still
run a dummy PBKDF2 so response timing doesn't reveal which accounts exist. (A lock/disabled
response does reveal that an account exists, as in dbapi; accepted for a small private tool.)

### DB access
`AGNES_DB_CONNECTION` (Npgsql connection string) from env / `.env`, never hardcoded;
`--init-env` writes `Host=/var/run/postgresql;Database=db77database;Username=user` (unix-socket
peer auth as OS user `user`, how the Python round connected — no password). A libpq-style
`AGNES_DB_DSN` from an old `.env` is still understood as a fallback. **Caveat:** that role is a
superuser. Recommended least-privilege setup (README): create role `agnes_service` with a password,
re-run `auth_schema.sql` (it grants `USAGE` on the schema plus DML on its tables, including
`usage_daily`), and point the connection string at it. Connections come from a pooled
`NpgsqlDataSource` (5 s connect timeout; 3 s for the `/health` probe, cached 30 s).

## Per-account limits (TASK4)

### What counts
| Endpoint | Counted? | Kind |
|---|---|---|
| `POST /api/text` | **yes** | `text` |
| `POST /api/image` | **yes** | `image` |
| `POST /api/video` (job creation) | **yes** | `video` |
| `GET /api/video/status/<id>` (polling) | no | — |
| `GET /api/models`, `/api/limits`, `/api/auth/*`, `/`, `/health` | no | — |

Login/refresh/logout are **not** part of this system; they keep the separate lockout / IP-throttle
logic from the auth section. The two systems share no counters or tables.

### Limits and defaults
| Limit | Default | Env (system default) | Per-user override column (`agnes_auth.users`) |
|---|---|---|---|
| per-minute rate (per account) | **10** | `LIMITS_DEFAULT_PER_MINUTE` | `rate_per_minute` |
| daily quota, all kinds together (per account) | **200** | `LIMITS_DEFAULT_PER_DAY` | `daily_quota` |
| daily video sub-quota (per account; also counts toward the daily quota) | **20** | `LIMITS_DEFAULT_VIDEO_PER_DAY` | `video_daily_quota` |
| concurrent generations in flight (per account) | **2** | `LIMITS_DEFAULT_CONCURRENT` | `max_concurrent` |
| concurrent generations in flight (all accounts) | **6** | `MAX_CONCURRENT` | — |

Override `NULL` = use the env default (read on every request, so `.env` edits apply without a
restart); `0` = nothing allowed for that limit. Overrides are set with `--create-user … --daily-quota
N` etc. for new accounts, and with SQL for existing accounts (README) — there is no admin API in
this round. Why a video sub-quota: video is billed per second and slow; a separate small cap was
trivial to add (one extra row kind), so it is included rather than skipped. Text and image have no
separate sub-quotas (they share the daily total).

### Semantics
- **Per-minute window: sliding 60 s**, implemented as a per-account log of admission timestamps;
  a request is refused when ≥ N admissions happened in the last 60 s. `Retry-After` = seconds
  until the oldest one leaves the window.
- **Day boundary: calendar day in Asia/Tokyo (UTC+9, no DST). The quota resets at 00:00 JST.**
  The service computes the JST date (`TimeZoneInfo` `Asia/Tokyo`, fixed +09:00 fallback if tzdata
  were missing) and passes it to SQL as a `date` parameter, so neither server nor DB time zone
  matters (`LimitService.QuotaDay`).
- **Concurrency** = generation calls currently being proxied. For video that is the create call
  only — the job then runs at Agnes and is polled; running video jobs are limited by the video
  daily quota, not by the concurrency cap. Concurrency 429s carry `Retry-After: 5` (a heuristic:
  the real wait depends on the other call's duration).
- **Check order** (first failing check answers): daily total → video sub-quota → per-minute →
  per-account concurrency → global concurrency. Daily goes first because its wait is longest.
- **Accounting: counted at admission.** After auth and body validation (a 400 for a bad body costs
  nothing), the request is admitted and the daily counter incremented before calling Agnes. The
  only refund: **Agnes answered 4xx** (it rejected the request, so nothing was spent upstream) →
  the daily unit is given back. A result, an Agnes 5xx, a timeout, an unreachable upstream, or a
  missing `AGNES_API_KEY` (503) all count. Requests refused by the limiter itself never count.
  Per-minute timestamps are not refunded (the rate limits attempts).
- **Race safety**: admission per account runs under an in-process async lock *and* inside a
  PostgreSQL transaction that row-locks the user (`SELECT … FOR UPDATE`) before reading and
  upserting `usage_daily`. Verified: 12 parallel requests against `daily_quota=3` → exactly 3
  admitted, counter = 3.
- **Persistence**: daily counters live in **`agnes_auth.usage_daily (user_id, day, kind, count)`**
  and survive restarts (verified). Per-minute windows and in-flight counters are **in memory**
  (single-instance service): a restart clears them, which at worst allows one extra minute's
  burst. Running two instances would multiply the per-minute/concurrency caps (the daily quota
  stays exact because it is in PostgreSQL).
- **Fail closed**: if the limits tables are missing or the DB is down, generation returns 503
  (`limits_unavailable` / `db_unavailable`) rather than running unmetered.

### 429 body
```json
{"error":"daily_quota_exceeded", "code":"daily_quota_exceeded",
 "message":"Daily generation quota exhausted (200 per day). Resets at 2026-11-19T00:00:00+09:00 (00:00 Asia/Tokyo).",
 "scope":"total", "limit":200, "used":200, "remaining":0,
 "reset_at":"2026-11-19T00:00:00+09:00", "reset_at_unix":1795014000, "retry_after":null,
 "reset_in_seconds":35259, "timezone":"Asia/Tokyo"}
```
- `error` = `code` ∈ `rate_limited` | `daily_quota_exceeded` | `concurrency_limit`; `message` is the
  English explanation (the frontend builds a Chinese one from the fields).
- `scope`: `total` / `video` (daily), `user` (rate, concurrency), `global` (global concurrency).
- `rate_limited` adds `window_seconds:60`, `retry_after` and the `Retry-After` header;
  `concurrency_limit` has `retry_after:5` + `Retry-After: 5`; `daily_quota_exceeded` has **no**
  `Retry-After` header — the reset moment is in the body (`reset_at`, JST).
- All `reset_at` values are ISO-8601 in +09:00.

### Visibility
`GET /api/limits` (and the `limits`/`usage` part of `/api/auth/me`):
```json
{"limits":{"per_minute":10,"per_day":200,"video_per_day":20,"concurrent":2,"global_concurrent":6,
           "overridden":["daily_quota"]},
 "usage":{"day":"2026-11-18","timezone":"Asia/Tokyo","reset_at":"2026-11-19T00:00:00+09:00",
          "reset_at_unix":1795014000,
          "used":{"text":1,"image":0,"video":0,"total":1},
          "remaining":{"total":199,"video":20},
          "last_minute":{"used":1,"remaining":9},"in_flight":0},
 "counted_endpoints":["POST /api/text","POST /api/image","POST /api/video"]}
```
Frontend: after login the header shows `今日剩余 199/200 次（视频 20/20）` (tooltip: per-minute and
concurrency limits, reset time in JST), refreshed after every generation; a 429 with one of the
three codes is shown as a specific Chinese message (which limit, remaining, reset time / retry
seconds) instead of the generic error.

## Video research — confirmed vs assumed

Researched 2026-11-18 from the official docs (Mintlify; raw markdown snapshots saved in
`research/`): `https://wiki.agnes-ai.com/en/docs/agnes-video-25.md` and
`…/agnes-video-25-flash.md`, linked from the quickstart page's navigation.

**Confirmed from official docs (not yet from a live call):**
- Async API. Create: `POST https://apihub.agnes-ai.com/v1/videos` with
  `{model, prompt, mode, seconds:"4"–"12" (string), size, aspect_ratio, seed?, n?}` plus
  mode-specific `first_frame/last_frame` (keyframe) or `images/audios/videos` (reference).
- Create response: `{id, task_id, video_id, object:"video", model, status:"queued", progress, created_at, seconds, size}`.
- Retrieve: `GET https://apihub.agnes-ai.com/agnesapi?video_id=<VIDEO_ID>&model_name=<model>`
  (note: **not** under `/v1`, and **not** OpenAI's `/v1/videos/{id}`). `model_name` is required
  for keyframe/reference modes; we always send it.
- Status values `queued | in_progress | completed | failed`; result video at **top-level `url`**
  when completed; `error.message` on failure. Docs suggest polling every 1–2 s.
- `agnes-video-2.5-flash`: size must be `720P`, ≤5 images, no reference videos.
- Prices: 2.5 list $0.025–0.055/s; flash $0.025/s list (docs say $0 "Angel User Trial").

**Assumed / unverified until the key is available:**
- That live behaviour matches the docs (field names, `video_id` vs `id`; we accept either).
- Typical generation time (we assume 1–6 min, per the task's 60–360 s estimate).
- How long the returned `url` stays valid (docs example shows `expires_at:null`). We don't
  re-host videos; the user should download if they need to keep them.
- The image API's top-level `ratio` field is from the Image 2.5 docs, not yet live-tested
  (the operator tested `size:"1K"` without ratio).

## Timeouts

- Text: 300 s upstream timeout (sync). Image: 360 s upstream timeout (sync) — docs recommend
  60–360 s; typical observed 13.6 s, so a synchronous request is simplest and fine.
- Video: the **upstream itself is async**, so our API is async too: `POST /api/video` returns
  the job immediately (120 s create timeout), the browser polls `/api/video/status/<id>` every
  3 s (30 s per poll), giving up after 20 min. This avoids holding an HTTP connection open for
  minutes (which proxies/mobile networks often kill) and survives page-side hiccups (5
  consecutive poll errors tolerated). We poll every 3 s rather than 1–2 s to stay clear of 429s.
  The frontend refreshes its access token 1 min before expiry (and on any 401), so a long video
  poll outlives the 30 min access token.
- **Change in rev 3:** the .NET client applies these as **total** time per upstream call
  (`CancellationTokenSource.CancelAfter`), whereas Python's urllib timeouts were per socket
  operation. Agnes answers non-streaming, so for these endpoints the two are effectively the same
  (the response arrives in one go after generation). A client disconnect cancels the upstream call.
- DB: 5 s connect timeout (3 s for `/health`, whose DB probe is cached 30 s).
- Caveat: if the public URL goes through a reverse proxy with an idle timeout < ~6 min, a very
  slow *image* request could be cut by the proxy. If that shows up, move image to job+poll.

## Security

- **Agnes key**: env `AGNES_API_KEY` (systemd `EnvironmentFile=-…/.env`), falling back to parsing
  `./.env` (re-read on mtime change). Never hardcoded, never logged, never sent to the browser;
  upstream error snippets have the key (and `JWT_SECRET`) redacted. `.env` is `chmod 600`; only
  `.env.example` (placeholders) ships. `install.sh` runs `AgnesService.dll --init-env`, which
  creates `.env`, sets 600, and generates `JWT_SECRET` inside the process without printing it.
- **Public page / quota risk**: anyone who reaches the URL could otherwise spend the user's Agnes
  quota (video is billed per second). Now every generation call needs a valid JWT of an active
  account in `agnes_auth.users`, accounts are only created locally by the operator, and
  brute-force is limited by account lockout + IP throttle/ban. Since rev 4 an authorised user (or
  a stolen session) is capped by the per-account limits (10/min, 200/day, 20 videos/day, 2 in
  flight by default). Remaining risk: those are request counts, not money — a 12 s 1080P video
  and a 4 s 720P one cost the same quota unit.
- **Passwords**: PBKDF2-SHA512, 210 000 iterations, never stored/logged in plaintext; login
  bodies are never logged. Login responses don't distinguish unknown user vs wrong password.
- **Proxy / client IP**: by default the TCP peer address is used. Behind your own reverse proxy
  set `TRUST_PROXY=1` (right-most `X-Forwarded-For` entry), otherwise lockout/IP-ban logic sees
  the proxy's IP for everyone (loopback is never banned; the 429 throttle would still apply).
- **Browser**: model output rendered via `textContent`; `<img>/<video>` src only `http(s)` URLs
  (or our own b64 data URI); CSP, `X-Frame-Options: DENY`, `nosniff`, `no-referrer`.
- Inputs validated (lengths, enum values, URL schemes); request bodies capped.
- Plain HTTP on 8793; TLS is expected from whatever publishes the page (set `COOKIE_SECURE=1`
  or forward `X-Forwarded-Proto: https` so the cookie gets `Secure`).


## Admin backend (TASK5, added 2026-11-18; section appended by operator after Claude hit its usage limit mid-report)

Admin API under /api/admin/ requires a JWT with role=admin (non-admin 403, anonymous 401), modelled on dbapi user management:
- GET /api/admin/users — list with role, is_active, created_at, last_login_at, locked_until, limit overrides, effective limits, today usage
- POST /api/admin/users {username, password, role, limits?} -> 201; structured errors invalid_username / invalid_role / weak_password
- PATCH /api/admin/users/{id} {role?, is_active?, limits?} — absent field = unchanged, number = set override, explicit null = clear override; empty patch -> 400 no_changes; changing own role/status -> 400 cannot_modify_self
- POST /api/admin/users/{id}/reset-password {newPassword} — bumps token_version (sessions die) and unlocks; self is rejected (use POST /api/auth/change-password with current password)
- DELETE /api/admin/users/{id} — self -> 400 cannot_modify_self; the last remaining admin cannot be deleted/demoted/disabled
- GET /api/admin/ip-blocks, DELETE /api/admin/ip-blocks/{ip}; GET /api/admin/audit?limit=N (table agnes_auth.admin_audit, one row per admin mutation, no secrets)
Frontend: a 管理 tab is shown only when /api/auth/me reports role=admin; users table + create form + per-row enable/disable, edit limits, reset password dialog (confirm + show/hide, disabled for own row), delete with confirm. Verified by operator 2026-11-18: 403/401 guards, PATCH set/clear limits, no_changes, cannot_modify_self.

## TASK6 第二期：朗读入库编排与网页保存通道（2026-11-19）

定稿 `TASK6-PHASE2-PLAN.md` §1–§2，网页通道按 `PHASE2-AUTH-DESIGN.md`（R 案）实施；本节只记设计要点，细节与测试见 `PHASE2-IMPL-REPORT.md`。

### 作业模型
- 异步：`POST /api/reading/ingest` 同步做「鉴权（准入前）→ Admit（立即释放并发 Ticket）→ 作业行落库（含原文）→ 同步 /share（上限 8 s）」，然后 202；补译由独立 worker 池（`INGEST_WORKERS=2`）做 preview → enrich（`enrich-v2`，每块带 `context.full_text`）→ fixes 本地分流 → replace commit。
- 持久化：`agnes_auth.ingest_jobs`；启动时 running → retry；BackgroundService 每 `INGEST_SCAN_S` 扫 `next_retry_at`，每 `INGEST_RECONCILE_S` 对 L2 待办做自动对账。「同文只跑一份」= `UNIQUE (share_id) WHERE status IN ('queued','running','retry')`。
- 记账：call_log 在 POST 写一行 `accepted`，结束时 UPDATE（model=`provider:model`，detail 含 trail、`fx=a/r/s`、retry 计数）；系统重试与熔断试探不计额度。

### 网页保存通道（R 案）
```
浏览器（8793 同源：管理页「添加文章」页签 或 /add 别名）
  └─ Authorization: Bearer <自己账号的 access JWT，内存>   {title, text, lang}
agnes-service：JwtBearer → RequireUser（10 s 缓存，含 reading_ingest）→ 'web'？否则 403 ingest_forbidden
  → 入参收窄（只收 title/text/lang）→ Admit(kind=ingest) → 作业行 → POST 127.0.0.1:8791/share
     Authorization: Bearer $TTSWEB_TOKEN  {text,title,lang,by:"web",added_via:"web"}
  → 202 {job_id, share_id, url:<TTSWEB_PUBLIC_URL>/v3?s=<id>}；页面轮询 GET /api/reading/jobs/{id}
```
- 机器令牌只在服务端之间流转：`TTSWEB_TOKEN` 不出现在任何响应里，不开 CORS，CSRF 模型不变（业务端点只认 Bearer；Cookie 端点要求 JSON Content-Type + SameSite=Strict）。
- 归属：reading 库 `added_by=web`、`added_via=web`（只记通道，不做跨体系映射）；谁点的保存记在 agnes 侧（`ingest_jobs.user_id/username`、call_log `ch=web;by=web`）。
- 单账号单会话：「添加文章」就是主站的页签，没有第二个身份，所以审核担心的「/add 与主站 refresh Cookie 互顶」不存在。两个标签页恰好同时刷新时，宽限内的「已轮换」返回 `refresh_rotated` 且不清 Cookie，前端稍等重刷（设计 §7 第 9 条）。
- 回显全部 `textContent` / `el()`；草稿存 localStorage（正文不算机密），成功后清除，401 重新登录时不清空表单。
- 已删除：`X-Ingest-Key` 旁路（`INGEST_WEB_KEY` / `INGEST_WEB_USER` / `AuthService.ServiceUser`）。

### 已知限制
- **P2R-19**：公网网关从回环转发，来访 IP 一律 127.0.0.1，IP 维度的节流/封禁对公网失效，他人可用错口令把账号锁 15 分钟（可用性问题，不泄露、不越权；与现有管理页相同）。若核实网关传 `X-Forwarded-For`，可开 `TRUST_PROXY=1` 缓解。
- 8793 是保存的单点（播放不受影响，agent 通道不受影响）。
- 熔断状态只在内存，重启后闭合（最坏多试一次）。
