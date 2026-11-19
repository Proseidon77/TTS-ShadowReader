# TASK6 第二期实施报告（续跑）

日期：2026-11-19 晚（续跑 23:03 起）
依据：`TASK6-PHASE2-PLAN.md`（2026-11-19 晚回写版定稿）＋ `PHASE2-AUTH-DESIGN.md` §7（网页保存认证设计，已批准）＋ `PHASE2-REVIEW.md`（P2R 编号）。首跑任务书 `PHASE2-IMPL-PROMPT.md` 仅作对照（其 B.1 已作废）。
范围：agnes-service 开发实例 8795（隔离库 `agnes_p2dev`）＋ MockAgnes（含假 TtsWeb）＋ TtsWeb 开发副本 8796（`tts-redesign/impl/phase2`，同一隔离库）。
**未 publish、未重启 8793 / 8791、未改任何生产配置与生产数据**（对生产库只做了只读 SELECT，见 §1.3）。

---

## 1. 盘点与复核结论

### 1.1 中断点盘点

首跑（18:44–19:14）已经完成了 agnes-service 侧 §2 的绝大部分并通过了自己的验收（中断前日志：自检 78/78、p2 验收 90/90、第一期 Mock 验收 84/84）。TtsWeb 侧首跑**不是未动**：它在工作副本 `tts-redesign/impl/phase2/` 里按旧口径做了契约项＋已作废的网页通道（见下）。生产目录 `/data/workspace/tts-web/` 未被改动（所有文件时间戳早于 18:44，续跑结束时复核仍为空差异）。中断时首跑起的开发实例 8795 与 Mock（19:22/19:23 启动）仍在运行，续跑接着用 pid 文件接管并重启。

**保留（核对与现行定稿一致，未重写）**

| 项 | 文件 |
|---|---|
| 作业表 `agnes_auth.ingest_jobs`（幂等迁移、部分唯一索引 `ingest_jobs_share_active_uq`）、`users.reading_ingest` / `muse_id`、两处 kind CHECK 加 `ingest` | `auth_schema.sql` |
| 编排端点、作业查询/列表、L2 清单＋自动对账＋可选回填、worker 池＋扫描器、启动恢复、准入语义（Admit 后释放 Ticket、accepted 行＋结束 UPDATE、系统重试不计额度）、T 态退避、延迟重试整篇重做、share_id 锁 | `src/IngestService.cs` |
| TtsWeb 客户端（/share、preview、commit replace、articles 回读、readings_check） | `src/TtsWebClient.cs` |
| fixes 本地分流（补齐类采纳、改写类默认不采纳、量词音变放行、词典佐证、`fx=a/r/s`） | `src/FixTriage.cs` |
| 熔断（429 读 retryDelay/Retry-After 否则 2 分钟、403 quota 60 分钟、鉴权 6 小时、E7 连续 3 篇、质量只统计） | `src/Circuit.cs` |
| 上游令牌桶（agnes 18/min、编排子配额 12/min，覆盖全部 Agnes 调用；交互请求快速 429 并退额） | `src/Upstream.cs` |
| enrich-v2＋`context.full_text`、ProviderDef 挂块参数/超时、换 provider 重新分块、`INGEST_DEADLINE_S`、`ANALYZE_DEADLINE_S` 可配置 | `src/Analyze.cs` |
| kind=ingest 全链（LimitService.Kinds/View、AdminApi 白名单、管理页下拉）、作业列表与 L2 待办面板 | `src/LimitService.cs`、`src/AdminApi.cs`、`index.html` |
| AdminApi / CLI 的 reading_ingest 与 muse_id 回显、修改、审计；建号拦截 `^A\d{3}$` | `src/AdminApi.cs`、`src/UserCli.cs` |
| Mock TtsWeb（/share、preview、commit、articles、readings_check；dup/409/503/401/rc503 模式；complete/delete 控制口） | `tests/MockAgnes/MockTtsWeb.cs` |
| TtsWeb 契约：`added_via`（/share、commit）、逐句 tr_via/tr_model、TR_VIA 放宽、readings_check、`/v3/t` 带 status、`/v3/articles` 纳入 partial、ingest.md 的 replace/L2/VERSION_CONFLICT 章节 | `tts-redesign/impl/phase2/{tts_web.py,store.py,ingest.py,ingest.md}` |
| reading 库迁移（added_via 列、tr_via CHECK 放宽，幂等） | `tts-redesign/impl/reading_p2_migration.sql`、`phase2/setup_devdb.py` |

**修正（与新定稿冲突，按新定稿改）**

| 项 | 改动 |
|---|---|
| `IngestApi.Caller`：`X-Ingest-Key` 分支 | 删除。JWT 用户按 `reading_ingest` 分流：agent（须有 muse_id）→ `(u,"agent",muse_id)`；web → `(u,"web","web")`；其他 403 `ingest_forbidden` |
| `Submit` 的 web 分支「只收 share_id」 | 改为只收 `title/text/lang`：其他字段 400 `unknown_field`；缺/空标题 400 `title_required`；缺正文 400 `invalid_text` |
| 202 与作业视图、L2 清单的 `url` | 新增 `TTSWEB_PUBLIC_URL`：配了拼 8791 绝对地址（去尾部 `/`），没配保持相对 `/v3?s=` |
| `/health.ingest.web_trigger` | 改为 `public_url_configured`（布尔） |
| 管理页「朗读入库权限」下拉的 web 文案「TtsWeb 服务账号」 | 改为「网页保存·添加文章」；`auth_schema.sql` 列注释同步 |
| TtsWeb 工作副本 `tts_web.py`：/share 的 web 特判（强制 by=web、web 必须带标题/校验 lang）、保存后经 localhost 触发编排 | 删除。/share 只做通用契约：带令牌时 `added_via` 取参数（缺省 agent），不带令牌一律 legacy；`by` 一律走 `resolve_added_by`（agnes-service 令牌 can_act_for 含 web） |
| `make_p2_secrets.py`、`tokens_dev.json`、`config_dev.json`、`tests/ingest_ttswebdev.env` | agnes-service 令牌 `can_act_for` 加 `web`；不再生成 `INGEST_WEB_KEY`、不再有 `web-add` 令牌、config 去掉 `agnes_ingest`/`features` |
| `tests/p2_accept.py` C 段及 H 段的 web 用例 | 改写为 web 账号用 JWT 提交 text（详见 §3） |
| `ingest.md`（工作副本）L2 章节 | 网页通道入口改为 8793「添加文章」/`/add`；巡检改为 A001 每小时（08–24 JST）、用自己 VM 上的 agy；agnes-service 令牌可代填 web |
| `.env.example` 关于 GEMINI_API_KEY「无需重启」、README 同句 | 按定稿 §1.6（P2R-5）改为须重启 |

**删除**

- agnes-service：`IngestApi.WebUser`、`AuthService.ServiceUser`（无其他引用）、`INGEST_WEB_KEY`（脱敏名单与自检里的条目）、`tests/ingest_mock.env` 与 `tests/ingest_ttswebdev.env` 里的 `INGEST_WEB_KEY` / `INGEST_WEB_USER`。
- TtsWeb 工作副本：`add.html`、`add.js`、`edit_v3.py`、`edit_ttsweb.py`（首跑的旧口径补丁脚本）、`dev_secrets/web-add.txt`、`dev_secrets/ingest_web.key`；`tts_web.py` 中 `_trigger_ingest`、`_INGEST_CFG`、`_FEATURES`、`/add`、`/add.js` 路由、v3 页注入 meta 的逻辑。`player_v3.html` 恢复为与生产完全一致（`cmp` 相同）。

**补齐（首跑未写）**

- `.env.example` 首跑没补第二期任何键：现补全第二期段（含 `TTSWEB_URL/TOKEN/PUBLIC_URL`、全部 `INGEST_*`、`UPSTREAM_RPM_*`、`CIRCUIT_*`、`ANALYZE_DEADLINE_S`，并写明改后须重启）。
- README、DESIGN 首跑没写第二期：现补章节。
- 网页通道全部新增项（见 §2.3）。

### 1.2 复核结论：**通过，无 blocker**

定稿与认证设计和现行代码、数据现状一致，可按稿实施。非 blocker 小出入及处理：

1. **readings_check 佐证过宽**（实测发现）：首跑实现允许用 nbest 里多个节点拼出 token 的读音，结果「東京」在 N=20 的候选里出现「ひがしきょう」（東|京 拆开拼读），改写类错误会被误判为有词典佐证、从而被采纳，削弱 §2.3 防线。按定稿「与 token 边界对齐」的精神收紧为「只认单个节点恰好覆盖该 token 的路径」＋该词单独分词的读音（`phase2/ingest.py`）。未改接口与口径。
2. **`/api/auth/me` 加 `reading_ingest` 字段**（加性）：用于决定是否显示「添加文章」页签；纯外观，真正的判定仍在 `POST /api/reading/ingest`。以 `/add` 打开时页签总是显示（未开通的账号会看到 403 文案）。
3. 设计 §5.1 的 web 通道表里没写「缺正文」的错误码：沿用既有 `invalid_text`。
4. 定稿说网页通道「TtsWeb 侧不写任何代码」：TtsWeb 侧 /share 原本就要实现 §5 的 `added_via` 契约，续跑把它做成通道无关的通用逻辑（只认令牌；匿名 legacy），没有任何 web 特判。

### 1.3 对生产的只读核对（SELECT only）

- `db77database`：`reading.articles` 尚无 `added_via`；`sentences_tr_via_check` 仍为 6 值；`agnes_auth.ingest_jobs` 不存在；`users` 无 `reading_ingest/muse_id`；kind CHECK 无 `ingest` —— 即生产库未被首跑或续跑改动，上线时需执行 §4 的两份迁移。
- 生产账号：TestUser001（admin）、TestUser002、MuseAgent1、MuseAgent2、ReaderNew（只读了名字与角色）。
- 8793 `/health` 仍为第一期（无 `ingest` 段）；8791 `/health` 200。

---

## 2. 实现清单

### 2.1 agnes-service · 定稿 §2（首跑完成、续跑核对保留）

| 定稿条目 | 实现 | 文件 |
|---|---|---|
| §2.1 端点＋作业落库＋恢复＋扫描＋部分唯一索引 | `POST /api/reading/ingest`、`GET /api/reading/jobs/{id}`、`GET /api/reading/jobs`（admin 全部 / 其他按属主，`mine=1`、`channel`、`status`、`share_id`、`limit`）；`ingest_jobs` 幂等迁移；`RecoverRunning`；`IngestWorker`（BackgroundService，`INGEST_SCAN_S=60`） | `src/IngestService.cs`、`auth_schema.sql`、`Program.cs` |
| §2.1 准入语义 | Admit 同步、成功即释放 Ticket；`INGEST_WORKERS=2`；call_log accepted→UPDATE；重试/试探只记 detail | 同上、`src/CallLog.cs`、`src/LimitService.cs` |
| §2.1 端点鉴权 | **agent 映射选 `users.muse_id`**（带 `^A\d{3}$` CHECK 与部分唯一索引；不用 `INGEST_AGENT_MAP`：映射有审计、改动不需重启、与 `reading_ingest` 同进 UserState 缓存）。准入前 401/403，不扣额度；AdminApi/CLI 回显、修改、审计；建号拦截 `^A\d{3}$` | `src/AuthService.cs`、`src/AdminApi.cs`、`src/UserCli.cs` |
| §2.1 replace 前回读 | commit 前 `GET /api/v1/articles/{id}` 带回 title/lang/rate/current_version；空标题用正文前 20 字 | `src/IngestService.cs`、`src/TtsWebClient.cs` |
| §2.2 L1 降级 | enrich-v2＋`context.full_text`；ProviderDef 挂 ChunkSents/ChunkChars/CallTimeoutS（gemini 20/1500、agnes 8/600；lite 45 s、flash 90 s、agnes 120 s）；`INGEST_DEADLINE_S=420`；`ANALYZE_DEADLINE_S=600`（原 DeadlineSeconds 改可配置）、单次 180 s 不变；换 provider 重新分块；熔断；令牌桶；延迟重试 15m/1h/4h 整篇重做 → L2 | `src/Analyze.cs`、`src/Circuit.cs`、`src/Upstream.cs`、`src/IngestService.cs` |
| §2.3 fixes 本地分流 | 补齐类采纳；改写类默认不采纳，量词表音变放行或 readings_check 佐证放行；词典不可用时退回本地分流（记 suspect）；`fx=a/r/s` 入作业 result 与 call_log detail | `src/FixTriage.cs` |
| §2.4 kind=ingest | 两处 CHECK、Kinds/View、ListCalls 白名单、管理页 kind 下拉 | `auth_schema.sql`、`src/LimitService.cs`、`src/AdminApi.cs`、`index.html` |
| §2.5 L2 | `GET /api/reading/l2`（admin / agent 账号；拉取即对账）、`INGEST_RECONCILE_S=3600` 后台对账、`POST /api/reading/l2/{id}/report` 可选回填；原因 api-down/quota/quality/deadline；建议执行者 agy-gemini | `src/IngestService.cs` |
| §2.6 管理页 | 作业列表、L2 待办、kind 筛选含 ingest | `index.html` |

### 2.2 设计 §7 第 1–9 条（网页通道，续跑完成）

| # | 做了什么 | 文件 |
|---|---|---|
| 1 | `Caller` 删除 `X-Ingest-Key` 分支，JWT 按 `reading_ingest` 三分流；删 `WebUser`、`AuthService.ServiceUser`、无用 using | `src/IngestService.cs`、`src/AuthService.cs` |
| 2 | web 分支入参收窄：只收 title/text/lang；`share_id`/`rate`/其他 → 400 `unknown_field`；标题缺/空 → 400 `title_required`；正文缺 → 400 `invalid_text`；`added_by/added_via` 写死 web | `src/IngestService.cs` |
| 3 | `IngestApi.PublicUrl` / `ShortUrl()`：202、`IngestStore.View`、L2 清单的 `url` 统一走它；`.env.example` 加 `TTSWEB_PUBLIC_URL` 并注明改后须重启 | `src/IngestService.cs`、`.env.example` |
| 4 | `app.MapMethods("/add", GET/HEAD, ServeIndex)`（同 CSP、同响应头） | `Program.cs` |
| 5 | `index.html`「添加文章」页签：标题（必填，≤120）、语言（默认 ja）、原文（字数显示，≤5000）、保存、结果区（8791 绝对短链可点＋复制按钮；share_id 为 null 时提示「已收下，稍后自动入库」；状态行）、「最近保存」（`GET /api/reading/jobs?mine=1&channel=web&limit=10`）；pathname 为 `/add` 时登录后直开该页签；草稿存 localStorage（输入即存），成功后清除，401 走现有单飞刷新/登录框且不清空；每 4 秒轮询作业到终态/retry 为止；所有回显 `textContent`/`el()`；只有 http(s) 绝对地址才渲染成链接；错误文案含 `ingest_forbidden`、`title_required`；`TABS`/`selectTab`/`clearSession`（退出时清本账号列表与结果、保留草稿）同步改 | `index.html`、`Program.cs`（/me 加 reading_ingest） |
| 6 | 测试改写与补充（§3） | `tests/p2_accept.py`、`tests/ingest_mock.env`、`tests/p2_ttswebdev.py`（新） |
| 7 | `/health.ingest.public_url_configured` | `Program.cs` |
| 8 | README、DESIGN 补第二期章节（网页通道链路、P2R-19 已知限制、双标签刷新） | `README.md`、`DESIGN.md` |
| 9 | **可选小修已做**：`AuthService.Refresh` 对 10 秒宽限内的「已轮换」返回独立码 `refresh_rotated`；`/api/auth/refresh` 遇该码**不清 Cookie**（其他 401 照旧清）；前端遇该码等 1.5 秒再刷一次 | `src/AuthService.cs`、`Program.cs`、`index.html` |

### 2.3 TtsWeb 侧 · 定稿 §5（工作副本 `tts-redesign/impl/phase2/`，用 8796 开发实例验证）

| 项 | 状态 |
|---|---|
| 网页通道 | **一概不做**；首跑写的 /add、按钮、保存口令、触发编排已删除；`player_v3.html` 与生产逐字节相同 |
| `/share` 与 commit 接受 `added_via` | 做了（/share：带令牌取参数、缺省 agent，匿名一律 legacy；commit：新文章写入，replace 不改） |
| `/v3/t` 加 status、`/v3/articles` 纳入 partial | 做了（`/t/<id>` 旧接口保持原样不带 status） |
| readings_check | 做了（`POST /api/v1/ingest/readings_check`，read 权限，nbest N=20；续跑收紧为单节点对齐，见 §1.2-1） |
| DDL 迁移 | `impl/reading_p2_migration.sql`：只在 `agnes_p2dev` 上连跑两次验证（幂等）；生产 SQL 见 §4 |
| `ingest.py` TR_VIA | 已加 `gemini-api`、`agnes-api`，与 CHECK 同步 |
| tokens.json 的 agnes-service 令牌 | 只写进上线清单（§4）；生成脚本 `phase2/make_p2_secrets.py`（不打印明文、不改 tokens.json） |
| /ingest.md | replace 口径、L2（agy）章节、VERSION_CONFLICT 语义、readings_check、added_via，已按新口径修订 |
| grace_until 提前 | 只写进上线清单 |

### 2.4 Mock

首跑的 MockTtsWeb 已覆盖 `/share`、preview、commit、`/api/v1/articles/{id}`、readings_check 及 dup/409/503/401/rc503 模式，续跑核对后未改，破坏性与 T 态用例全部基于它。

---

## 3. 测试结果

全部在 8795（库 `agnes_p2dev`）＋ MockAgnes（:18793）＋ TtsWeb 开发副本（:8796）上跑；日志在 `.probe/`。

| 套件 | 结果 | 日志 |
|---|---|---|
| 构建（服务 + MockAgnes，Release） | 0 警告 0 错误 | — |
| `--self-test` | **78/78** | `.probe/p2_selftest_default.log` |
| 第二期 Mock 验收 `tests/p2_accept.py`（A–M 全量） | **112/112** | `.probe/p2_accept2_default.log` |
| 第一期 Mock 验收 `tests/task6_accept.py mock`（含 text / image / video 回归，phase1_compat 覆盖） | **84/84** | `.probe/p2_accept1_mock.log` |
| TtsWeb 开发副本真链路 `tests/p2_ttswebdev.py` | **14/14** | 终端输出（见下） |
| `auth_schema.sql` 在开发库连跑两次 | 两次 exit 0（幂等） | — |
| `reading_p2_migration.sql` 在开发库连跑两次 | 两次 ok（幂等） | — |
| `index.html` 内联脚本 `node --check` | 通过 | — |

新增/改写的网页通道用例（p2_accept.py，均通过）：
- A 段：无 JWT 带 `X-Ingest-Key` → 401（旁路已删）；web 账号带 `share_id` / `rate` → 400 `unknown_field`；缺标题、空白标题 → 400 `title_required`；缺正文 → 400 `invalid_text`；这些 400 不扣额度；`none` → 403 `ingest_forbidden`（原有）；`/health` 有 `public_url_configured`、无 `web_trigger`；`GET /add` 与 `/` 内容和 CSP 完全相同且含页签；页面源码无令牌；预检/跨源请求不带任何 `Access-Control-Allow-*`；`/api/auth/me` 返回 reading_ingest。
- C 段：web JWT 提交 → 202 + share_id + 8791 绝对 url（尾部 `/` 去掉）、7 ms 返回；作业 done/ready、channel=web、username=该账号；reading 文章 `added_by=web`、`added_via=web`；一次准入；call_log `ch=web;by=web`；「最近保存」列表；活动中重复提交同文 → 200 duplicate、同一作业、退额；TtsWeb 503 时 202 + `share_id=null`、worker 退避后补上并完成；agent 行为不变（share_id+rate 仍可、相对 url、added_by 不变）。
- H 段：web 通道 L1 失败 → 3 次延迟重试 → L2，系统重试不计额度、只一行 call_log（改用 JWT 提交）。
- M 段（新）：双标签同一会话同时刷新 → 第二个 401 `refresh_rotated` 且不带 Set-Cookie；用当前 Cookie 重试 200；无效 Cookie 仍 401 并清 Cookie。

TtsWeb 开发副本真链路（p2_ttswebdev.py，14/14）：匿名 /share（宽限已过）401；`added_via` 非法 400；A001 令牌不能代填 web；带令牌缺省 agent；`/v3/articles` 列出 partial 并带 status；readings_check 支持「東京=とうきょう」、不支持「ひがしきょう」；`none` 403；web 保存 202（真实 /share）后库里立即 `web/web/partial`；`/v3/t` 带 status；作业完成后 replace 出 v2、`ready`、added_by/added_via 不变；句子 tr_via=`gemini-api`（CHECK 放宽生效）；停掉 8796 时保存 202 + share_id=null，8796 恢复后作业补上短链并完成（`web/web/ready`）。

**未做的验证（如实）**：没能用真实浏览器点一遍「添加文章」页签——本会话的桌面/浏览器工具（computer-use）未获授权，node 的浏览器自动化模块也无法调用。前端只做了 `node --check` 语法检查和人工代码审读，页签的交互（草稿、轮询、/add 直开、双标签不登出）需在生产冒烟时用手机/桌面浏览器确认（§4 第 9 步已列）。

---

## 4. 上线清单（operator 按顺序执行）

> 两边都会读写 PG，建议在低峰执行；全程大约 10 分钟。令牌明文只在 0600 文件里流转，不要贴到任何聊天或文档里。

**第 0 步 · 备份**
```bash
cd /data/workspace/agnes-service && tar -cf publish.bak-20261119-task6p2.tar publish
cd /data/workspace/tts-web && mkdir -p ../tts-web.bak-20261119-p2 && cp tts_web.py store.py ingest.py ingest.md tokens.json config.json player_v3.html ../tts-web.bak-20261119-p2/
pg_dump -d db77database -n reading --schema-only > /data/workspace/tts-redesign/impl/reading_schema.bak-20261119-p2.sql
```

**第 1 步 · reading 库 DDL（先于 TtsWeb 新代码）**
```bash
psql -d db77database -v ON_ERROR_STOP=1 -f /data/workspace/tts-redesign/impl/reading_p2_migration.sql
```
（幂等；加 `articles.added_via`（存量回填 legacy、NOT NULL、默认 legacy、CHECK agent/web/legacy）、放宽 `sentences_tr_via_check` 加 `gemini-api`/`agnes-api`。只有 77B db77database 一处需要执行。执行用户须对 reading 表有 ALTER 权限——与第一期建表同一用户。）

**第 2 步 · agnes_auth 迁移**
```bash
cd /data/workspace/agnes-service && psql -d db77database -v ON_ERROR_STOP=1 -f auth_schema.sql
```
（幂等：users.reading_ingest/muse_id、kind CHECK 加 ingest、ingest_jobs 及索引。）

**第 3 步 · 签发 agnes-service 的 TtsWeb 令牌**
```bash
python3 /data/workspace/tts-redesign/impl/phase2/make_p2_secrets.py --tokens-dir /data/workspace/tts-web/tokens
```
- 生成 `tts-web/tokens/agnes-service.txt`（0600，明文，已存在则沿用），并打印一段只含 sha256 的 JSON。
- 把打印的 `"agnes-service": {"sha256": …, "scope": "write", "can_act_for": ["A001","A002","A003","web"]}` 并入 `tts-web/tokens.json`（不新增 `web` 条目）。
- 把 `tokens/agnes-service.txt` 的值写进 agnes-service `.env` 的 `TTSWEB_TOKEN=`（不要 echo 到终端）。

**第 4 步 · TtsWeb 代码与配置**
```bash
cd /data/workspace/tts-redesign/impl/phase2
cp tts_web.py store.py ingest.py ingest.md /data/workspace/tts-web/
```
- `player_v3.html`、`auth.py` **不复制**（与生产相同，播放页 diff 必须为空）。
- `tts-web/config.json`：`grace_until` 改为上线当天（P2R-18；前提是已确认 A001/A003 都已带令牌调用 /share）。不要加 `agnes_ingest`、`features` 段（已作废）。

**第 5 步 · agnes-service 配置（`.env`，0600）**
- `TTSWEB_URL=http://127.0.0.1:8791`
- `TTSWEB_TOKEN=<第 3 步的值>`
- `TTSWEB_PUBLIC_URL=<8791 的公网根地址，无尾部 />`（例如 `https://8791-<机器 id>.instabox.tech`）
- 其余第二期键用默认值即可（见 `.env.example`）；生产 `.env` 本来就没有 `INGEST_WEB_KEY` / `INGEST_WEB_USER`，无需删除。

**第 6 步 · 发布 agnes-service（先停后覆盖）**
```bash
cd /data/workspace/agnes-service
sudo systemctl stop agnes-service
/data/dotnet-sdk/dotnet publish -c Release -o publish
/data/dotnet-sdk/dotnet publish/AgnesService.dll --self-test      # 期望 78/78
sudo systemctl start agnes-service
```

**第 7 步 · 重启 TtsWeb（8791）**：按其现行方式重启（令牌与 config 启动时加载）。重启后 `curl -s 127.0.0.1:8791/health`。

**第 8 步 · 开通账号（均有审计；也可在管理页「编辑」里操作）**
```bash
cd /data/workspace/agnes-service
D=/data/dotnet-sdk/dotnet; E="--env-file .env"
$D publish/AgnesService.dll $E --set-reading-ingest MuseAgent1 agent --muse-id A001
$D publish/AgnesService.dll $E --set-reading-ingest MuseAgent2 agent --muse-id A003
$D publish/AgnesService.dll $E --set-reading-ingest <用户本人日常账号> web
```
用户本人日常账号请 operator 确认（生产现有账号：TestUser001(admin)、TestUser002、MuseAgent1、MuseAgent2、ReaderNew；ReaderNew 不开通）。

**第 9 步 · 生产冒烟（设计 §7 第 13 条）**
1. `curl -s 127.0.0.1:8793/health`：`ingest.ttsweb_token=true`、`ingest.public_url_configured=true`。
2. 手机浏览器打开 `https://8793-<id>.instabox.tech/add`：自动恢复会话或密码管理器填充登录后直接是「添加文章」页签；粘贴一篇用户指定的真实文章保存，≤3 秒拿到 8791 绝对短链，点开能听原文；页签状态最终到「完成」（或按设计进入待重试/L2）。这篇即正式篇目，不删。
3. 用 `reading_ingest=none` 的账号保存 → 「此账号未开通保存文章」（403）。
4. `curl -si -X OPTIONS -H 'Origin: https://x.example' -H 'Access-Control-Request-Method: POST' 127.0.0.1:8793/api/reading/ingest | grep -i access-control` 应无输出。
5. 浏览器查看页面源码与网络面板，搜不到 TtsWeb 令牌。
6. `cmp /data/workspace/tts-web/player_v3.html /data/workspace/tts-web.bak-20261119-p2/player_v3.html` 无输出（播放页未改）。
7. 停掉 8791 再保存一篇短文 → 返回 202 且提示「已收下…」；恢复 8791 后作业自动补上短链（T 态首次退避 60 s）。
8. 一个标签开管理页、一个开 `/add`，各自操作 30 分钟以上（跨过一次自动续期），两边都不被登出。
9. `psql -d db77database -Atc "SELECT added_by, added_via, status FROM reading.articles ORDER BY added_at DESC LIMIT 1"` → `web|web|ready`（或 partial + 作业在 retry/L2）。
10. agent 通道：MuseAgent1 JWT 提交一篇（或 share_id）→ 202，完成后 `added_by=A001`、`added_via=agent`。

**第 10 步 · 建立 A001 巡检任务**：每小时一次，08:00–24:00 JST；`GET /api/reading/l2`（MuseAgent1 JWT）→ 用 A001 VM 本地的 agy 按 ingest.md 的 L2 章节处理；清单为空静默，只在处理了待办或失败时报告。每月 1 次 L2 演练。

**回滚**：`sudo systemctl stop agnes-service && rm -rf publish && tar -xf publish.bak-20261119-task6p2.tar && sudo systemctl start agnes-service`；TtsWeb 把备份目录里的四个文件与 tokens.json/config.json 拷回并重启。DDL 只扩不回退（新列/新表保留无害；旧 TtsWeb 代码不写 added_via 时取默认 legacy）。

---

## 5. 遗留与偏差

1. **前端未经真实浏览器验证**（§3 末）：需要在上线冒烟第 9 步 2、8 项确认。
2. **readings_check 收紧**（§1.2-1）：只认单节点对齐，比首跑实现严格；可能让少数合法的复合读音得不到佐证（结果是「不采纳、记 suspect」，偏安全）。建议跑一段后按 `fx=` 统计复盘。
3. **`/api/auth/me` 新增 `reading_ingest` 字段**（加性，§1.2-2）。
4. **登录锁定可被他人触发**（P2R-19，已接受、README/DESIGN 已写明）；如核实网关传 `X-Forwarded-For` 可开 `TRUST_PROXY=1`。
5. **上线初期抽查**（§2.3 最后一条：前 20 篇 L0 的每日汇总）属巡检者的报告内容，本服务提供数据（作业 result 内 fixes 明细、call_log `fx=`），汇总逻辑未在 agnes-service 内实现。
6. **P2R-23 存量 partial 并入 L2 清单**：定稿列为可选、不阻塞，未做（生产现有 8 篇 partial）。
7. **开发环境残留**：8795 开发实例与 MockAgnes（:18793）、TtsWeb 开发副本（:8796）仍在本机运行（只监听 127.0.0.1、只连隔离库 `agnes_p2dev`），可用 `python3 tests/p2_stack.py stop` 一并停掉。开发库 `agnes_p2dev` 的 reading schema 里留有测试文章（隔离库，不影响生产）。
8. 首跑留下的 `.probe/edit_*.py`、`engine_*.cs` 等一次性补丁脚本只是过程文件，不参与构建，可随时清理。
