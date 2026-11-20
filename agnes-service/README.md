# agnes-service

.NET 10 (ASP.NET Core) service wrapping the Agnes AI API for **text / image / video**
generation, with a one-page frontend, **JWT login** (users in PostgreSQL schema `agnes_auth`)
and **per-account API limits**. Port **8793**. Design: `DESIGN.md`. The earlier Python
implementation is archived in `python-legacy/` (reference only).

## Files

| Path | Purpose |
|---|---|
| `AgnesService.csproj`, `Program.cs`, `src/*.cs` | the service (see DESIGN.md for the file map) |
| `index.html` | frontend served at `/` (copied into `publish/`) |
| `auth_schema.sql` | idempotent DDL for `agnes_auth` incl. the limits migration |
| `.env.example` | config template (`.env` is created by `--init-env`, mode 600) |
| `agnes-service.service` | systemd unit (runs `publish/AgnesService.dll`) |
| `install.sh` | publish + `.env` + schema + unit + health check |
| `smoke.sh` | curl smoke test |
| `tests/MockAgnes`, `tests/mock.env`, `tests/run_dev.sh` | local fake Agnes API for testing limits/proxy without a key |
| `research/` | snapshots of the official Agnes docs |
| `python-legacy/` | archived Python implementation |

`DOTNET=/data/dotnet-sdk/dotnet` below.

## Setup (operator)

```bash
cd /data/workspace/agnes-service
bash install.sh          # = the five steps below; only step 4 needs sudo
```
or by hand:
```bash
# 1. build (packages Npgsql 9.0.3 + JwtBearer 10.0.0 restore from the local NuGet cache)
$DOTNET publish -c Release -o publish

# 2. config: creates .env from .env.example (mode 600), generates JWT_SECRET (not printed),
#    sets AGNES_DB_CONNECTION=Host=/var/run/postgresql;Database=db77database;Username=user
$DOTNET publish/AgnesService.dll --init-env

# 3. schema (idempotent; only touches agnes_auth.* — already applied on this machine)
psql -d db77database -v ON_ERROR_STOP=1 -f auth_schema.sql

# 4. systemd (sudo)
sudo install -m 644 agnes-service.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now agnes-service
curl -s localhost:8793/health

# 5. first account (prompts for the password twice, no echo; no default account exists)
$DOTNET publish/AgnesService.dll --create-user <username> admin
```
When the Agnes key arrives: add `AGNES_API_KEY=…` to `.env` — it is picked up without a restart
(the app re-reads `.env` when it changes; restart anyway if you prefer:
`sudo systemctl restart agnes-service`). Until then generation answers
`503 {"error":"AGNES_API_KEY not configured"}`; without `JWT_SECRET` or the DB, login answers a
503 explaining what is missing. `/health` shows all of it.

Optional least-privilege DB role instead of peer auth as `user` (a superuser):
```bash
sudo -u postgres psql -c "CREATE ROLE agnes_service LOGIN PASSWORD '<strong password>'"
psql -d db77database -v ON_ERROR_STOP=1 -f auth_schema.sql   # re-run: grants rights on agnes_auth
# .env: AGNES_DB_CONNECTION=Host=127.0.0.1;Database=db77database;Username=agnes_service;Password=<…>
```

## Run manually

```bash
$DOTNET publish/AgnesService.dll                     # 0.0.0.0:8793; HOST / PORT env to override
$DOTNET publish/AgnesService.dll --env-file other.env # alternative config file(s); later files win
journalctl -u agnes-service -f                        # logs (never contain key/passwords/tokens)
```

## User management

```bash
$DOTNET publish/AgnesService.dll --create-user alice              # role user, default limits
$DOTNET publish/AgnesService.dll --create-user bob admin --daily-quota 500 --rate-per-minute 20 \
                                 --video-daily-quota 50 --max-concurrent 3
$DOTNET publish/AgnesService.dll --reset-password alice           # new password, unlock, log out everywhere
$DOTNET publish/AgnesService.dll --disable alice                  # or --enable
$DOTNET publish/AgnesService.dll --unlock alice                   # clear 15-min lockout
$DOTNET publish/AgnesService.dll --logout-all alice               # revoke all tokens now
$DOTNET publish/AgnesService.dll --list-users                     # incl. limit overrides
$DOTNET publish/AgnesService.dll --unblock-ip 203.0.113.5
$DOTNET publish/AgnesService.dll --help-users
```
`--password-stdin` reads the password from stdin (scripted test accounts only).

## Per-account limits

Counted: `POST /api/text`, `/api/image`, `/api/video`. Not counted: video status polling,
`/api/models`, `/api/limits`, auth endpoints (login has its own lockout/throttle).

| Limit | Default | System default (env / .env) | Per-account column |
|---|---|---|---|
| requests per minute (sliding 60 s) | 10 | `LIMITS_DEFAULT_PER_MINUTE` | `rate_per_minute` |
| requests per day (JST day, resets 00:00 Asia/Tokyo) | 200 | `LIMITS_DEFAULT_PER_DAY` | `daily_quota` |
| videos per day (part of the daily total) | 20 | `LIMITS_DEFAULT_VIDEO_PER_DAY` | `video_daily_quota` |
| generations in flight, per account | 2 | `LIMITS_DEFAULT_CONCURRENT` | `max_concurrent` |
| generations in flight, all accounts | 6 | `MAX_CONCURRENT` | — |

Env defaults apply on the next request after editing `.env`. There is **no admin API in this
round**; change an existing account with SQL (`NULL` = back to the default, `0` = none allowed):
```sql
-- psql -d db77database
UPDATE agnes_auth.users SET daily_quota = 500, rate_per_minute = 20, updated_at = now()
 WHERE lower(username) = lower('alice');
UPDATE agnes_auth.users SET daily_quota = NULL, video_daily_quota = NULL, max_concurrent = NULL
 WHERE lower(username) = lower('alice');                           -- back to defaults
-- reset today's usage (JST day) for one account:
DELETE FROM agnes_auth.usage_daily
 WHERE user_id = (SELECT id FROM agnes_auth.users WHERE lower(username) = lower('alice'))
   AND day = (now() AT TIME ZONE 'Asia/Tokyo')::date;
-- usage overview for today:
SELECT u.username, d.kind, d.count FROM agnes_auth.usage_daily d JOIN agnes_auth.users u ON u.id = d.user_id
 WHERE d.day = (now() AT TIME ZONE 'Asia/Tokyo')::date ORDER BY 1, 2;
```
Over a limit → **429** with `{"error": "rate_limited" | "daily_quota_exceeded" | "concurrency_limit",
"limit", "used", "remaining", "reset_at", "retry_after", …}` (+ `Retry-After` header for the
per-minute and concurrency limits). Counting happens at admission; a request Agnes rejects with
4xx is refunded. Daily counters are in PostgreSQL (survive restarts); per-minute/concurrency
state is in memory. Details: DESIGN.md "Per-account limits".

## Smoke test

```bash
./smoke.sh                                             # public + 401 checks
SMOKE_USER=smoketest SMOKE_PASSWORD='…' ./smoke.sh      # + login, /me, /models, /limits, refresh rotation, logout
SMOKE_TEXT=1 SMOKE_USER=… SMOKE_PASSWORD=… ./smoke.sh   # + one real /api/text (costs quota)
```
Each authenticated run records one login; it never spends generation quota unless `SMOKE_TEXT=1`.

Testing limits without the Agnes key: `MOCK=1 tests/run_dev.sh` starts `tests/MockAgnes` on
127.0.0.1:18793 and runs the service against it (prompts `sleep:N …`, `reject`, `boom` make the
mock slow / 400 / 500).

## Endpoints

| Method | Path | Auth | Body / result |
|---|---|---|---|
| GET | `/` | – | frontend |
| GET | `/health` | – | `{status, key_configured, auth:{jwt_secret, db:{…}}, limits_default, …}` |
| POST | `/api/auth/login` | – | `{username, password}` → `{access_token, expires_in, user}` + cookie |
| POST | `/api/auth/refresh` | cookie | → new `{access_token, …}` + rotated cookie |
| POST | `/api/auth/logout` | cookie | → `{ok, revoked}`, cookie cleared |
| GET | `/api/auth/me` | JWT | `{user, expires_at, limits, usage}` |
| GET | `/api/limits` | JWT | `{limits, usage, counted_endpoints}` |
| GET | `/api/models` | JWT | `{source, models:{text,image,video}, defaults}` |
| POST | `/api/text` | JWT | `{prompt, model?, system?}` → `{text, reasoning?, usage?, model}` |
| POST | `/api/image` | JWT | `{prompt, model?, size?, ratio?, image?}` → `{url \| b64, model, size}` |
| POST | `/api/video` | JWT | `{prompt, model?, seconds?, size?, aspect_ratio?, first_frame?, …}` → `{id, status, progress, poll}` |
| GET | `/api/video/status/<id>?model=…` | JWT | `{id, status, progress, url (when completed), error}` |
| POST | `/api/analyze` | JWT | `{mode:"raw", text}` → `{toks (reading shape), tokens (analyzer shape), provider, model, attempts, trail}`; `{mode:"enrich", sentences:[{seq,text,toks}], gloss?}` → `{sentences:[{seq,tr,fixes:[{tok_i,s,reading}]}], chunks, warnings?}` (TASK6, see below) |

JWT = `Authorization: Bearer <access_token>` (30 min). Auth endpoints need
`Content-Type: application/json`.

```bash
TOKEN=$(curl -s localhost:8793/api/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"alice","password":"…"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')
curl -s localhost:8793/api/limits -H "Authorization: Bearer $TOKEN"
curl -s localhost:8793/api/text -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d '{"prompt":"你好"}'
curl -s localhost:8793/api/video -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" \
  -d '{"prompt":"a cat walking on the beach","seconds":"5"}'
curl -s "localhost:8793/api/video/status/<id>?model=agnes-video-2.5-flash" -H "Authorization: Bearer $TOKEN"
```


## Admin backend (TASK5, added 2026-11-18; section appended by operator after Claude hit its usage limit mid-report)

Admin API under /api/admin/ requires a JWT with role=admin (non-admin 403, anonymous 401), modelled on dbapi user management:
- GET /api/admin/users — list with role, is_active, created_at, last_login_at, locked_until, limit overrides, effective limits, today usage
- POST /api/admin/users {username, password, role, limits?} -> 201; structured errors invalid_username / invalid_role / weak_password
- PATCH /api/admin/users/{id} {role?, is_active?, limits?} — absent field = unchanged, number = set override, explicit null = clear override; empty patch -> 400 no_changes; changing own role/status -> 400 cannot_modify_self
- POST /api/admin/users/{id}/reset-password {newPassword} — bumps token_version (sessions die) and unlocks; self is rejected (use POST /api/auth/change-password with current password)
- DELETE /api/admin/users/{id} — self -> 400 cannot_modify_self; the last remaining admin cannot be deleted/demoted/disabled
- GET /api/admin/ip-blocks, DELETE /api/admin/ip-blocks/{ip}; GET /api/admin/audit?limit=N (table agnes_auth.admin_audit, one row per admin mutation, no secrets)
Frontend: a 管理 tab is shown only when /api/auth/me reports role=admin; users table + create form + per-row enable/disable, edit limits, reset password dialog (confirm + show/hide, disabled for own row), delete with confirm. Verified by operator 2026-11-18: 403/401 guards, PATCH set/clear limits, no_changes, cannot_modify_self.

## 调用记录（call log，2026-11-18 operator 追加）

新表 agnes_auth.call_log：每次到达限额层的生成调用（准入或 429 拒绝）记一行——账号、类型、模型、结果（success／upstream_rejected／rate_limited／daily_quota_exceeded／concurrency_limit 等）、状态码、上游状态码、耗时、提示词前 120 字摘要；准入前的参数 400 不记。写入是尽力而为（失败只记服务日志、不影响生成）。管理接口 GET /api/admin/calls?limit=&user_id=&kind=（仅 admin）；管理页签新增「调用记录」区，可按账号/类型筛。超过 90 天的行由服务机会性清理（每 6 小时至多一次）。/health 增 calls_ready。视频只记创建调用，轮询不记。改动文件：src/CallLog.cs（新）、Program.cs（Generate 记录）、src/AdminApi.cs、src/Db.cs、auth_schema.sql、index.html；备份 *.bak-20261118-calllog。已在 8794 + MockAgnes 验证（成功/超额 429/403 三路），生产已上线。

## 日语解析 /api/analyze（TASK6 第一期，2026-11-19）

- 代码：`src/Analyze.cs`（provider 注册表 + 引擎 + 提示词 analyze-v1/enrich-v1 + 校验 + ruby 推导 + `--self-test`）、`src/Upstream.cs`（通用上游调用 + 敏感键脱敏）。kind=`analyze`：一次请求只准入 1 次，计入每日总量/每分钟/并发，无独立子配额；上游全部 4xx 才退额。
- provider：agnes（agnes-3.0-flash → agnes-2.5-flash，json_object）、gemini（gemini-flash-lite-latest[minimal] → gemini-flash-latest[low]，json_object + reasoning_effort）。顺序：请求 provider/model > `ANALYZE_DEFAULT_PROVIDER` > `ANALYZE_PROVIDER_ORDER`，先同 provider 换模型再换 provider。ready = Key 非空且不以 # 开头 + URL 可解析；`/health.analyze` 只给 ready 与模型名。
- 失败分类：参数类 4xx → 不兜底（502 upstream_rejected）；401 → 跳过该 provider；403/429/5xx/超时/网络/响应形状错 → 下一个模型；JSON/校验失败 → 同模型修复重试 1 次；enrich `finish_reason=length` → 对半拆块。上限：raw 整单 4 次；enrich 每块 3 次、整单 min(30, 2×块数+4) 次；整单 600 s 墙钟。
- 配置（.env，注释必须单独成行）：`GEMINI_API_KEY`（空=未就绪；填入后**须重启**，见定稿 §1.6 / P2R-5 更正）、`GEMINI_API_URL`（空=Google 兼容端点）、`ANALYZE_DEFAULT_PROVIDER`、`ANALYZE_PROVIDER_ORDER`、`ANALYZE_TIMEOUT_S`（单次调用）。注意：systemd 用 EnvironmentFile 注入 .env，启动时非空的键改了要重启（评审 A-4）。
- 测试：`dotnet bin/Release/net10.0/AgnesService.dll --self-test`；开发实例 8795（`tests/run_test_8795.sh`，`MOCK=1` 叠加 `tests/mock8795.env`）+ `tests/MockAgnes`（场景前缀 `scenario:…`，`/gemini/chat/completions` 路由）；验收脚本 `tests/task6_accept.py mock|timeout|real|real1|prod`。详见 PHASE1-REPORT.md。

## 朗读入库编排 + 网页「添加文章」（TASK6 第二期，2026-11-19）

依据：`TASK6-PHASE2-PLAN.md`（定稿）＋ `PHASE2-AUTH-DESIGN.md`（网页保存认证设计 R 案）；实施报告 `PHASE2-IMPL-REPORT.md`。

- 代码：`src/IngestService.cs`（端点、作业表存取、worker 池 + 扫描器、L2 清单与自动对账）、`src/TtsWebClient.cs`（经 127.0.0.1 调 TtsWeb，带 `TTSWEB_TOKEN`）、`src/FixTriage.cs`（fixes 本地分流 + 词典佐证）、`src/Circuit.cs`（熔断）、`src/Upstream.cs`（上游令牌桶）。
- 端点：`POST /api/reading/ingest`（202 + job_id；同一篇已有进行中作业 → 200 duplicate）、`GET /api/reading/jobs/{id}`、`GET /api/reading/jobs?mine=1&channel=&status=&limit=`、`GET /api/reading/l2`（admin / agent 账号；拉取即对账）、`POST /api/reading/l2/{id}/report`（可选回填）。kind=`ingest`，一篇准入一次，系统重试不计额度。
- 两条通道（账号列 `agnes_auth.users.reading_ingest` = none / web / agent，准入前判定，401/403 不扣额度）：
  - **agent**：MuseAgent JWT，`muse_id`（A001/A003）→ added_by；可交 `text` 或已 share 的 `share_id`，可带 `rate`。
  - **web**：用户本人的 8793 账号（`reading_ingest=web`），在管理页「添加文章」页签或收藏的 **`/add`**（同一个 index.html，登录后直开该页签）提交 `{title(必填), text(≤5000), lang}`；带 `share_id`/`rate` → 400 `unknown_field`，缺标题 → 400 `title_required`。agnes-service 用自己的 `TTSWEB_TOKEN` 代调 TtsWeb `/share`（`by=web, added_via=web`），202 返回 `<TTSWEB_PUBLIC_URL>/v3?s=<id>`；TtsWeb 暂不可用时 202 且 `share_id=null`，worker 按 T 态退避最长 24 h 补上。浏览器只与 8793 同源交互，拿不到 TtsWeb 令牌；不开 CORS。草稿存浏览器 localStorage，保存成功后清除。
  - 旧的 `X-Ingest-Key` 旁路（`INGEST_WEB_KEY` / `INGEST_WEB_USER`）已删除。
- 开通账号：管理页「编辑」里的「朗读入库权限」下拉，或 CLI `AgnesService --env-file .env --set-reading-ingest <用户名> web|agent|none [--muse-id A001]`（均有审计）。`^A\d{3}$` 用户名保留给 Muse agent，建号会被拒。
- 配置（全部见 `.env.example` 第二期段；**任何键改后都要重启**）：`TTSWEB_URL`、`TTSWEB_TOKEN`、`TTSWEB_PUBLIC_URL`、`INGEST_*`、`UPSTREAM_RPM_*`、`CIRCUIT_*`、`ANALYZE_DEADLINE_S`。`/health.ingest` 给 workers / deadline_s / ttsweb_token / public_url_configured。
- 已知限制（P2R-19）：公网网关从回环转发，来访 IP 一律 127.0.0.1，他人连输错口令可把账号锁 15 分钟或触发全站登录节流（可用性问题，不泄露口令、不越权）；页面会显示剩余锁定时间。
- 两个标签页（管理页 + `/add`）同时刷新会话时，`/api/auth/refresh` 对「10 秒宽限内的已轮换」返回 401 `refresh_rotated` 且**不清 Cookie**，页面 1.5 s 后再刷一次，互不登出。
- 测试：`python3 tests/p2_stack.py build|start mock8795.env ingest_mock.env ingest_tune.env|selftest|accept2 [A..M]|accept1 mock`；对 TtsWeb 开发副本（`tts-redesign/impl/phase2`，:8796，库 agnes_p2dev）：`python3 tests/p2_stack.py ttsweb` 后叠加 `ingest_ttswebdev.env` 跑 `tests/p2_ttswebdev.py`。

## 文章管理：删除 + 编辑（2026-11-20）

实施报告 `DELETE-EDIT-REPORT.md`。代码 `src/ArticleService.cs`（端点）、`src/IngestService.cs`（`op=edit` 作业）、`src/TtsWebClient.cs`（List/Delete/Patch）；TtsWeb 侧 `DELETE` / `PATCH /api/v1/articles/{sid}`（写令牌）。

- 端点（同源 JWT，不开 CORS）：`GET /api/reading/articles`（全部登录用户，含 `can_manage`）、`GET /api/reading/articles/{sid}`（含原文，编辑预填）、`DELETE /api/reading/articles/{sid}`（软删）、`PATCH /api/reading/articles/{sid}` `{title?, lang?, text?}`：原文未变 → 200 `mode=meta`（TtsWeb PATCH，不出新版本）；原文变了 → 202 `mode=job`（`ingest_jobs.op='edit'`，同一 share_id 出新版本、旧版本保留，非日文按 D8 只存原文；计一次 ingest 额度）。
- 权限（接口强制，页面只照着显示按钮）：admin 全部；`reading_ingest=web` ↔ `added_by=web`；`reading_ingest=agent` ↔ `added_by=<自己的 muse_id>`；其余 403 `article_forbidden`。文章正在处理 → 409 `job_active`。删改都写 `admin_audit`（delete_article / update_article / edit_article）。
- 页面：「添加文章」页签下的「文章管理」区。
- 测试：`tests/de_ttswebdev.py`（TtsWeb 开发副本 `tts-redesign/impl/deledit`，:8796）、`tests/de_accept.py`（dev：8795+8796+Mock；`prod`：生产实测，一次性测试账号与测试文用完即删）。
