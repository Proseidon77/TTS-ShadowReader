# 日语朗读全流程架构说明（READING-ARCHITECTURE）

- 版本日期：2026-11-20（JST）
- 撰写依据：77B 现行生产代码（`/data/workspace/tts-web/`、`/data/workspace/agnes-service/`）＋ 工作目录 `refs/` 参考件；与旧文档冲突处一律以代码为准，差异集中记在第 10 节。
- 运行态核对（只读，2026-11-20 本次撰写时）：`GET 127.0.0.1:8793/health`、`GET 127.0.0.1:8791/health`、`ss -ltn`、PG 系统目录与聚合计数查询（`pg_constraint`、`information_schema.columns`、`count(*)`）。未改任何代码、配置与数据，未重启任何服务。
- 记号：**〔代码〕**＝有代码出处；**〔运行态〕**＝本次只读核对所见；**〔设计〕**＝只出现在设计文档、代码未体现；**〔未核实〕**＝本次无法从代码或只读核对确认，不作定论。
- 保密约定：本文不含任何密钥、令牌、口令、连接串真值；配置只写键名与用途。公网地址只写形态，不写机器 ID。

---

## 目录

1. 总览
2. 组件逐个说明
3. 端到端数据流
4. 补译分级与降级
5. 数据模型
6. 认证与权限
7. 限额与配额
8. 部署与运维
9. 文件索引
10. 与旧文档的出入
11. 代码观察到的风险与边角（附）

---

## 1. 总览

### 1.1 系统定位

一套「存日语文章 → 自动生成分句/分词/注音骨架 → 机器补中文译文与读音校对 → 网页边合成边朗读、逐句跟读」的个人学习系统。核心原则（代码与设计一致）：

- **存原文与补译解耦（share-first）**：任何入库先 `POST /share` 落一份 `status=partial` 的文章拿到短链，立刻可朗读；补译只是给同一短链出新版本，短链永不变（`store.py: Store.share / Store._replace`）。
- **服务端不调 LLM 的部分与调 LLM 的部分分开**：TtsWeb 只做规范化、分句、fugashi 骨架、校验、存取、TTS 合成（`ingest.md` 第 3 行「服务端不调用任何 LLM」）；译文与读音修正由「执行者」完成——自动路径是 agnes-service（调 Gemini / Agnes API），兜底路径是 agy（Gemini agent）。
- **播放时现合成，不存音频**：每次播放把整篇文本送 Edge TTS，经 ffmpeg 转 AAC 切 HLS（`tts_web.py: handle_play / _run_hls_session`）。

### 1.2 组件清单

| 组件 | 跑在哪 | 进程与端口 | 一句话职责 |
|---|---|---|---|
| TtsWeb（77B） | 77B | Python 3 aiohttp 单进程，`0.0.0.0:8791`〔代码 `config.json` host/port；运行态 8791 LISTEN〕 | 文章真源的存取门面（/share、/t、/v3/t、/api/v1/*）、分句与骨架、入库校验、Edge TTS→HLS 合成、播放页静态文件 |
| agnes-service | 77B | .NET 10 ASP.NET Core，`0.0.0.0:8793`〔代码 `Program.cs` PORT 默认 8793；运行态 LISTEN〕 | JWT 账号体系与限额、`/api/analyze`、**入库编排**（`/api/reading/ingest` 异步作业、L1 降级、fixes 核对、replace 提交、L2 标记与对账）、「添加文章」网页入口 `/add` |
| reading 数据库 | 77B | PostgreSQL，`127.0.0.1:5432`，库 `db77database`，schema `reading` 与 `agnes_auth`〔运行态〕 | 文章/版本/分句（reading）；账号、会话、限额计数、调用记录、入库作业（agnes_auth） |
| Edge TTS 上游 | 微软云 | WSS（`edge_tts.constants.WSS_URL`） | 文本→MP3 流（`audio-24khz-48kbitrate-mono-mp3`） |
| Gemini API / Agnes API | 云端 | HTTPS（OpenAI 兼容 chat/completions） | agnes-service 补译所用 LLM（Gemini 主、Agnes 兜底，运行态确认） |
| agy L2 执行 | ddPC 与 A001 的 VM 各一份〔设计/ingest.md〕 | 非服务进程，由 agent 调度〔未核实具体启动方式〕 | 当 API 路没做成 ready 时整篇接手补译，`tr_via=agy-gemini` |
| L2 巡检 | A001 的 VM〔设计〕 | 定时任务〔未核实单元名/是否已建〕 | 每小时（08:00–24:00 JST）拉 `GET /api/reading/l2`，用自有 agy 完成网页通道待办 |
| TtsWeb（ddPC） | ddPC（推断为 Windows 机，见 2.7） | 同源 aiohttp，默认端口 8791〔代码 `ddpc_tts_web.py` PORT〕 | 家中播放副本：77B ready 文章的只读缓存＋预热、本地 SQLite shares、HLS 合成；可离线播缓存文章 |
| muse-tts | 「本机」muse-tts 流式服务（见 2.8） | aiohttp，`127.0.0.1:8790`〔代码 `refs/tts_service.py`〕 | 纯 MP3 流式 TTS（无 HLS、无存储），供本地/agent 取音频 |
| 播放页 v4 | 浏览器（由 TtsWeb 下发） | `GET /`（默认）与 `/v4` | Vue 3 单文件跟读页：当前句卡片＋全文列表、ruby/译文开关、选文、遥控导航 |
| GitHub 仓库 | — | — | 〔未核实〕本次在 77B 上未找到本系统自身的 git 仓库（`tts-web/`、`agnes-service/` 均无 `.git`）；文档里出现的 GitHub 仓库只是**设计参考**（见 2.10） |

### 1.3 文字版拓扑（谁调谁）

```
                       ┌──────────────────────── 公网（instabox 端口转发，约 20 s 掐断长请求）
                       │
  手机/电脑/电视浏览器 ─┼─ GET / ,/v4 ,/v3/t/<id> ,/v3/articles ,POST /play ,GET /hls/... ──▶ TtsWeb(77B) :8791
                       │                                                                     │  │  │
  手机浏览器（保存）  ─┼─ GET /add ,POST /api/auth/* ,POST /api/reading/ingest (JWT) ──▶ agnes-service :8793
                       │                                                                     │  │  │
  agent A001/A003 ─────┼─ ① TtsWeb 令牌直调 /share、/api/v1/ingest/preview|commit ─────────▶ TtsWeb
                       │   ② MuseAgent 账号 JWT 调 /api/reading/ingest、/api/reading/l2 ─▶ agnes-service
                       │
  ddPC TtsWeb ─────────┴─ GET /api/v1/articles[/<id>]（ddpc-cache 只读令牌，每 preheat_seconds）▶ TtsWeb(77B)

  agnes-service ──(127.0.0.1, TTSWEB_TOKEN)──▶ TtsWeb: /share, /api/v1/articles/{id}, /api/v1/ingest/preview,
                                               /api/v1/ingest/readings_check, /api/v1/ingest/commit
  agnes-service ──HTTPS──▶ Gemini API（主）/ Agnes API（兜底）
  agnes-service ──Npgsql──▶ PG db77database.agnes_auth
  TtsWeb ──psycopg（角色 reading_rw）──▶ PG db77database.reading；PG 不可用时回落 SQLite ttsweb.db
  TtsWeb ──WSS──▶ Edge TTS ──MP3──▶ ffmpeg（AAC 96k, HLS 3 s 片）──▶ hls/<sid>/
  agy（ddPC / A001 VM）──TtsWeb 令牌──▶ TtsWeb: GET article → preview → commit(replace)
```

---

## 2. 组件逐个说明

### 2.1 TtsWeb（77B 生产）

**职责**：文章库的唯一 HTTP 门面（读写 reading schema）；分句与 fugashi 骨架（入库前置的唯一管线）；commit 契约校验；Edge TTS 合成与 HLS 会话；下发播放页与前端库。

**实现方法**

- 语言/框架：Python 3 + aiohttp（`web.run_app`），同步 DB 调用经 `asyncio.to_thread` 放线程（`tts_web.py: _in_thread`）。
- 关键文件与分工：
  - `tts_web.py`：路由与 handler（`make_app`）；Edge TTS 流式（`stream_segment / stream_text / split_text`）；HLS（`handle_play / _run_hls_session / _cleanup_hls_sessions / handle_hls_file`）；/share、/t、/v3/*、/api/v1/* handler。
  - `store.py`：`Store` 类，PG（psycopg v3，`application_name=tts-web-reading`，`connect_timeout` 默认 3 s）读写与 SQLite 回落；任何 PG 异常统一抛 `PgUnavailable`。
  - `ingest.py`：入库契约纯逻辑（错误体 `ApiError`、8 个错误码、`plan_commit`、`plan_share`、`preview`、`readings_check`、`check_added_via`）。
  - `tokenizer.py`：规范化、分句、fugashi 分词、数字串合并、ruby 对齐（`SPLITTER="svc-v1"`）。
  - `auth.py`：令牌鉴权与鉴权失败限流（`Auth.identify / require / resolve_added_by`）。
  - `player_v4.html`（默认页）、`player_v3.html`、`player_v2.html`、`vue.global.prod.js`、`hls.min.js`：前端。
  - `ingest.md` / `ingest_prompt.md`：给执行者 agent 的契约说明与翻译模板，HTTP 直接下发。
- 配置：`config.json`（路径可由环境变量 `TTSWEB_CONFIG` 覆盖）。键：`host`、`port`、`pg.{host,port,dbname,user,password_file,connect_timeout}`、`sqlite_path`、`tokens_file`、`grace_until`。PG 口令从 `password_file` 或环境变量 `READING_RW_PASSWORD` 读（`store.py: Store.__init__`），不在 config 明文。
- 出网：Edge TTS 走 `HTTPS_PROXY` 环境变量代理，失败再直连（`proxy_candidates`）。

**端点（`tts_web.py: make_app`，以代码为准）**

| 组 | 方法/路径 | 鉴权 | 说明 |
|---|---|---|---|
| 页面 | `GET /` | 无 | **默认页 = `player_v4.html`**（`handle_index`，2026-11-20 起） |
| 页面 | `GET /v2`、`/v3`、`/v4` | 无 | 各版播放页并存；均 `Cache-Control: no-store`、每次从盘读 |
| 静态 | `GET /vue.global.prod.js`（no-store）、`/hls.min.js`（max-age 86400） | 无 | 前端库 |
| 合成 | `POST /play` | 无 | 建 HLS 会话，返回 `{url:"/hls/<sid>/playlist.m3u8"}` |
| 合成 | `GET /hls/{sid}/{file}` | 无 | 只放行 `playlist.m3u8` 与 `seg\d+.ts`，no-cache |
| 合成 | `GET/POST /tts`、`/tts2`、`/tts3` | 无 | 旧裸流（MP3 / fMP4 AAC / ADTS），备用 |
| 存取 | `POST /share` | 写令牌（宽限期后必需） | 存原文、去重、建 v1 partial |
| 存取 | `GET /t/{sid}` | 无（短链即凭据） | 旧形状 `{text,lang,rate,title?,added_by?,added_at?}`，不含 status |
| 存取 | `GET /v3/t/{sid}` | 无 | 旧字段＋`status`＋`share_id`、`version`、当前版本 `sentences[{seq,para,text,tr,kana,toks,len_class}]` |
| 存取 | `GET /v3/articles` | 无 | 选文列表：未删除且 `status IN ('ready','partial')`，倒序，上限 500，字段 `share_id,title,added_at,added_by,lang,sentences(句数),status` |
| 入库 | `POST /api/v1/ingest/preview` | read | 现算骨架，不落库 |
| 入库 | `POST /api/v1/ingest/commit` | write | 新建或 replace 出新版本 |
| 入库 | `POST /api/v1/ingest/readings_check` | read | 词典佐证（第二期） |
| 只读 | `GET /api/v1/articles[?since=ISO]`、`/api/v1/articles/{sid}` | read | 列表（含 `added_via`、`status`、`current_version`）/ 单篇全量（含句子的 `tr_by/tr_via/tr_model/fixes/content_hash`） |
| 文档 | `GET /ingest.md`、`/ingest_prompt.md` | 无 | text/markdown，no-cache，每次从盘读 |
| 运维 | `GET /health` | 无 | 恒 `{"ok": true}`（不探 PG） |

请求体上限：应用 `client_max_size = 512 KB + 1 KB`；/api/v1 另由 `_api_body` 卡 512 KB（`API_MAX_BODY`）。

### 2.2 agnes-service（77B 生产）

**职责**：账号与会话（JWT＋refresh Cookie）、按账号限额、Agnes 文/图/视频代理（与朗读无关的原始功能）、`POST /api/analyze`（第一期日语分析）、**第二期入库编排**与网页保存入口。

**实现方法**

- .NET 10 minimal API（`AgnesService.csproj`：`net10.0`、Version 3.0.0、包 Npgsql 9.0.3 与 JwtBearer 10.0.0、`InvariantGlobalization`）；以 `dotnet publish/AgnesService.dll` 运行（`agnes-service.service`）。
- 关键文件：
  - `Program.cs`：宿主、中间件（安全头、JSON 错误、只记 method/path/status 的请求日志）、全部公共/鉴权路由、`Generate` 记账管线、CLI 分派（`--init-env`、`--self-test`、用户管理命令）。
  - `src/IngestService.cs`：`IngestApi`（5 个 /api/reading 端点）、`IngestStore`（`agnes_auth.ingest_jobs` 读写）、`IngestWorker`（BackgroundService：worker 池＋扫描器＋对账＋清理；`Pipeline` 为编排主体）。
  - `src/Analyze.cs`：`Providers`（provider 定义、候选次序、就绪判定）、`Analyze`（prompt 常量、`ValidateRaw/ValidateEnrich`、分块 `MakeChunks`、L1 引擎 `RunUnit / RunEnrichCore`）。
  - `src/TtsWebClient.cs`：调 TtsWeb 的客户端，失败分 5 类（t / auth / conflict / gone / invalid）。
  - `src/FixTriage.cs`：fixes 本地分流（第 1 层）。
  - `src/Circuit.cs`：内存熔断器。
  - `src/Upstream.cs`：通用上游调用＋按 provider 的令牌桶 `UpstreamLimiter`、脱敏 `Redact`。
  - `src/LimitService.cs`、`src/CallLog.cs`、`src/AuthService.cs`、`src/AccountService.cs`（改密）、`src/AdminApi.cs`、`src/AuditService.cs`、`src/Passwords.cs`、`src/Settings.cs`、`src/Db.cs`、`src/UserCli.cs`、`src/Agnes.cs`、`src/ApiError.cs`。
  - `index.html`：管理页＋「添加文章」页签（`/add` 别名返回同一文件）。
  - `auth_schema.sql`：`agnes_auth` 全量幂等 DDL（含 TASK4/5/6 迁移）。
- 配置：`Settings.Get` = 进程环境变量优先，其次 `.env`（按 mtime 热重读）。但 systemd unit 用 `EnvironmentFile=-…/.env` 把 `.env` 在启动时灌入环境，**故启动时已存在的键全部冻结，改任何键都要重启**（`.env.example` 第 63–64 行、`TASK6-PHASE2-PLAN.md §1.6`）。

**端点（朗读相关部分）**

| 方法/路径 | 鉴权 | 说明 |
|---|---|---|
| `GET /`、`/index.html`、`/add` | 无 | 前端页（`/add` 时登录后直开「添加文章」页签） |
| `GET /health` | 无 | 版本、DB 就绪、默认限额、analyze（provider 就绪、默认 provider、次序、prompt 版本、deadline、**熔断状态**、**上游 RPM 用量**）、ingest（workers、deadline、是否配了 TTSWEB_TOKEN 与 TTSWEB_PUBLIC_URL） |
| `POST /api/auth/login`、`/refresh`、`/logout` | Cookie 端点（须 JSON Content-Type） | 见第 6 节 |
| `GET /api/auth/me` | JWT | 含 `reading_ingest`（页面据此显示「添加文章」页签，仅外观；真正判定在 ingest 端点） |
| `POST /api/analyze` | JWT | kind=analyze，raw / enrich 两形状 |
| `POST /api/reading/ingest` | JWT（账号 `reading_ingest` ∈ agent/web） | 异步入库作业，202 |
| `GET /api/reading/jobs/{id}`、`GET /api/reading/jobs` | JWT（本人或 admin） | 作业状态/列表（`?status=&channel=&share_id=&limit=&mine=1`） |
| `GET /api/reading/l2` | JWT（admin 或 agent 账号） | L2 待办清单，**回答前先对账** |
| `POST /api/reading/l2/{id}/report` | 同上 | 可选回填 `{note, give_up}`；`give_up=true` 把作业转 failed/l2_failed |
| `/api/admin/*` | JWT＋Admin 策略＋库内复核 | 用户、IP 封禁、审计、调用记录（`GET /api/admin/calls`） |

### 2.3 reading 数据库

- PostgreSQL，库 `db77database`；本系统用两个 schema：`reading`（文章，TtsWeb 用角色 `reading_rw` 访问）与 `agnes_auth`（agnes-service 用 `AGNES_DB_CONNECTION` 连接，示例为本机 unix socket peer 认证）。
- `reading` DDL：工作目录 `refs/reading_ddl.sql`（注：**该文件是第一期版本**，缺 `articles.added_via` 列、`tr_via` 白名单缺两个值；生产已迁移，见 5.1 与第 10 节）。
- 运行态（2026-11-20）：`reading` 有 `articles / versions / sentences`（＋视图 `v_current_sentences`）；未删除文章 6 篇（`ready/legacy` 5、`ready/web` 1）；`agnes_auth.ingest_jobs` 1 行（`done/web`）。

### 2.4 Edge TTS 上游

- 协议：Edge「大声朗读」的 WSS 协议；借 `edge_tts`（`tts_web.py` 模块文档串记为 edge-tts==7.3.1）的 `DRM.generate_sec_ms_gec`、`DRM.headers_with_muid`、`mkssml`、`ssml_headers_plus_data`、`TTSConfig`、`WSS_URL`、`SEC_MS_GEC_VERSION` 等辅助；传输层自己用 `websockets.connect`（去掉重复的 `Sec-WebSocket-Version`、Origin 单独传），`open_timeout=10`、`close_timeout=5`，单段接收总时限 120 s（`asyncio.timeout(120)`）。
- 请求：先发 `speech.config`（`outputFormat=audio-24khz-48kbitrate-mono-mp3`，`sentenceBoundaryEnabled=true`、`wordBoundaryEnabled=false`），再发 SSML。
- 响应：只取二进制帧中 `Path: audio` 的数据，文本帧只认 `turn.end` 收尾。**句边界元数据虽开启但代码未使用**——这就是播放页逐句高亮只能「按字数加权估算」的原因（真实时间轴 P3 未实施）。
- 音色：`DEFAULT_VOICES = {ja: ja-JP-NanamiNeural, en: en-US-AriaNeural, zh: zh-CN-XiaoxiaoNeural}`；`voice` 参数优先于 `lang`。
- 失败策略：首块音频到达前失败就换下一个代理候选；全部失败抛错；`/tts` 类端点取到首块才回 200、全败回 502（绝不回空 200）。
- 约束：上游无鉴权、无显式 RPM 约束体现在代码里〔未核实微软侧实际限流〕。

### 2.5 agy L2 执行

- 定位：API 路（agnes-service）没把文章做成 ready 时的整篇兜底执行者，`tr_via=agy-gemini`（`ingest.md`「L2 补译」节；`IngestApi.ListL2` 固定回 `executor/suggested_executor="agy-gemini"`）。
- 部署：ddPC 一份、A001 的 VM 一份，同一 Google 账号、共用一个额度池〔设计：`TASK6-PHASE2-PLAN.md §1.1/§5`、`ingest.md`〕。安装与登录状态〔未核实〕。
- 调度：
  - agent 通道落 L2 → 由发起的 agent 自己在 ddPC 调 agy 当场接手；
  - 网页通道落 L2 → 巡检者 A001 用自有 agy 完成。
  agnes-service **只判定、不调度**，代码里没有任何调 agy 的逻辑〔代码：全仓无 agy 调用〕。
- 执行口径（`ingest.md`）：`GET /api/v1/articles/{id}` → 已 ready 即停 → preview → 通读全篇翻译、补读音 → `commit {replace:true, share_id, base_version, text, title, lang, rate, …}`（title/lang/rate 必须原样带回）。遇 `VERSION_CONFLICT` 视为对方已完成。
- 完成判定：不需回填，由 agnes-service 自动对账关闭（4.5）。
- 凭据：agy 调 TtsWeb 用自己的 TtsWeb 令牌（`IngestWorker.RunJob` 注释「agy uses its own token」）；具体用哪条令牌〔未核实〕。

### 2.6 L2 巡检

- 设计口径〔设计〕：A001 每小时 1 次，仅 08:00–24:00 JST；拉 `GET /api/reading/l2`（MuseAgent1 的 JWT）→ 用自有 agy 完成网页通道待办；清单为空静默，只在处理了或失败时报告；另每月 1 次 L2 演练。
- 代码侧支撑〔代码〕：`ListL2` 要求 admin 或 `reading_ingest='agent'` 账号；每次拉取前先 `IngestWorker.Reconcile`；后台每 `INGEST_RECONCILE_S`（默认 3600 s）也对账一次。
- 定时任务的实体（cron / systemd timer 名称、所在机器、是否已建）〔未核实〕：本机无法列出 systemd 单元，且巡检在 A001 的 VM 上。

### 2.7 TtsWeb（ddPC 变体）

来源：`refs/ddpc_tts_web.py`。与 77B 版**同源**（Edge TTS / HLS / 页面部分逐字相同），差异逐项：

| 项 | 77B `tts_web.py` | ddPC `ddpc_tts_web.py` |
|---|---|---|
| /share | PG reading 优先；令牌鉴权、规范化、分句骨架、content_hash 去重、`added_via`；PG 不可用回落 SQLite | **旧版原样**：无令牌、无规范化、无去重，`uuid4().hex[:10]` 写本地 `ttsweb.db` 的 `shares` 表（`_share_put`） |
| /t/{sid} | PG（含 alt_ids）→ PG 不可用才查 SQLite | 只查本地 `shares` |
| /v3/t/{sid} | PG 当前版本句子；PG 不可用回落 SQLite（无句子→简易模式） | **只读缓存** `CACHE.get`（share_id 或 alt_ids）→ 未命中再查本地 shares（旧形状→简易模式）→ 都无 404 |
| /v3/articles | PG `list_public`（ready＋partial） | 缓存里的列表快照（预热只收 ready）；缓存空 → 503「等待预热」 |
| /api/v1/*、/ingest.md、readings_check | 有 | **无** |
| 后台任务 | 无 | `_preheat_loop`：启动即一轮，之后每 `preheat_seconds`（默认 3600，下限 60）一轮 |
| 预热做法 | — | 用 `cache_token_file` 里的只读令牌（tokens.json 中对应 `ddpc-cache`，scope=read）调 77B `GET /api/v1/articles` → 只取 `status=="ready"` → 逐篇 `GET /api/v1/articles/{id}` → `ddpc_cache.to_v3_payload` + `validate_payload` → `CACHE.put`；单篇失败保留旧缓存；**整轮全成功才替换列表快照并 prune 下线文章** |
| 出网 | Edge TTS 代理候选 | 同；调 77B API 也走同一代理候选，`trust_env=False`，总超时 30 s |
| 配置 | `config.json`（PG/令牌/宽限） | `config.json` 可缺省，默认值 `api_base`（77B 8791 公网根）、`cache_token_file`、`cache_db=cache.db`、`shares_db=ttsweb.db`、`preheat_seconds=3600` |
| 默认页 | v4 | v4（2026-11-20 同步；v3 留 `/v3` 回退） |
| 请求体上限 | 512 KB+1 KB | aiohttp 默认 |

- `ddpc_cache` 模块（`Cache` 类、`to_v3_payload`、`validate_payload`、`list_entry`、`LIST_KEYS`、`cache.db` 表结构）**不在本次参考件中**〔未核实〕。
- ddPC 本地新存的文章只在 ddPC 的 SQLite，不回写 77B，页面以简易模式播放。
- 平台推断：`_find_ffmpeg` 首选 `C:\Tools\ffmpeg\bin\ffmpeg.exe`、`SSL_CTX` 注释「Windows 系统证书库」，ddPC 应为 Windows 机〔推断〕。77B 版 `tts_web.py` 保留了同样两行（同源遗留，在 Linux 上走 `which ffmpeg`）。

### 2.8 muse-tts（「本机」流式服务）

- 来源：`refs/tts_service.py`。`HOST=127.0.0.1`、`PORT=8790`；只有 `GET/POST /tts`（MP3 流）、`GET /health`、`GET /`（纯文本说明）。
- 与 TtsWeb 的差别：`SEGMENT_MAX_CHARS=250`（TtsWeb 为 2000）；TLS 显式用 `/etc/ssl/certs/ca-certificates.crt`；无 HLS、无存储、无页面、无鉴权（只绑回环）。
- 用途：给本机 agent/脚本取一段日语/中英文朗读 MP3〔推断，代码无调用方〕。
- 运行位置〔未核实〕：本次在 77B 上 `127.0.0.1:8790` 无监听（`curl` 连接失败、`ss -ltn` 无 8790）。旧总览把 A001 所在 VM 称为「本机 VM」，muse-tts 可能指那台机器上的服务。

### 2.9 播放页 v4（`player_v4.html`，逻辑概述）

- 单文件，Vue 3.5 全局包（`/vue.global.prod.js`）＋自托管 `hls.js`；无构建链、无外部资源请求。v4 是 v3 的**纯视觉重设计**：`<script>` 区与 v3 逐字相同（页首注释【九】【十】【十一】），所以 JS 里的 `document.title` 后缀仍写「日语朗读页 v3」、localStorage 键仍是 `ttsv3.kana / ttsv3.tr`、数据口仍是 `/v3/t` 与 `/v3/articles`。
- 入口：`?s=<id>` → `fetchShare`（先 `/v3/t/<id>`，非 404 的失败再退 `/t/<id>`）→ `applyItem` → 自动 `startReading(true)`（被浏览器拦自动播放则静默）；`?text=` → 简易模式直接读；裸路径 → 自动展开选文面板（`/v3/articles`）。
- 富/简易模式：`applyItem` 收到非空 `sentences` 即富模式（服务端分句、ruby、译文、段落空行）；否则简易模式（客户端 `SPLIT_RE` 分句、无注音无译文，页顶小字「简易模式」）。会话建立时还要求 `RICH.text === 当前文本`（`newSession`）。partial 文章也有 `sentences`（骨架、tr 为空），因此是**富模式但无译文**。
- 播放：`POST /play {text, lang, rate}` → 拿 m3u8 地址；桌面 Chrome（非 Edge/CriOS/webOS 且有 MSE）用 hls.js（`hlsWanted`），其余（iOS Safari、webOS 电视等）原生 HLS，并用 `rewindOnce` 纠正直播型起播位置；webOS 另有每秒一次的结束看门狗。
- 跟读推进：见 3.4。
- 编辑视图已于 2026-11-20 下线（textarea 只作内核文本载体、恒 hidden）；**播放页不承载任何保存功能**，保存走 8793 `/add`。
- 电视适配：≥1280 px 电视排版、遥控键（方向键空间导航 `findNext`、确定、webOS 返回 461、媒体键 179/415/19）。

### 2.10 GitHub 仓库

- 〔未核实〕本系统自身的代码托管：`/data/workspace/tts-web/` 与 `/data/workspace/agnes-service/` 均不是 git 工作区；本次必读来源中没有任何仓库地址或推送流程。变更留痕现靠同目录 `*.bak-YYYYMMDD-<标签>` 文件与 `publish.bak-*.tar`（见 8.5）。
- 文档中出现过的 GitHub 仓库都是**设计参考**，不参与运行：
  - `cokice/japanese-analyzer`：分词/注音/词性的数据结构与展示参考（`tts-redesign/task_design.md`；硬约束「不照搬其运行时调 LLM 的做法」）；`analyze-v1` prompt 的规则来源之一（`Analyze.cs` 注释）。
  - `guokaigdg/animal-island-ui`：v4 视觉令牌参考（`player_v4.html` 注释【九】：只借令牌与形状，不引入该库）。

---

## 3. 端到端数据流

### 3.1 链路 (a)：网页保存（/add → 作业 → 代调 /share → 短链 → L0/L1 补译 → 入库与版本流转 → ready）

1. **打开入口**：手机打开 8793 公网 `/add`（或管理页「添加文章」页签）。无会话时登录：`POST /api/auth/login` 发 30 分钟 access JWT（页面放内存）＋ `agnes_rt` refresh Cookie（`Path=/api/auth; HttpOnly; SameSite=Strict`）。
2. **提交**：页面 `POST /api/reading/ingest`，`Authorization: Bearer <access JWT>`，只能带 `title`（必填，≤120）、`text`（必填，≤5000，`\r\n`→`\n` 并 trim）、`lang`（ja/zh/en，默认 ja）；其他字段一律 400 `unknown_field`（`IngestApi.Submit`）。
3. **准入前鉴权**（不扣额度）：`IngestApi.Caller` → `AuthService.RequireUser`（10 s 用户状态缓存）→ `reading_ingest`：`web` → 通道 web、`added_by="web"`；`agent` → 见 3.2；`none` → 403 `ingest_forbidden`；匿名 → 401。
4. **准入**：`LimitService.Admit(uid, "ingest")`（每分钟/每日/并发检查，计 1 次日额度），随即 `ticket.DisposeAsync()` 释放并发槽（作业不占 Generate 并发）。被拒时写一行 call_log 后抛 429。
5. **记账与落作业**：`CallLog.WriteReturningId(kind=ingest, outcome=accepted, http 202, detail="ch=web;by=web")`；`IngestStore.Insert` 写 `agnes_auth.ingest_jobs`（`stage=share`、`status=queued`、`source_text`=原文、`call_log_id`）。
6. **同步代调 /share（上限 8 s）**：`TtsWebClient.Share` → `POST http://127.0.0.1:8791/share`，头 `Authorization: Bearer <TTSWEB_TOKEN>`，体 `{text, lang, added_via:"web", title, by:"web"}`（`rate` 为空不传，TtsWeb 默认 `-20%`）。
   - TtsWeb `handle_share`：`AUTH.identify` 识别为 `agnes-service`（write）→ `_ingest.T.normalize`（半角片假名/全角英数 NFKC、换行统一、去首尾空白）→ 长度 ≤5000 → `check_added_via` 得 `web` → `AUTH.resolve_added_by(who,"web")`（`web ∈ can_act_for`）→ `plan_share`（分句＋骨架，tr 全空）→ `STORE.share`：
     - 按 `content_hash` 查未删除同文：命中则直接返回已有 share_id（响应带 `duplicate:{status,version}`）；
     - 否则 `secrets.token_hex(5)` 生成 10 位十六进制 share_id，插 `articles`（`status='partial'`、`current_version=1`、`added_via='web'`、`added_by='web'`）＋ `versions` v1（`tool={splitter,tokenizer,dict,kind:"share",caller:"agnes-service",token:true}`）＋ 全部 `sentences`（骨架、tr 为空）。单事务，`SET CONSTRAINTS ALL DEFERRED`（`current_version` 外键延迟）。
   - 返回 `{id, url:"/?s=<id>", v3_url:"/v3?s=<id>"}`。
   - agnes-service `IngestStore.SetShare` 写回 share_id、`stage=preview`；若部分唯一索引冲突（同篇已有活动作业）→ 退额度、本作业 `cancelled/duplicate`，返回 200 `{duplicate:true, job:<那个作业>}`。
   - /share 超时或 TtsWeb 不可用 → 只记 warn，作业保留原文，交给 worker 按 T 态补（202 里 `share_id=null`）。
7. **202 返回**：`{job, job_id, share_id, url}`，`url = TTSWEB_PUBLIC_URL + "/v3?s=<id>"`（未配则相对路径）。此时短链已可朗读（partial：富模式、有注音无译文）。页面轮询 `GET /api/reading/jobs/{id}`。
8. **worker 推进**（`IngestWorker.Enqueue` → `WorkLoop` → `RunJob` → `Pipeline`；`IngestStore.Claim` 原子把 queued/retry 改 running）：
   1. share（如第 6 步没拿到短链）；
   2. **回读**：`GET /api/v1/articles/{id}`；已 `ready` → 作业 `done`（`already_ready` 或 `done_by_other`）；取 `text/lang/rate/title/current_version`，标题空则取正文前 20 字（记 `title=auto`）；
   3. **D8 非日文兜底**：`lang!="ja"` 或假名占（假名+汉字）比例 < `INGEST_KANA_MIN_PERCENT`（默认 10%）→ `done`、outcome `non_ja`，文章停在 partial（终态）；
   4. **preview**：`POST /api/v1/ingest/preview` 取服务端分句与 toks；
   5. **enrich（L0/L1）**：`Providers.Candidates` → `Analyze.RunEnrichCore(EngineOptions.ForIngest())`，详见第 4 节；
   6. **fixes 核对**：`FixTriage.Triage` → 待定改写项送 `POST /api/v1/ingest/readings_check` → `FixTriage.Resolve`；结果 `fx=a/r/s` 记入作业与 call_log；
   7. **commit（replace）**：`POST /api/v1/ingest/commit`，体 `{replace:true, share_id, base_version:<回读的 current_version>, text, title, lang, rate, executor:"agnes-service", tr_via:"<主provider>-api", tr_model, prompt_version:"enrich-v2", allow_partial:true, sentences:[{text, tr?, tr_via?, tr_model?, readings?}]}`；
   8. TtsWeb `plan_commit` 校验（3.2 第 4 步同一套）→ `Store._replace`：`FOR UPDATE` 锁文章、校 `base_version==current_version`（否则 409 `VERSION_CONFLICT`）、查别篇同文、`new_v = max(version)+1`、写 `versions`（`tool.kind="replace"`、`base_version`、原文变化时 `prev_text`）与全部 `sentences`、更新 `articles.status/current_version/text/title/lang/rate/updated_at`（**不改 added_by/added_via**）；
   9. 结果：`status=ready` → 作业 `done`、call_log `done:ready`；`partial`（有句未译或缺读音）→ 进 `NotReady`（网页通道延迟重试，见 4.3）；409 → `done`（`version_conflict_converged`）；其他 4xx → L2（`quality`）。
9. **版本流转示例**：v1（share，partial，骨架）→ v2（agnes-service replace，ready）；若 v2 仍 partial → 15 分钟后整篇重做出 v3 …；L2 agy 完成时再出 vN（`tr_via=agy-gemini`）。所有版本保留（`reading.versions` 注释「全部保留」），读取只看 `current_version`。

### 3.2 链路 (b)：agent 通道保存（A001/A003）

agent 有两种合法姿势，代码都支持：

**姿势 ①：TtsWeb 令牌直调（agent 自己当执行者）**——`ingest.md`「两步姿势」与「驻地运行手册」：

1. 令牌：`Authorization: Bearer $TTSWEB_TOKEN`（agent 各自的 TtsWeb 写令牌；tokens.json 条目 `A001`、`A003`，或驻地 `ddpc-claude` 代填 A001/A002/A003）。
2. （可选）`POST /share {text, title, lang?, by?, added_via?}` 先存原文拿短链；不带 `added_via` 时带令牌调用按 `agent` 记；`by` 与令牌名不同时须在 `can_act_for` 内，否则 403。
3. `POST /api/v1/ingest/preview {text, lang?}` → `text`（规范化后）、`content_hash`、`sentences[{seq,para,text,char_start,char_end,kana,toks}]`、`warnings`。
4. 执行者通读全篇翻译后 `POST /api/v1/ingest/commit`，`ingest.py: plan_commit` 校验顺序：
   - 必填 `text/title/executor/tr_via`；`tr_via ∈ TR_VIA`；可选 `tr_model/prompt_version/translation_text/allow_partial/replace/added_via/share_id/base_version`；
   - `sentences[].text` 必须与服务端分句**逐句一致**，否则 422 `SPLIT_MISMATCH`（附首个不同处）；
   - `readings[{tok,s,r}]`：下标与表层形核对、`r` 必须平假名（`HIRA_RE`），应用后记入该句 `fixes[{tok,from,to,by:executor}]` 并重算 ruby 与 kana；
   - 读音完整性：非符号且含汉字/数字（`NEEDS_READING_RE`）的 token 必须有读音，缺则 400（除非 `allow_partial`）；
   - 带 `translation_text` 时 `tr_via` 必须 `provided`，且各句 `tr` 去空白拼接须等于译文全文（`ALIGN_MISMATCH` 分 missing/orphan/altered）；
   - 缺译文句 → 422 `TR_MISSING`（除非 `allow_partial`）；单句 tr >500 字拒；译文假名占比 >30% 记 warning `TR_SUSPECT`；
   - `status = ready` 当且仅当无缺译且读音齐全，否则 `partial`。
5. 写库（`Store.commit`）：非 replace 时按 content_hash 去重——命中直接返回 `duplicate:true`（**普通 commit 补不了已存在文章的译文**，要补就 replace）；否则新建文章 v1（`tool.kind="commit"`）。`tr_by = executor`；`tr_via/tr_model` 逐句优先、缺省用版本级值；`tr_at = now()`。
6. 失败自纠最多 2 次；仍失败就 `/share` 存原文并报告（`ingest.md` 运行手册）。

**姿势 ②：MuseAgent 账号 JWT 调 agnes-service（API 路自动补译）**：

1. 用 agnes-service 账号登录取 JWT；账号须 `reading_ingest='agent'` 且有 `muse_id`（运行态：MuseAgent1→A001、MuseAgent2→A003），否则 403 `ingest_agent_unmapped`。
2. `POST /api/reading/ingest`，字段只许 `text/share_id/title/lang/rate`，且 `text` 与 `share_id` **二选一**：
   - 交 `text`：同 3.1 第 6 步代调 /share，`by=<muse_id>`、`added_via="agent"`（agnes-service 令牌 `can_act_for` 含 A001–A003）；
   - 交 `share_id`（agent 已自行 /share 过）：不再 share，直接从回读开始；若该 share_id 已有活动作业 → 200 `{duplicate:true, job}`、不计额度。
3. 之后与网页通道相同的 Pipeline；差别在未做成 ready 时：**agent 通道不做延迟重试，直接转 L2**（`NotReady` 只对 `channel=="web"` 排延迟重试），由发起 agent 在 ddPC 调 agy 当场接手。
4. agent 通过 `GET /api/reading/jobs/{id}` 看结果；作业视图含 `status/stage/provider_model/chunks/trail/error_code/l2_reason/result` 等。

### 3.3 链路 (c)：打开短链播放

1. 用户打开 `<8791 公网根>/?s=<id>`（agnes 回的是 `/v3?s=<id>`，二者都给出同一套页面逻辑：`/` 现为 v4，`/v3` 为 v3 视觉）。
2. 页面 `fetchShare(id)`：
   - 77B：`GET /v3/t/<id>` → `Store.get_with_sentences`：按 share_id 或 `alt_ids` 命中未删除文章（精确 share_id 优先），连接 `sentences` 的 `version = current_version`，回 `{text,lang,rate,title?,added_by?,added_at?,status,share_id,version,sentences[]}`。PG 不可用 → 回落 SQLite `shares`（只有旧字段，无 sentences）→ 简易模式。404 → 页面保持默认（无文章）。
   - ddPC：`GET /v3/t/<id>` → 只读缓存（富）→ 本地 shares（简易）→ 404。
3. 富/简易判定（`applyItem` / `newSession`）：有非空 `sentences` → 富模式（卡片＋全文、ruby、译文三态「显示/模糊/隐藏」、假名开关；`status=partial` 时页面可标「译文待补」）；无 → 简易模式。
4. 选文列表来源：`GET /v3/articles`。77B = PG 现查（ready＋partial，倒序，≤500）；ddPC = 预热快照（只 ready）。不可用时面板内提示，当前文章照常播放。选中一篇：先取到数据再 `stopSession → applyItem → startReading(true)`，并 `history.replaceState` 同步 `?s=`。
5. ddPC 只读缓存与预热：见 2.7。断网或 77B 暂停时，ddPC 仍可播缓存中的 ready 文章（音频合成仍需外网到 Edge TTS）。
6. 页面打开后自动播放（被拦则等用户按播放）。

### 3.4 链路 (d)：朗读合成与跟读推进；电视推屏

**服务端合成（`tts_web.py`）**

1. `POST /play {text, lang, rate}`：text ≤5000（`MAX_TEXT_CHARS`）；音色按 lang；rate/pitch/volume 正则校验。
2. `_cleanup_hls_sessions(keep=6)`：只保留 mtime 最新的 6 个会话目录；`sid = uuid4().hex[:16]`。
3. 后台 `_run_hls_session`：`stream_text` → `split_text` 用 `SENTENCE_RE`（句末 `。．.!?！？；;…\n`）切句，再打包成 ≤`SEGMENT_MAX_CHARS=2000` 字的段（超长句硬切）——注释说明「整段单次合成，避免拼接处 iOS 解码中断」；**每段一条 WSS 连接，顺序串行**。
4. 首块 MP3 到达后启动 ffmpeg：`-ar 44100 -ac 1 -c:a aac -b:a 96k -f hls -hls_time 3 -hls_list_size 0 -hls_flags omit_endlist -hls_segment_filename hls/<sid>/seg%03d.ts`，MP3 边收边喂；结束后补 `#EXT-X-ENDLIST`。
5. `/play` 轮询（每 0.25 s，最多 60 次 ≈15 s）直到 m3u8 出现第一个 `.ts` 才返回 `{url}`，否则 504「会话启动超时」。起播约 2–3 s（注释）。
6. `GET /hls/<sid>/<file>` 直接读文件下发（m3u8 与 ts 均 `no-cache`）。

**页面跟读推进（`player_v4.html` 脚本，与 v3 相同）**

- 句子列表：富模式用服务端 `sentences`，简易模式用客户端 `splitSentences`。
- 权重：`weightOf = 有效长度/速度 + 句间停顿 + 逗号×0.2 s + 省略号×0.35 s`；`PRIOR.ja = {cps 6.5, kcps 7.5, pause 0.45}`；富模式「有效长度」用该句 kana 长度/kcps，简易模式用去标点字数/cps。累计得 `cum[]` 与总权重 `W`。
- 总时长：每 `POLL_MS=1500` ms 拉一次 m3u8，累加 `#EXTINF` 得已生成秒数 `plSum`，见 `ENDLIST` 即精确；未结束时取 `max(plSum, 缓冲末端, duration, currentTime, 估算 W/speed)`，估算被追上时放大到 `plSum×1.15`。
- 当前句：`target = currentTime/total × W` → 二分 `locate(cum)` → 高亮并居中滚动（用户手动滚动后让行 4 s）。状态行在未结束时注明「合成中，进度为估算」。
- 点句跳转：`seekToLine` 按 `cum[i]/W × total` 估算时间点，直播期不越过已生成部分；未开播先开播、起播后再跳。
- 局限：没有用 Edge 句边界做真实时间轴，长句/停顿多的句子会有小半句偏差（旧总览所述「P3 已设计未实施」，代码现状与之一致）。

**电视推屏路径**

- 页面侧：支持电视排版与遥控（2.9）。
- 「ddPC 脚本打开短链页、播放前 setvol 12」只见于旧总览，**本次所有代码来源中都没有该脚本**〔未核实〕。

---

## 4. 补译分级与降级

### 4.1 分级定义（代码对应）

| 级 | 定义 | 代码落点 |
|---|---|---|
| L0 | 首选候选（运行态为 `gemini:gemini-flash-lite-latest`）一次通过校验 | `RunUnit` 第一个候选 `ok`；trail 形如 `gemini/flash-lite-latest:ok` |
| L1 | API 路内部降级：同模型修复重试 → 同 provider 换模型 → 换 provider → 拆块/重分块 → 到时限提交已完成部分；网页通道另有整篇延迟重试 | `Analyze.RunUnit / RunEnrichCore`、`IngestWorker.NotReady` |
| L2 | agy Gemini agent 整篇接手（作业 `status=l2`、`stage=l2-pending`） | `IngestWorker.End(... "l2" ...)`、`IngestApi.ListL2`、`Reconcile` |
| L3 | 原文保底：文章停在 partial 照常可朗读 | 任何失败都不删文章；`/v3/t` 照样返回骨架 |
| T 态 | TtsWeb/PG 不可用：不算降级，只退避重试，最长 24 h | `RunJob` 的 `TtsWebError Kind=="t"` 分支 |

### 4.2 L1 梯子：单块（chunk）怎么走（`Analyze.RunUnit`，ingest 模式）

对每个候选 `(provider, model)` 依次：

1. 熔断打开 → 跳过（trail `…:open-<reason>`）。
2. 块超出该 provider 分块上限（`ProviderDef.Fits`）→ 按该 provider 上限**重新分块**（trail `rechunk`）。
3. 半开试探（冷却已过的熔断）计入 `probe`。
4. 调用（`CallModel`，`response_format=json_object`；gemini 另带 `reasoning_effort`：flash-lite=`minimal`、flash=`low`）：
   - 内容通过 `ValidateEnrich` → 成功；
   - 内容不合格/非 JSON → 把失败清单作为 user 消息追加，**同模型修复重试 1 次**；
   - `finish_reason=length` → 拆半；
   - 上游 401 / 无 key → 跳过整个 provider；403、429、5xx、超时、网络、空内容 → 下一个候选；**其他 4xx → Fatal，整篇中止**；
   - 本地令牌桶拿不到令牌（`upstream_throttled`）→ 不计调用、下一个候选；
   - 剩余时限 <5 s → 停（`deadline`）。
5. 调用预算：每块 ≤`ChunkCallCap=3` 次；整篇 ingest 上限 `min(60, 2×初始块数+4)`（直调 analyze 为 `min(30, …)`）；块并行度 `ChunkParallelism=2`。

候选次序（`Providers.Candidates`）：请求指定的 provider/模型 → `ANALYZE_DEFAULT_PROVIDER` → `ANALYZE_PROVIDER_ORDER`，provider 内按模型表顺序，未就绪（key 空或形态不对、URL 不合法）的 provider 跳过。

| provider | 模型顺序 | 代码默认 | 生产运行态（/health） |
|---|---|---|---|
| gemini | `gemini-flash-lite-latest` → `gemini-flash-latest` | 默认 provider=`agnes`、次序=`agnes,gemini`（`Analyze.cs: Providers.DefaultProvider / Order`） | `default_provider=gemini`，`order=[gemini, agnes]`（由 `.env` 设定，`.env.example` 同） |
| agnes | `agnes-3.0-flash` → `agnes-2.5-flash` | | 两家均 `ready=true` |

`ValidateEnrich` 校验口径：每块必须覆盖全部 seq、无重复/越界；`tr` 非空且 ≤500 字（prompt 要求 ≤300）；fixes 项 `i` 在范围内、`s` 与该下标表层形一致、`r` 纯平假名、送假名覆盖（`ReadingCoverageError`）、同一 `i` 不得给两个不同读音；不需读音的 token（假名/拉丁/符号）的 fix 丢弃；与原读音相同的 fix 丢弃。任一错误 → 整块不合格。

### 4.3 整篇层面：超时、分块参数、延迟重试（以代码常量为准）

| 参数 | 默认值 | 出处 |
|---|---|---|
| gemini 分块 | 20 句 / 1500 字 | `Analyze.cs: Providers.All["gemini"]`（`INGEST_CHUNK_SENTS_GEMINI` / `INGEST_CHUNK_CHARS_GEMINI`） |
| agnes 分块 | 8 句 / 600 字 | 同上（`INGEST_CHUNK_SENTS_AGNES` / `INGEST_CHUNK_CHARS_AGNES`） |
| 单次调用超时 | gemini lite 45 s、gemini flash 90 s、agnes 120 s | 同上（`INGEST_TIMEOUT_GEMINI_LITE_S / _FLASH_S / INGEST_TIMEOUT_AGNES_S`） |
| enrich 整篇时限 | 420 s，从 enrich 开始计 | `Providers.IngestDeadline`（`INGEST_DEADLINE_S`），运行态 420 |
| 直调 `/api/analyze` | 单次 180 s、整单 600 s、全局分块 20/1500、不用熔断 | `Providers.CallTimeout / AnalyzeDeadline`、`EngineOptions.Direct` |
| 网页通道延迟重试 | 900 s / 3600 s / 14400 s（15 分 / 1 小时 / 4 小时），整篇重做 | `IngestWorker.RetryScheduleS`（`INGEST_RETRY_SCHEDULE_S`） |
| T 态退避 | 60 / 300 / 900 / 3600 s，末值重复；累计 86400 s 后 `failed_ttsweb` | `TBackoffS`、`TMaxS` |
| 扫描/对账/清理 | 扫描 60 s、L2 对账 3600 s、过期作业每日清理（保留 30 天） | `ScanS`、`ReconcileS`、`IngestApi.KeepDays` |
| worker 数 | 2 | `IngestApi.Workers`（`INGEST_WORKERS`） |
| TtsWeb 调用超时 | 30 s（commit 至少 60 s、readings_check 15 s、提交时同步 /share 8 s） | `TtsWebClient.TimeoutS / Commit / ReadingsCheck`、`IngestApi.Submit` |

enrich 结束后的分支（`IngestWorker.Pipeline`）：

- 无任何句子产出 → `NotReady(L2ReasonOf(o))`；
- 有产出 → fixes 核对 → replace 提交（`allow_partial:true`，已完成部分先落库）→ ready 则 done；仍 partial → `NotReady`；
- `NotReady`：网页通道且 `attempt < 3` → `status=retry`、`stage=preview`、`error_code=l1_incomplete`、按表排时间；否则 → `status=l2`、`error_code=l1_exhausted`、`l2_reason`。
- `L2ReasonOf`：无结果对象 → `api-down`；撞时限 → `deadline`；校验失败或只缺读音 → `quality`；trail 含 403/429/`open-quota`/`open-rate` → `quota`；其余 `api-down`。
- 其他直接进 L2 的情形：TtsWeb 写令牌被拒（`ttsweb_auth_failed`，`api-down`）；commit 被 TtsWeb 判 4xx（`commit_<code>`，`quality`）。
- 直接结束的情形：文章已 ready（done）、非日文（done/non_ja）、409（done/converged）、文章被删（cancelled/article_gone）、其他 TtsWeb 4xx 在非 commit 阶段（failed/ttsweb_<code>）。

### 4.4 FixTriage：模型给的读音修正如何过滤（`src/FixTriage.cs`）

模型的 fixes **不直接落库**，先分流：

1. **补齐类**（原 token `r` 为空、或 `unk=true`、或 `pos=num`）：已过 `ValidateEnrich` → **采纳**（数字读音的主要来源）。
2. **改写类**（原 token 已有读音）：默认不采纳；只有两种放行：
   - 本地量词音变表命中（`NumeralForms` 一/1/三/3/六/6/八/8/十/10/百/100/何 × `CounterForms` 本/匹/杯/分/発/階/回/百/千/軒/冊/個/歳/足/泊/頭/枚/件/曲/週間/か月/ページ/点/袋/箱/羽/拍/敗/版 …，`CounterAllows`）；
   - TtsWeb 词典佐证 `supported=true`。
3. 结果：`a`=采纳（fill＋counter＋dict）、`r`=词典明确不支持、`s`=无裁决（接口不可用或未问）；记为 `fx=a{n}/r{n}/s{n}` 写入作业 result 与 call_log detail。采纳项以 `readings[{tok,s,r}]` 形式进 commit。
4. 设计取舍：核对**不放进 TtsWeb 的 commit 校验**（commit 也服务 L2 agy 与人工，人工读音可能超出词典）。

### 4.5 readings_check 的校验口径（`ingest.py: readings_check`）

- 入参 `{items:[{sentence, tok, s, r}]}`，≤`READINGS_CHECK_MAX=200` 项；`sentence` 规范化后 1–1000 字；`tok` 为 preview toks 下标。
- 对整句 `tokenize` 并取 MeCab nbest 路径（`READINGS_CHECK_NBEST=20`）；先核 `toks[tok].s == s`，不符回 `{supported:false, error:"TOKEN_MISMATCH"}`。
- 计算该 token 的字符区间，在每条路径里**只认「恰好一个节点完整覆盖该区间」**的路径，取其 kana（片假名转平假名）为候选；拼多个节点的读音不算（防「東|京」拼出错读被误判支持）。
- 另加 `s` 单独分词的读音为候选。
- `r`（转平假名后）在候选集合内 → `supported:true`；返回 `{ok, results:[{supported, candidates}]}`。
- agnes-service 侧：`INGEST_READINGS_CHECK`（默认开）；接口错误/形状不符 → 整批无裁决（全部记 suspect）。

### 4.6 provider 熔断（`src/Circuit.cs`，只用于 ingest 路径，内存态，重启即闭合）

| 类别 | 粒度 | 触发 | 冷却 |
|---|---|---|---|
| 瞬时（5xx/超时/网络/非法形状） | provider:model | 10 分钟内连续 3 次 | 300 s 起，每次再开翻倍，封顶 3600 s（`CIRCUIT_TRANSIENT_COOLDOWN_S`） |
| 限流 429 | provider:model | 1 次 | 上游 `Retry-After` 或 Gemini `retryDelay`，无则 120 s（`CIRCUIT_429_COOLDOWN_S`） |
| 配额 403（响应体含 quota） | provider:model | 1 次 | 3600 s（`CIRCUIT_QUOTA_COOLDOWN_S`）；不含 quota 的 403 按瞬时 |
| 鉴权 401 / 缺 key | 整个 provider | 1 次 | 21600 s（6 h，`CIRCUIT_AUTH_COOLDOWN_S`） |
| 质量（E7） | 整个 provider | 连续 3 篇在该 provider 上以校验失败告终（且该篇没在此 provider 成功过） | 86400 s（`CIRCUIT_QUALITY_COOLDOWN_S`） |

- 冷却过后的下一次真实调用即半开试探，成功闭合、失败重开。
- 「fixes 质量」只统计 `fx=`，**不自动熔断**（设计 DF-6，代码一致）。
- 当前熔断状态看 `/health` 的 `analyze.circuit`（运行态为空 `{}`）。

---

## 5. 数据模型

### 5.1 reading schema（PG，TtsWeb 读写）

**`reading.articles`**（一篇文章一行）

| 字段 | 说明 |
|---|---|
| `share_id` | PK，`^[0-9a-f]{10}$`；即 `/?s=<id>` 短链 |
| `alt_ids text[]` | 别名短链（去重合并时另一侧 ID 等）；/t、/v3/t、/api/v1/articles/{id} 都会匹配（GIN 索引） |
| `text` | 规范化原文，1–5000 字 |
| `lang` | ja/zh/en，默认 ja |
| `rate` | `^[+-]\d+%$`，默认 `-20%` |
| `title` | ≤120 |
| `added_by` | ≤40；agent 通道 = A00x；网页通道 = `web`；宽限期无令牌旧调用 = 请求里的 `by` 原样（未验证） |
| `added_via` | **生产已有**（DDL 参考件缺）：`agent / web / legacy`，默认 `legacy`〔运行态 `articles_added_via_check`〕 |
| `added_at` | 入库时间（JST 写入） |
| `status` | `raw / partial / ready`（DDL 默认 raw；现行代码只写 partial 或 ready） |
| `current_version` | 指向 `versions`；外键 `(share_id,current_version)` DEFERRABLE INITIALLY DEFERRED |
| `content_hash` | 生成列 `reading.sha256_hex(text)`；**未删除文章间唯一**（`articles_content_hash_uq`），全库去重依据 |
| `deleted_at` | 软删除；非空即对外不显示（/t 返回 None；/api/v1/articles/{id} 回 410） |
| `created_at / updated_at` | 时间戳 |

**`reading.versions`**：PK `(article_id, version)`；`tool jsonb`（`kind: share|commit|replace|import`，以及 `splitter/tokenizer/dict/executor/model/prompt_version/caller/token/base_version/prev_text/align` 等）；`note`；`created_by`（调用方令牌名，/share 无令牌时为 `share`）。每次 share/commit/replace 各一行，全部保留。

**`reading.sentences`**：PK `(article_id, version, seq)`，FK 到 versions（级联删）。

| 字段 | 说明 |
|---|---|
| `seq / para` | 句序号（从 0）/ 段落号（遇连续 ≥2 个换行 +1） |
| `text, char_start, char_end` | 句文本与在 `articles.text` 中的半开区间 |
| `len_class` | s（≤20 字）/ m（≤45）/ l |
| `tr` | 中文译文，NULL=未生成 |
| `tr_by` | 译文执行者（= commit 的 `executor`，如 `agnes-service`、agy 的标识） |
| `tr_via` | 出处，CHECK 白名单（生产）：`self, claude-code, agy-gemini, human, import, provided, gemini-api, agnes-api`〔运行态 `sentences_tr_via_check`；与 `ingest.py: TR_VIA` 一致〕 |
| `tr_model / tr_at` | 模型名 / 译文写入时间 |
| `toks jsonb` | `[{s, l, pos, pd, r, ruby, conj?, sub?, unk?, merged?, gloss?}]`；`pos` 为归并后的英文枚举（noun/verb/…/num/sym/other），`r` 平假名读音（符号为空串、缺为 null），`ruby` 为 `[{t, r?}]` 分段 |
| `kana` | 整句读音串（缺读音处用表层形） |
| `lemmas text[]` | 非符号 token 原形去重（GIN 索引） |
| `fixes jsonb` | 读音覆盖记录 `[{tok, from, to, by}]` |
| `content_hash` | 行内容生成列（不含时间戳），供镜像两侧对账 |

视图 `reading.v_current_sentences`：未删除文章的当前版本句子。

**状态机**

```
/share（任意通道）         commit 新文（全齐）        commit 新文（缺译/缺读音 + allow_partial）
      │                         │                           │
      ▼                         ▼                           ▼
  partial(v1, 骨架) ──replace(全齐)──▶ ready(vN)        partial(v1)
      │  ▲                                  │
      │  └──replace(仍缺, allow_partial)────┘（replace 可把 ready 改回 partial，若新版本不全）
      └──（失败/非日文/无人补）── 停在 partial（L3 保底）
```

- `current_version` 只在 share（=1）、commit 新文（=1）、replace（=max+1）时变化。
- 去重：/share 与普通 commit 遇未删除同文直接返回已有 share_id（不改动）；replace 若新原文与**另一篇**相同则拒绝。并发去重竞争由部分唯一索引兜底（`UniqueViolation` 后回查返回已有 ID）。

### 5.2 ddPC 侧 shares 表（SQLite）

- `ttsweb.db` 表 `shares(id TEXT PK, text TEXT NOT NULL, lang, rate, title, added_by, added_at TEXT)`；`added_at` 为 JST ISO 字符串（`ddpc_tts_web.py: _db_conn / _share_put`）。
- 77B 也保留同结构的 SQLite（`config.sqlite_path` 指向 `tts-web/ttsweb.db`）作 PG 不可用时的回落（`Store._sqlite / sqlite_get / sqlite_put`）。
- ddPC 另有 `cache.db`（预热缓存，表结构〔未核实〕）。

### 5.3 agnes_auth schema（agnes-service）

**`ingest_jobs`**（`auth_schema.sql` TASK6 第二期段）

| 字段 | 说明 |
|---|---|
| `id uuid` | 作业 ID（job_id） |
| `user_id / username / channel` | 谁、哪个通道（agent/web） |
| `added_by / share_id / base_version / title / lang / rate` | 写入 reading 的归属与目标；`base_version` 为最近一次回读或本作业 partial 提交后的版本 |
| `source_text` | 仅在 share 尚未成功时保留原文；作业终结且有 share_id 时清空 |
| `stage` | `share / preview / enrich / commit / done / l2-pending / failed` |
| `status` | `queued / running / retry / done / l2 / failed / cancelled` |
| `provider_model / chunks jsonb / trail` | 主产出模型、分块明细、调用轨迹（≤2000 字） |
| `error_code / l2_reason` | 错误码；L2 原因 `api-down / quota / quality / deadline` |
| `attempt / t_attempts / t_since / next_retry_at` | 延迟重试次数、T 态连续次数与起点、下次时间 |
| `result jsonb` | 文章状态/版本、`fx` 与 fixes 明细、tr_via、句数、调用数、L2 报告与关闭记录 |
| `call_log_id / l2_closed_at / created_at / updated_at / finished_at / expires_at` | 对应 call_log 行、L2 关闭时刻、过期时间（终态后 30 天，L2 无过期） |

索引：`ingest_jobs_share_active_uq UNIQUE (share_id) WHERE status IN ('queued','running','retry')`（同篇只跑一份，share_id 即锁键）；另有 retry、status、user 索引。

作业状态机：

```
queued ──Claim──▶ running ──▶ done | cancelled | failed | l2
   ▲                 │
   └── retry ◀───────┘（延迟重试 / T 态 / 进程中断；启动时 running 一律改 retry 立即到期）
l2 ──对账(ready 且 version > base_version)──▶ done（l2_closed_at，result.l2）
l2 ──report give_up──▶ failed（error_code=l2_failed）
```

**`call_log`**：每次到达限额器的生成调用一行（被 429 拒也记），字段 `created_at, user_id, username, kind(text|image|video|analyze|ingest), model, outcome, http_status, upstream_status, duration_ms, prompt_preview(≤120), detail(≤1000, 脱敏)`；90 天前的行每 6 小时内最多清一次。ingest 作业只写**一行**（`accepted`），结束时 UPDATE：outcome 取 `done:<文章状态或 outcome>`、`l2:<reason>`、`failed:<code>`、`cancelled:<code>`、中途 `retry:<reason>`，L2 对账关闭时 `done:l2`；detail 形如 `ch=web;by=web;share=<id>;retry=1;fx=a2/r0/s1;m=enrich;n=..;c=..;a=..;c0[0-14]=gemini/flash-lite-latest:ok;v=ok;tb=..`。

其他表：`users`（含 `role`、`is_active`、`token_version`、`failed_logins`、`locked_until`、限额覆盖四列、`reading_ingest`、`muse_id`）、`refresh_tokens`、`ip_block`、`login_audit`、`usage_daily(user_id, day, kind, count)`、`admin_audit`。

---

## 6. 认证与权限

### 6.1 agnes-service：JWT 与 refresh 会话（`src/AuthService.cs`）

- access token：HS256，`iss=aud="agnes-service"`，有效期 `JWT_ACCESS_MINUTES`（默认 30 分钟，运行态 30），载荷含 `sub`（用户 id）、`ver`（`token_version`）、角色；JwtBearer 校验 30 s 宽限，密钥每请求从 `JWT_SECRET` 重取（≥32 字符，否则登录 503）。
- 每个受保护请求再做用户状态复核（`RequireUser`，10 s 缓存）：存在、启用、`token_version` 一致、角色合法；状态里带 `reading_ingest` 与 `muse_id`。
- refresh：不透明令牌放 `agnes_rt` Cookie（`Path=/api/auth; HttpOnly; SameSite=Strict`，经 HTTPS 时加 `Secure`，`COOKIE_SECURE=auto` 看 `X-Forwarded-Proto`）；库里只存 SHA-256。单次有效期 `REFRESH_TOKEN_DAYS=7`，家族上限 `REFRESH_FAMILY_DAYS=30`；每次刷新轮换；10 s 宽限内的并发刷新回 `refresh_rotated` 且**不清 Cookie**（双标签页不互相登出）；超宽限重放 → 判定重用，整个家族吊销。
- 登录防护：同账号连错 `LOGIN_MAX_FAILURES=5` 次锁 `LOGIN_LOCKOUT_MINUTES=15` 分钟（423）；同 IP 15 分钟内失败 ≥`IP_MAX_FAILURES=20` → 429；24 h 内锁定 ≥`IP_BAN_AFTER_LOCKOUTS=3` 次 → 封 IP `IP_BAN_HOURS=24`（回环永不封）。客户端 IP 默认取 TCP 对端，`TRUST_PROXY=1` 才取 `X-Forwarded-For` 最右项。已知限制 P2R-19：公网网关从回环转发，IP 维度防护对公网实际失效（DESIGN.md「已知限制」）。
- 口令：PBKDF2-SHA512、210 000 次、16 字节盐、64 字节输出（`Passwords.cs`）；最短 10。
- CSRF：Cookie 端点要求 `Content-Type: application/json`；业务端点只认 Bearer；不开 CORS。
- 管理：Admin 策略（JWT role=admin）＋库内复核；自我保护与最后一个 admin 保护；所有变更写 `admin_audit`。

### 6.2 账号与 reading_ingest 通道

- `users.reading_ingest ∈ {none, web, agent}`（默认 none），`users.muse_id ~ ^A\d{3}$`（唯一）。授予途径：`PATCH /api/admin/users/{id}` 或 CLI `--set-reading-ingest NAME none|web|agent [--muse-id A001|none]`，均审计；迁移脚本不改账号。
- 形如 `A001` 的用户名保留给 Muse agent 编号，建号时拒绝（`AdminApi.ReservedUsername`）。
- 运行态（2026-11-20）：`agent` 两个（MuseAgent1→A001、MuseAgent2→A003）；`web` 两个账号（账号名不在此列出）。
- 通道语义：`agent` → `added_by = muse_id`、`added_via=agent`，可交 text 或 share_id、可带 rate；`web` → `added_by = added_via = "web"`，只收 title/text/lang。谁点了保存只记在 agnes 侧（`ingest_jobs.user_id/username`、call_log）。

### 6.3 TtsWeb：tokens.json 与 can_act_for（`auth.py`）

- `tokens.json` 结构 `{name: {sha256, scope: read|write, can_act_for: [...]}}`，**只存哈希**；明文在 `tokens/<name>.txt`（0600）。改配置**重启 TtsWeb 后生效**（吊销同理）。
- 登记名单（运行态读取名称与权限，未读任何值）：

| 令牌名 | scope | can_act_for | 用途 |
|---|---|---|---|
| `A001`、`A002`、`A003` | write | — | 各 agent 自己的写令牌 |
| `ddpc-claude` | write | A001、A002、A003 | ddPC 驻地执行者代填 |
| `77b-claude` | write | — | 77B 侧 Claude（编码实现用） |
| `ddpc-cache` | read | — | ddPC 预热只读拉取 |
| `agnes-service` | write | A001、A002、A003、**web** | agnes-service 服务令牌（`TTSWEB_TOKEN`），代调 /share 与 commit |

- `added_by` 判定（`resolve_added_by`）：未给或等于令牌名 → 令牌名；在 `can_act_for` 内 → 照填；否则 403。`web` 不是独立令牌，只是 agnes-service 可代填的名字。
- 失败限流：同 IP 60 s 内鉴权失败 >10 次 → 封 600 s（429 RETRY_LATER）。IP 取 `X-Forwarded-For` **最左项**，否则对端地址（`tts_web.py: _client_ip`）。
- /share 宽限：`grace_until` 之前允许不带令牌（记 `legacy`、`added_by` 未验证）。代码缺省 2026-12-02，**生产 `config.json` 为 2026-11-19**，即自 2026-11-19 起不带令牌的 /share 一律 401。

### 6.4 TTSWEB_TOKEN 代调关系

```
浏览器 ──(自己账号 JWT, 只到 8793)──▶ agnes-service ──(TTSWEB_TOKEN, 127.0.0.1)──▶ TtsWeb
MuseAgent JWT ───────────────────────▶ agnes-service ──(同上)────────────────────────▶ TtsWeb
```

- `TTSWEB_TOKEN` 只在 agnes-service 进程内（`.env` 键）；列入 `Upstream.SensitiveKeys`，日志、错误、call_log 一律脱敏；不进任何响应、不发浏览器。
- 「不允许把 agent 的 TtsWeb 令牌转交 agnes-service」：agent 已自行 /share 的只交 share_id（设计 §1.2；代码中 agnes-service 只用自己的令牌）。
- 失败分类：TtsWeb 回 401/403 → `auth`（API 路写不进 → 作业转 L2 `ttsweb_auth_failed`）；缺 `TTSWEB_TOKEN` 也按 auth 处理。

### 6.5 公开面

- TtsWeb 的播放与读取端点（`/`、`/v*`、`/t`、`/v3/t`、`/v3/articles`、`/play`、`/hls`、`/tts*`）**不需要任何凭据**：短链即凭据；`/v3/articles` 公开列出所有未删除 ready/partial 文章的标题与保存者。
- 写入（/share、commit）与机器读（/api/v1/*）需令牌；网页保存需 8793 账号且 `reading_ingest='web'`。

---

## 7. 限额与配额

### 7.1 agnes-service 账号限额（`src/LimitService.cs`，运行态与默认值一致）

| 项 | 默认 | 键 | 说明 |
|---|---|---|---|
| 每分钟 | 10 | `LIMITS_DEFAULT_PER_MINUTE` | 每账号 60 s 滑动窗口（内存） |
| 每日总量 | 200 | `LIMITS_DEFAULT_PER_DAY` | 所有 kind 合计；JST 00:00 重置；存 `usage_daily`（重启不丢） |
| 每日视频 | 20 | `LIMITS_DEFAULT_VIDEO_PER_DAY` | 同时计入总量 |
| 每账号并发 | 2 | `LIMITS_DEFAULT_CONCURRENT` | 内存 |
| 全局并发 | 6 | `MAX_CONCURRENT` | 内存 |

- 计数口：`POST /api/text|image|video|analyze|reading/ingest`；每账号可在 `users` 覆盖（NULL=默认，0=禁止）。
- 判定顺序：日额度 → 视频子额度 → 每分钟 → 每账号并发 → 全局并发；准入在 PG 事务里对用户行 `FOR UPDATE`，防并发抢最后一个单位。
- 退额度：上游 4xx（没花上游钱）、本地令牌桶拒绝（`upstream_throttled`）、analyze 全部调用都是 4xx 时；5xx/超时照计。
- ingest：**一个作业准入 1 次**，并发槽当场释放；系统延迟重试、T 态重试、熔断试探都不再计用户额度；同篇重复提交（活动作业已存在）不计。

### 7.2 上游 RPM 约束（`src/Upstream.cs: UpstreamLimiter`）

| 桶 | 总量/分 | ingest 子配额/分 | 说明 |
|---|---|---|---|
| agnes | 18（`UPSTREAM_RPM_AGNES`） | 12（`INGEST_UPSTREAM_RPM_AGNES`） | 覆盖 text/image/video/analyze/ingest 的全部 Agnes POST；Agnes 免费档有效约 20 次/分（设计实测），留余量给交互请求 |
| gemini | 0 = 不限（`UPSTREAM_RPM_GEMINI`） | 0（`INGEST_UPSTREAM_RPM_GEMINI`） | 按 Gemini 项目限额再设〔Gemini 实际项目限额未核实〕 |

- 交互请求拿不到令牌 → 立即 429 `upstream_throttled`（带 Retry-After、退额度）；ingest 作业则等待，直到作业时限（`IngestCallScope.WaitUntil`），等待次数记 `tb=`。
- 运行态 `/health.analyze.upstream_rpm` 可看最近一分钟用量。

### 7.3 agy 额度池的位置

- agy 不经 agnes-service、不受上述任何限额与令牌桶约束；它直接用 TtsWeb 令牌读写 reading（2.5）。
- 两处 agy（ddPC、A001 VM）同一 Google 账号、共用一个额度池〔设计〕；每日额度检查（旧总览「每日 09:00」）〔未核实〕。
- 在体系中的位置：API 路（L0/L1）是主力、几秒级；agy（L2）是兜底、几十秒到分钟级，且受 Google 额度池约束——这正是「API 为主、agy 只作 L2」的原因（旧总览 §七.5，设计一致）。Claude 不参与朗读流水线执行（设计 §1.1）。

### 7.4 其他硬上限

- 文本 ≤5000 字（TtsWeb `MAX_TEXT_CHARS`/`MAX_TEXT`、PG CHECK、agnes `IngestApi.MaxText`）；标题 ≤120；单句译文 ≤500；/api/v1 请求体 ≤512 KB；`/api/reading/ingest` 体 ≤64 KB；直调 analyze：raw ≤2000 字、enrich ≤200 句/5000 字。
- HLS 会话目录只保留最新 6 个。

---

## 8. 部署与运维

### 8.1 77B 与 ddPC 的分工

| 机器 | 承担 | 数据 |
|---|---|---|
| 77B（云端 instabox） | TtsWeb 生产（8791）、agnes-service 生产（8793）、PG（5432，仅本机） | **唯一真源**：reading 文章/版本/句子、agnes_auth 全部 |
| ddPC（家中） | TtsWeb 同源副本（播放、缓存）、agy（agent 通道 L2）、电视播放〔推屏脚本未核实〕 | 77B ready 文章的只读缓存（cache.db）；本地新存只进本地 shares（不回写） |
| A001 的 VM | agy（网页通道 L2）、L2 巡检〔设计〕 | 旧总览称有「reading 只读镜像，每日 04:27 拉取」；设计 §5 称镜像「P5 后已由 ddPC SQLite 缓存取代」——二者冲突〔未核实现状〕 |

77B 其他监听（`ss -ltn`）：8792（0.0.0.0）、8080（127.0.0.1）、5901（127.0.0.1）——不属本文档范围〔未核实用途〕。开发实例：agnes-service 8795、TtsWeb 开发副本 8796（库 `agnes_p2dev`，`tts-redesign/impl/phase2`），见 agnes-service `README.md`；本次未见其监听。

### 8.2 公网入口

- instabox 端口转发：公网地址形如 `https://<端口>-<机器ID>.instabox.tech`（ddPC 版 `api_base` 默认值即此形态，指向 77B 的 8791）。8791 与 8793 各有一个公网地址；`TTSWEB_PUBLIC_URL` 配的就是 8791 的公网根（运行态 `public_url_configured=true`）。
- 网关约束（设计实测）：约 **20 秒**掐断长请求 → 编排必须异步（202＋轮询）；`/play` 最多等 15 s 首片，处在限内。来访 IP 在服务看来是回环。
- TLS 由网关提供；两服务本身都是明文 HTTP。

### 8.3 进程与单元

| 服务 | 启动方式 | 证据 |
|---|---|---|
| agnes-service | systemd unit `agnes-service.service`：`User=user`，`WorkingDirectory=/data/workspace/agnes-service`，`EnvironmentFile=-…/.env`，`ExecStart=/data/dotnet-sdk/dotnet …/publish/AgnesService.dll`，`Restart=on-failure`，`TimeoutStopSec=20` | 仓内 unit 文件〔安装状态：本次无法执行 systemctl，未核实〕 |
| TtsWeb（77B） | `python3 tts_web.py`（venv：`tts-web/venv`），日志 `tts-web/server.log` | `server.log` 属主为 root，末次启动约 2026-11-20 07:49（与 `tts_web.py.bak-20261120-v4default` 时间一致）→ 由 operator 以 root 启动；**是否有 systemd 单元及其名称〔未核实〕** |
| PG | `postgresql.service`（agnes unit 的 `After=`） | 〔未核实单元实名〕 |
| ddPC TtsWeb、agy、巡检、镜像 | — | 〔未核实〕 |

### 8.4 重启与生效方式（含踩过的坑）

| 改了什么 | 是否需重启 | 依据 |
|---|---|---|
| `player_v*.html`、`ingest.md`、`ingest_prompt.md`、`vue.global.prod.js` | **不需要**：每次请求从盘读、`no-store`/`no-cache` | `handle_index/handle_v3/handle_v4/_md_handler/handle_vue_js` |
| `hls.min.js` | 不需重启，但客户端缓存 1 天 | `Cache-Control: public, max-age=86400` |
| `tts_web.py / store.py / ingest.py / auth.py / tokenizer.py` | **需重启 TtsWeb**（进程启动时加载；由 operator 以 root 启动，故须 operator 重启） | Python 模块常驻；`server.log` 属主 |
| `config.json`（含 `grace_until`、PG 参数）、`tokens.json`（增删/吊销令牌） | **需重启 TtsWeb** | 模块级 `CFG / STORE / AUTH / GRACE_UNTIL` 只在启动时读；`auth.py` 文档串「改配置后重启即吊销」 |
| agnes-service 代码 | `dotnet publish -c Release -o publish` ＋ `sudo systemctl restart agnes-service` | README、unit |
| agnes-service `index.html` | 需拷入 `publish/`（服务从 `AppContext.BaseDirectory` 即 publish 目录读，`SendFileAsync` 每次读盘）；拷入后无需重启 | `Program.cs: indexFile`；csproj `CopyToPublishDirectory` |
| agnes-service `.env` 任何键 | **需重启**（EnvironmentFile 冻结启动时已有键；`Settings` 环境变量优先） | `.env.example` 第 63–64 行、PLAN §1.6；旧说法「热重读免重启」只对启动时为空的键成立 |
| reading / agnes_auth DDL | 先迁移再发布代码（只扩不回退） | `auth_schema.sql` 各段注释、PLAN §6 |
| 熔断、每分钟窗口、并发计数、令牌桶 | 重启即清零（内存态） | `Circuit.cs`、`LimitService.cs`、`UpstreamLimiter` |
| 运行中的 ingest 作业 | 重启后 `running → retry` 立即重跑（整篇） | `IngestStore.RecoverRunning` |

### 8.5 备份口径

- 代码与配置改动前的就地备份：`<文件>.bak-YYYYMMDD-<标签>`（如 `tts_web.py.bak-20261120-v4default`、`Analyze.cs.bak-20261119-gemini-lite`、`auth_schema.sql.bak-20261119-task6p2`）；`.env.bak-*` 保持 0600。
- agnes-service 发布前整包：`publish.bak-YYYYMMDD-<标签>.tar`；回滚 = 解回上一个 tar（PLAN §6「回滚：代码回退上一 publish 备份；DDL 只扩不回退」）。
- TtsWeb 依赖：`venv.bak-p2`（第二期前的 venv 备份）。
- PG 数据库（reading / agnes_auth）的定期备份机制〔未核实〕：本次来源中未见 pg_dump 任务；旧总览所述 A001 VM 的每日镜像能否视为备份见 8.1〔未核实〕。
- 令牌明文备份：`tts-web/tokens/<name>.txt`（0600）。

### 8.6 巡检与演练节奏

| 事项 | 频率 | 执行者 | 依据 |
|---|---|---|---|
| ingest 扫描（到期重试、queued 入队） | 每 60 s | agnes-service 后台 | `INGEST_SCAN_S` |
| L2 自动对账 | 每 3600 s ＋每次拉清单时 | agnes-service | `INGEST_RECONCILE_S`、`ListL2` |
| 过期作业清理 / call_log 90 天清理 | 每日 / 每 6 h 至多一次 | agnes-service | `IngestWorker`、`CallLog.Prune` |
| ddPC 预热 | 启动＋每 `preheat_seconds`（默认 1 h） | ddPC TtsWeb | `_preheat_loop` |
| L2 巡检 | 每小时，08:00–24:00 JST | A001 | 设计 DF-5〔任务实体未核实〕 |
| L2 演练 | 每月 1 次 | — | 设计 DF-5、`ingest.md` |
| agy 额度检查 | 每日（旧总览「09:00」） | — | 〔未核实〕 |
| L0 抽查（前 20 篇） | 上线初期每日汇总 | 巡检 | PLAN §2.3〔代码无对应，未核实是否在做〕 |

### 8.7 常见故障排查入口

| 症状 | 先看 | 判断要点 |
|---|---|---|
| 页面打不开 / 播放 504 | `curl 127.0.0.1:8791/health`；`tts-web/server.log` | `/play` 504=15 s 内没出首片（Edge TTS 连不上、代理问题、ffmpeg 缺失）；`HLS 会话失败` 日志 |
| 播放到中途停 / 高亮偏差 | 浏览器类型；m3u8 是否有 ENDLIST | iOS/webOS 走原生 HLS；高亮是估算（3.4） |
| 打开短链进了「简易模式」 | `GET /v3/t/<id>` 是否有 `sentences` | 77B：PG 不可用回落 SQLite；ddPC：缓存未命中（看预热日志「preheat ok/partial」） |
| 选文列表不可用 | `GET /v3/articles` 状态码 | 77B 503=PG 不可用；ddPC 503=缓存为空等待预热 |
| 网页保存 401/403 | `/api/auth/me` 的 `reading_ingest` | 403 `ingest_forbidden`=账号未开通 web；423=账号被锁 |
| 保存后长期 partial | `GET /api/reading/jobs/{id}`；`GET /api/admin/calls`（kind=ingest） | `status=retry`（看 `next_retry_at`、`error_code`）、`l2`（看 `l2_reason`、`trail`）；`/health.analyze.circuit` 是否有熔断；`upstream_rpm` 是否打满 |
| 作业卡 T 态 | 作业 `error_code=ttsweb_unavailable`、`t_attempts` | TtsWeb 进程或 PG；24 h 后 `failed_ttsweb` |
| 作业转 L2 `ttsweb_auth_failed` | `/health.ingest.ttsweb_token`；TtsWeb tokens.json 是否仍有 `agnes-service` | 令牌被吊销/不一致；两边改后都要重启 |
| agent commit 被拒 | 错误体 `{code, message, hint, details}` | 按 `ingest.md` 错误码表；`SPLIT_MISMATCH` 用 preview 原样句子；缺读音按 `details.missing` 补 |
| /share 401「宽限期已过」 | 调用是否带令牌 | `grace_until=2026-11-19` 已过 |
| 改了 .env/配置不生效 | 是否重启 | 8.4 |

数据核对常用只读查询：`reading.articles` 按 `status, added_via` 计数；`agnes_auth.ingest_jobs` 按 `status, channel` 计数；`pg_constraint` 看 `sentences_tr_via_check` / `articles_added_via_check` 是否为新版。

---

## 9. 文件索引

| 机器 | 路径 | 作用 |
|---|---|---|
| 77B | `/data/workspace/tts-web/tts_web.py` | TtsWeb 主程序（路由、Edge TTS、HLS、/share、/v3、/api/v1） |
| 77B | `/data/workspace/tts-web/store.py` | reading 存取（PG＋SQLite 回落）、commit/replace/share 事务 |
| 77B | `/data/workspace/tts-web/ingest.py` | 入库契约校验、readings_check |
| 77B | `/data/workspace/tts-web/tokenizer.py` | 规范化、分句、fugashi 骨架、数字合并、ruby |
| 77B | `/data/workspace/tts-web/auth.py` | TtsWeb 令牌鉴权与失败限流 |
| 77B | `/data/workspace/tts-web/config.json` | TtsWeb 配置（host/port/pg/sqlite/tokens_file/grace_until） |
| 77B | `/data/workspace/tts-web/tokens.json`、`tokens/<name>.txt` | 令牌哈希登记 / 明文备份（0600，勿读入文档） |
| 77B | `/data/workspace/tts-web/ttsweb.db` | SQLite 回落库（shares 表） |
| 77B | `/data/workspace/tts-web/hls/` | HLS 会话目录（保留最新 6 个） |
| 77B | `/data/workspace/tts-web/player_v4.html`（默认）、`player_v3.html`、`player_v2.html`、`player.html` | 播放页各版 |
| 77B | `/data/workspace/tts-web/vue.global.prod.js`、`hls.min.js` | 前端库 |
| 77B | `/data/workspace/tts-web/ingest.md`、`ingest_prompt.md` | 执行者契约与翻译模板（HTTP 下发） |
| 77B | `/data/workspace/tts-web/server.log` | TtsWeb 运行日志（root 属主） |
| 77B | `/data/workspace/tts-web/venv/` | Python 运行环境 |
| 77B | `/data/workspace/tts-redesign/impl/reading_rw.pw` | `reading_rw` 口令文件路径（config 引用；勿读入文档） |
| 77B | `/data/workspace/agnes-service/Program.cs` | 宿主、路由、Generate 记账、CLI 分派 |
| 77B | `/data/workspace/agnes-service/src/IngestService.cs` | 入库编排（端点、作业表、worker、Pipeline、对账） |
| 77B | `/data/workspace/agnes-service/src/Analyze.cs` | provider 定义、prompt、校验、L1 引擎 |
| 77B | `/data/workspace/agnes-service/src/TtsWebClient.cs` | 调 TtsWeb 客户端与失败分类 |
| 77B | `/data/workspace/agnes-service/src/FixTriage.cs` | fixes 本地分流 |
| 77B | `/data/workspace/agnes-service/src/Circuit.cs` | 熔断器 |
| 77B | `/data/workspace/agnes-service/src/Upstream.cs` | 上游调用、令牌桶、脱敏 |
| 77B | `/data/workspace/agnes-service/src/LimitService.cs`、`CallLog.cs` | 限额、调用记录 |
| 77B | `/data/workspace/agnes-service/src/AuthService.cs`、`AccountService.cs`、`Passwords.cs` | 认证、改密、口令哈希 |
| 77B | `/data/workspace/agnes-service/src/AdminApi.cs`、`AuditService.cs`、`UserCli.cs` | 管理 API、审计、运维 CLI |
| 77B | `/data/workspace/agnes-service/src/Settings.cs`、`Db.cs`、`Agnes.cs`、`ApiError.cs` | 配置、DB、Agnes 客户端、错误体 |
| 77B | `/data/workspace/agnes-service/index.html` | 管理页＋「添加文章」（发布后在 `publish/index.html`） |
| 77B | `/data/workspace/agnes-service/auth_schema.sql` | agnes_auth 幂等 DDL（含 ingest_jobs） |
| 77B | `/data/workspace/agnes-service/.env.example` | 配置键名与注释（`.env` 本体 0600，勿读入文档） |
| 77B | `/data/workspace/agnes-service/agnes-service.service`、`install.sh`、`smoke.sh` | systemd 单元、安装、冒烟 |
| 77B | `/data/workspace/agnes-service/publish/` | 运行中的发布目录；`publish.bak-*.tar` 为发布前备份 |
| 77B | `/data/workspace/agnes-service/DESIGN.md`、`TASK6-PHASE2-PLAN.md`、`PHASE2-AUTH-DESIGN.md`、`PHASE2-IMPL-REPORT.md`、`README.md` | 设计背景与实施报告 |
| 77B | `/data/workspace/agnes-service/tests/` | MockAgnes、开发实例脚本（8795）、验收脚本 |
| 77B | `/data/workspace/tts-redesign/archdoc/refs/reading_ddl.sql` | reading DDL（**第一期版本，已落后于生产**） |
| 77B | `/data/workspace/tts-redesign/archdoc/refs/READING-FLOW-OVERVIEW.md` | 旧总览（2026-11-19 晚） |
| ddPC | `ddpc_tts_web.py`（参考件：`refs/ddpc_tts_web.py`）、`ddpc_cache.py`、`config.json`、`cache.db`、`ttsweb.db` | ddPC 版 TtsWeb、缓存模块、配置、缓存库、本地 shares〔ddPC 上实际路径未核实〕 |
| 「本机」muse-tts | `tts_service.py`（参考件：`refs/tts_service.py`） | 127.0.0.1:8790 MP3 流式 TTS〔运行机器未核实〕 |

---

## 10. 与旧文档的出入

旧文档指 `refs/READING-FLOW-OVERVIEW.md`（下称「旧总览」，2026-11-19 晚），兼及 `DESIGN.md`、`TASK6-PHASE2-PLAN.md`、`README.md` 中与代码冲突的说法。格式：**旧说法 → 代码事实（出处）**。

1. 旧总览 §二/§四：77B 默认页为 v2（ddPC 已切 v3）→ 77B 与 ddPC 默认页均为 **v4**（`tts_web.py: handle_index`、`ddpc_tts_web.py: handle_index`，2026-11-20）。
2. 旧总览 §三.5：播放页 v3 → 现行默认 v4；v4 只改视觉，脚本与 v3 逐字相同（`player_v4.html` 注释【九】–【十一】）。
3. 旧总览 §四端点表缺 `/v4` 与 `POST /api/v1/ingest/readings_check` → 二者均已上线（`make_app`）。
4. 旧总览 §四：`/v3/articles` 仅 ready → **ready＋partial**，并带 `status`（`store.py: list_public`）。ddPC 版仍只含 ready（预热只收 ready）。
5. 旧总览 §三.1：网页通道「尚未上线」→ 已上线：8793 `/add`＋`POST /api/reading/ingest` web 分支（`Program.cs`、`IngestApi.Caller`）；运行态已有 1 篇 `added_via=web` 文章与 1 个 done 的 web 作业。
6. 旧总览 §五「added_by 口径：网页记登录名」→ 网页通道 `added_by = added_via = "web"`，登录名只记在 agnes 侧（`IngestApi.Caller` 返回 `(u,"web","web")`；PLAN §1.5「归属记账」）。
7. 旧总览 §六「入口 `POST /api/reading/ingest`（agent 令牌）」→ 凭据是 **MuseAgent 账号的 8793 JWT**（`reading_ingest='agent'`＋`muse_id`），不是 TtsWeb 令牌（`IngestApi.Caller`）。
8. 旧总览 §三.3「执行者只回 `fixes[{seq, tok_i, reading}]`」→ commit 契约里读音修正是 `sentences[].readings[{tok, s, r}]`（`ingest.py: plan_commit`）；模型输出是 `fixes[{i,s,r}]`（`Analyze.ValidateEnrich`）；`tok_i/reading` 只是直调 `/api/analyze` 的响应字段名（`Analyze.SentencesJson`）。
9. 旧总览 §三.4「校验不过保持 partial」→ commit 校验不过**直接报错、什么都不写**；partial 来自 `allow_partial:true` 时仍缺译文/读音（`plan_commit`）。
10. 旧总览 §三.3 分级表「L2：API 结果校验不过/质量不行/熔断时才上」→ 网页通道先排 **3 次整篇延迟重试（15 分/1 小时/4 小时）** 才转 L2；agent 通道不重试、直接 L2（`IngestWorker.NotReady`）。另有 `ttsweb_auth_failed`、commit 被拒等直接转 L2 的情形（4.3）。
11. 旧总览 §六「熔断：429…403 配额 60 分钟；质量熔断只统计不自动熔断」→ 另有：瞬时类（10 分钟内连续 3 次，5 分钟起翻倍至 60 分钟）、鉴权类（整 provider 6 h）、**文章级质量熔断 E7 会自动打开 24 h**（连续 3 篇校验失败）；「只统计」只针对 fixes 质量（`Circuit.cs`）。
12. 旧总览 §五「provider：Gemini 主、Agnes 兜底」→ 运行态如此，但这是 `.env` 设的；**代码缺省是 agnes 优先**（`Providers.DefaultProvider` 缺省 `"agnes"`、`Order` 缺省 `"agnes,gemini"`）。`.env` 丢失时会回到 agnes 主。
13. 旧总览 §八「TtsWeb 侧 §5 未动、实施顺延」→ 已实施：`added_via`（`ingest.ADDED_VIA`、生产列与 CHECK）、`tr_via` 放宽（`TR_VIA`、生产 CHECK）、readings_check、`/v3/t` 带 status、`/v3/articles` 含 partial。
14. 旧总览 §三.1「agent 调 TtsWeb `POST /share`（令牌认证）」→ 仍成立；但另须注意 `grace_until` 生产值已是 **2026-11-19**（代码缺省与 PLAN §5 写的 2026-12-02 都不是现值），无令牌 /share 现返回 401（`config.json`、`handle_share`）。
15. 旧总览 §二「ddPC：每小时拉取＋服务内预热」→ 代码只有服务内循环预热（启动一轮＋每 `preheat_seconds`，默认 3600 s），无独立拉取任务；预热只收 ready、整轮成功才换列表快照（`_preheat_loop / preheat_once`）。ddPC 实际 `preheat_seconds` 配置值〔未核实〕。
16. 旧总览 §二「本机 VM：reading 只读镜像（每日 04:27 从 77B 拉）」↔ PLAN §5「本机镜像在 P5 后已由 ddPC SQLite 缓存取代」——两份文档互相矛盾，代码无镜像任务〔未核实〕。
17. 旧总览 §三.5「推电视：ddPC 脚本打开短链页，播放前 setvol 12」→ 本次所有代码来源中无此脚本〔未核实〕。
18. 旧总览 §五「refresh Cookie 7 天轮换」→ 另有家族上限 30 天、10 s 轮换宽限与 `refresh_rotated`、重放即吊销整个家族（`AuthService.Refresh`）。
19. 旧总览 §五「调用记录 90 天清」→ 一致；补充：ingest 作业只写一行、结束 UPDATE（`CallLog.WriteReturningId / Update`）。
20. 旧总览正文中含「[truncated 3072 chars]」字样，§六「翻译」一条后的约 3 KB 原文缺失——该参考件本身不完整，缺失部分无法对照。
21. `refs/reading_ddl.sql`：`articles` 无 `added_via` 列、`sentences_tr_via_check` 只有 6 个值 → 生产已有 `added_via text DEFAULT 'legacy'` 与 `CHECK (added_via IN ('agent','web','legacy'))`，`tr_via` 白名单含 `gemini-api`、`agnes-api`（运行态 `pg_constraint`）。
22. `tts_web.py` 模块文档串（同源遗留）：「本机 127.0.0.1:8790」「每段 ≤500 字」「TLS 用系统 CA 包」→ 代码 `HOST=0.0.0.0`、`PORT=8791`（生产取 config 8791）、`SEGMENT_MAX_CHARS=2000`、`ssl.create_default_context()`（无显式 CA 文件）。
23. `player_v4.html` 注释【四】：「/v3/articles 只含 ready」→ 77B 含 partial（同第 4 条）。
24. `DESIGN.md`（rev 3 认证说明）与 `README.md` 第 51–53 行：「`.env` 改动免重启／AGNES_API_KEY 后补即热生效」→ 在 systemd `EnvironmentFile=` 下，启动时已有的键被冻结，**任何键改后都要重启**（`Settings.Get` 环境优先；`.env.example` 第 63–64 行、PLAN §1.6 已更正）。
25. `TASK6-PHASE2-PLAN.md §1.2`：agent 映射「`muse_id` 或 `.env INGEST_AGENT_MAP` 二选一」→ 代码只用 `users.muse_id`（`AuthService.LoadState`、`IngestApi.Caller`）。
26. `TASK6-PHASE2-PLAN.md §2.1`：启动时把未完作业（按 stage）重新入队 → 代码按 **status**：`running → retry` 并立即到期，`queued` 由扫描器入队（`IngestStore.RecoverRunning`、`ExecuteAsync`）。
27. `TASK6-PHASE2-PLAN.md §2.2`：鉴权类熔断「每天试探 ≤6 次」→ 冷却 6 h，实际每天至多约 4 次试探（`Circuit.cs` 注释 `≤ 6`，以冷却值计为 4）。
28. `ingest.md` 列出 warning `SPLIT_NONSTANDARD`（`ingest.py` 模块串同）→ 现行代码从不产生该 warning（只有 `TOKENIZER_FAILED`、`TR_SUSPECT`）。

---

## 11. 代码观察到的风险与边角（附）

以下均由读代码得出，未做运行验证，供后续评估；不代表已发生的故障。

1. **PG 不可用时 /share 落 SQLite 的文章在 PG 恢复后不可见**：`handle_share` 回落写 SQLite（新 ID），但 `/t`、`/v3/t` 只在**抛 `PgUnavailable` 时**才查 SQLite；PG 恢复后查 PG 得 `None` → 404。若这是 agnes-service 代调，作业随后 `GET /api/v1/articles/{id}` 在 PG 恢复后得 404 → `gone` → 作业 `cancelled/article_gone`。
2. **HLS 会话只保留最新 6 个**：第 7 个 `/play` 会删掉最旧会话目录，即便它仍在播放（多人/多设备同时听时可能中断）。
3. **合成端点无鉴权**：`/play`、`/tts*` 任何能访问 8791 公网地址的人都可用来合成 ≤5000 字音频（消耗出网与 CPU）。
4. **TtsWeb 鉴权限流取 `X-Forwarded-For` 最左项**：客户端可伪造该头规避或嫁祸封禁；agnes-service 则默认取对端、`TRUST_PROXY=1` 才取最右项，两边口径不同。
5. **`/v3/articles` 公开列出全部 ready/partial 文章的标题与保存者**：短链即凭据的前提下，列表等于公开目录。
6. **熔断、令牌桶、每分钟窗口都在内存**：重启后清零（设计已接受）。
7. **provider 次序依赖 `.env`**：代码缺省为 agnes 优先（第 10 节第 12 条）。
8. **v4 的 JS 文案与存储键仍是 v3**（`document.title` 后缀「日语朗读页 v3」、`ttsv3.*`）：设计如此，排查时勿误判为加载了 v3。
9. **`status='raw'` 只存在于 DDL 默认值**：现行写路径不会产生 raw；若库中出现 raw，应来自迁移或手工写入。
