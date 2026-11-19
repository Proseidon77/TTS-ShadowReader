# TASK6 第二期方案审核报告（PHASE2-REVIEW）

审核对象：`TASK6-PHASE2-PLAN.md`（2026-11-19 待审核稿）
审核日期：2026-11-19
审核方式：对照 agnes-service 源码（`Program.cs`、`src/*.cs`、`auth_schema.sql`、`.env.example`、`agnes-service.service`、`tests/`）、TtsWeb 现行代码（`/data/workspace/tts-web/`，即 8791 在跑的那份）、朗读设计与 DDL（`/data/workspace/tts-redesign/`）做核对；对 `db77database`（agnes_auth、reading）和 `dbapi` 库只做了只读查询。没有改任何代码或方案稿，没有 publish，也没有重启服务。

---

## 0. 结论

**修改后可行。** 大方向没有问题：异步作业、share-first、只判定不调度 L2、Gemini 为主。但方案稿有 4 处 blocker，不改无法实施：
① 作业只放进程内存，扛不住每次 publish 的重启和最长 4 小时的延迟重试；
② TtsWeb 侧缺 agnes-service 的服务令牌，网页通道 `added_by=ReaderNew` 也不在任何代填白名单里，按现契约 /share 会被 403；
③ 分块翻译没有带全文上下文，违反朗读侧「全篇上下文翻译」的硬规矩；
④ A 案（跨源 CORS 登录）下，/add 页与 8793 主站共用同一个 refresh Cookie，会互相顶掉登录。

认证归属：**推荐 C 案（TtsWeb 同源透传反代）**，不推荐 B 案。理由见 §2。

---

## 1. 修改意见（P2R-n）

严重程度：**blocker**＝不改无法实施或必出错；**should**＝应改，不改会留明显坑；**nice**＝建议。

### A. 作业模型与记账

**P2R-1 作业必须落库，不能只放进程内存**（blocker）
- 问题：§2.1 只写了「作业保留期与清理规则」，没写存哪里。§2.4 的延迟重试最长 +4 h，§2.5 还要提供 L2 待办清单接口；§2.1 的「按文本哈希加锁」也是进程内锁。这些都放内存的话，每次 publish/重启（§6 的发布闸门每次都会重启）都会把在途作业、待重试队列和 L2 待办一起清掉。
- 证据：现有服务的限额窗口、熔断状态都只在内存里（`src/LimitService.cs:58-70`），作业没有任何持久层；`auth_schema.sql` 也没有作业表。
- 建议：在 `auth_schema.sql` 增加 `agnes_auth.ingest_jobs`，按第一期的幂等迁移写法。字段至少要有：id uuid、user_id、username、channel、added_by、share_id、base_version、stage、status、provider_model、chunks jsonb、trail、error_code、l2_reason、attempt、next_retry_at、created_at、updated_at、expires_at。服务启动时把 `stage∈{share,preview,enrich,commit}` 的作业重新入队，或者标为 `retry`；延迟重试用 `next_retry_at` 加一个后台扫描器（`BackgroundService`，每分钟扫一次）。「同文只跑一份」改用部分唯一索引，例如 `UNIQUE (share_id) WHERE status IN ('queued','running','retry')`，这样重启后也有效。见 P2R-14。

**P2R-2 异步作业不能套用现有 `Generate` 管线的 Ticket 语义**（should）
- 问题：`Generate` 的 Ticket 在请求结束时释放（`Program.cs:213` `await using var _ticket`），并发占用（每账号 2、全局 `MAX_CONCURRENT=6`）也跟着请求走。如果在作业期间一直持有 Ticket，一篇入库最长可能占住全局槽位好几分钟，挤掉别人的 text/image 请求；ReaderNew 连续保存两篇时，第三篇会直接 429。如果不持有，并发就没有约束。
- 证据：`src/LimitService.cs:204-214`（并发检查）、`Program.cs:195-242`（call_log 在请求结束时写）。
- 建议：在 POST 时同步执行 `Admit`，让 429 和每日额度当场返回给调用方；准入成功后**马上 Dispose 掉 Ticket**，只计「日额度＋每分钟」。作业并发交给独立的 worker 池（建议 `INGEST_WORKERS=2`），不占 `MAX_CONCURRENT`。call_log 在 POST 时写一行（outcome=`accepted`），作业结束时用 `UPDATE` 回填 model/outcome/duration/detail；也可以结束时再写一行，但这一行不能重复计额度。重试和半开试探属于系统发起，只记 detail（`retry`/`probe`），不计用户额度。这一条和 `TASK6-FALLBACK-DESIGN.md` §7.1 的原口径一致。

**P2R-3 kind 选独立的 `ingest`，连带改动要列全**（should；见 §3 的 D5）
- 证据：`usage_daily` 和 `call_log` 的 kind 都有 CHECK 约束（`auth_schema.sql:91,124`）；`LimitService.Kinds` 和 `View` 是写死的（`src/LimitService.cs:28,316-331`）；管理页筛选也写死了（`src/AdminApi.cs:498-500`）。
- 建议：选 `ingest`。迁移照抄第一期的 DO 块（`auth_schema.sql:142-160`），把 `'ingest'` 加进两处 CHECK。代码要改：`Kinds`、`View`（used 和 counted_endpoints）、`AdminApi.ListCalls` 的 kind 白名单、index.html 管理页的 kind 下拉。

**P2R-4 上游令牌桶必须放在 `Upstream` 层，覆盖所有 Agnes 调用**（should）
- 问题：§2.2 写的是「编排内须有全局上游令牌桶」。但 Agnes 的 20 次/分钟是同一类 Key 共享的池子，`/api/text`、`/api/image`、`/api/video` 和直调 `/api/analyze` 都走同一把 Key。只在编排里节流，照样会出现「用户请求没超限、上游先被打穿」。
- 建议：按 provider 设令牌桶（`UPSTREAM_RPM_AGNES=18`、`UPSTREAM_RPM_GEMINI=按项目限额`），放在 `Upstream.Call` 入口。编排作业只拿一部分配额（例如 agnes 每分钟最多 12 次），给交互请求留余量。拿不到令牌时，编排排队等待，交互请求直接快速 429，不让前台请求等上几十秒。

**P2R-5 `ANALYZE_*` 不能热生效的真实原因要写对，新参数同样受影响**（should）
- 问题：§1.6 说「`ANALYZE_DEFAULT_PROVIDER` 不吃热重读，API Key 类键热生效」。根因其实是 systemd 的 `EnvironmentFile=` 在启动时把 `.env` 灌进了进程环境，而 `Settings.Get` 是环境变量优先（`agnes-service.service:15`、`src/Settings.cs:87-90`）。所以**启动时 `.env` 里已有的所有键都会被冻结**。Key 看上去能热生效，只是因为启动时它是空的；Key 一旦配上，以后轮换也必须重启。第二期要新增的 `CHUNK_TIMEOUT_S`、`INGEST_DEADLINE_S` 等参数同样会被冻结。
- 建议：方案稿和 README 改成上面的口径。如果想要真正的热生效，可以删掉 unit 里的 `EnvironmentFile=` 一行，因为应用本身会读 `.env`；但 `--create-user` CLI 和 systemd 的其他用法要一起回归。删不删由用户决定。

### B. L1 降级执行

**P2R-6 分块翻译必须带全文上下文**（blocker）
- 问题：`TASK6-FALLBACK-DESIGN.md` 的 A5 已经定为「必须先定」：「每块请求都附上全文纯文本（≤5000 字）＋随文累积的译名表」。依据是朗读设计稿修订注记 4/5（`tts-redesign/design_proposal.md:16-17,569`）的「全篇上下文翻译」硬规矩。现有 enrich 的投影只发本块（`src/Analyze.cs:339-357`），第二期方案稿对此一字未提。Agnes 改成 8–10 句一块以后，块数更多，上下文断裂会更严重。
- 建议：enrich 的 user 消息加一段 `context.full_text`（全文纯文本，只读、不要求输出）。译名表第一版可以不做：块之间并行，累积译名表要求串行，会拖慢速度。prompt_version 升到 `enrich-v2`。代价是输入 token 大约 ×(块数)，Gemini lite 可以忽略；Agnes 每分钟 20 次的限制卡的是次数不是 token，影响也不大。

**P2R-7 按 provider 分块需要「换 provider 时重新分块」**（should）
- 问题：现在是请求开始时按全局 20 句/1500 字分一次块（`src/Analyze.cs:769`、`501-519`），一块在 Gemini 失败后，原样交给 Agnes 去做。§2.2 要求 Agnes 的块更小，就必须在候选切换到另一个 provider 时把当前块再拆一次。现有的「对半拆」只在 `finish_reason=length` 时才触发（`src/Analyze.cs:648-650`、`789-794`）。
- 建议：`RunUnit` 在 `ci` 跨 provider 时检查块大小，超过目标 provider 的上限就返回 `Split=true` 并带上 `CandIndex`，复用现有的拆块路径。块大小参数挂在 `ProviderDef` 上，例如 `ChunkSents`、`ChunkChars`、`CallTimeoutS`。

**P2R-8 熔断的计数主体与 Gemini 429 的分类**（should；见 §3 的 DF-6）
- 问题：§2.2 对 429 的处理沿用设计稿（配额类熔 1 次 60 分钟）。但现有代码把 403 和 429 都当成「换下一个候选」（`src/Analyze.cs:572`）。Gemini 免费档的 429 既可能是每分钟限额（几十秒就恢复），也可能是每日限额，一律熔 60 分钟太粗。
- 建议：429 优先读响应里的 `retryDelay`（Gemini 的 RetryInfo）或 `Retry-After`，按它来冷却；没有这类信息时，429 冷却 2 分钟，403 带 quota 字样的冷却 60 分钟。熔断状态放内存可以接受（设计稿 §2.2 说过「重启后闭合，最坏多试一次」），但作业要落库（P2R-1）。

### C. fixes 核对层（§2.3）

**P2R-9 核对层放在 TtsWeb 侧，agnes-service 先做本地分流**（should，给出落地形态）
- 现状：TtsWeb 用的是**完整版 UniDic**（`venv/.../unidic/dicdir`：`sys.dic` 243 MB、`matrix.bin` 481 MB、`model.bin` 84 MB），由 `fugashi.Tagger()` 常驻在 TtsWeb 进程里（`tts-web/tokenizer.py:36-40`）。.NET 侧没有日语词典，要再加载一份 800 MB 的词典不现实。另外 pip 版 unidic 不带 `lex.csv`，导不出一张「表层形→读音集合」的静态表。
- 建议分两层实现：
  1. **agnes-service 本地分流（零依赖，第二期必做）**：preview 返回的 toks 里已经有 UniDic 的首选读音 `r`。把 fix 分成两类：
     - **补齐类**（原来 `r == null`，或者 `unk=true`，或者 `pos=num`）：现有校验（平假名、送假名覆盖，见 `src/Analyze.cs:466-469`）通过就采纳。这是数字读音的主要收益来源。
     - **改写类**（原来 `r` 非空）：默认**不采纳**，记为可疑。只有两种情况放行：数字＋量词的音变命中本地量词表（`一/六/八/十/百 + 本/匹/杯/分/発/階/百/千…`，例如 `いっ/ぽん`），或者通过下面第 2 层的词典佐证。
     - §3 的 3 条实测误改（大雪山「大」、日本一「日本」）都属于改写类，这一层就能全部挡掉。
  2. **TtsWeb 词典佐证接口（由朗读项目实施，列入 §5）**：新增 `POST /api/v1/ingest/readings_check`（read 权限），入参 `[{sentence, tok, s, r}]`。服务端对该句做 `nbest`（N≈20），收集与该 token 边界对齐的各条路径给出的读音（片假名转平假名），也可以加上对 `s` 单独分词的结果；返回 `{supported: bool, candidates: [...]}`。代价：TtsWeb 侧约 60 行代码，每篇 1 次本机 HTTP 调用，15 句的文章是毫秒级。
- 结果写进作业和 call_log detail 时用 `fx=a{applied}/r{rejected}/s{suspect}`，接口不可用时退回到第 1 层。
- 不要把这个核对塞进 TtsWeb 的 commit 校验：commit 也服务 L2 的 agy 和人工，人工补的熟字訓、专名读音本来就可能超出词典。

### D. TtsWeb 契约与 reading DDL（§5、DF-11、DF-12）

**P2R-10 服务令牌和网页通道的代填规则缺失**（blocker）
- 证据：`tts-web/tokens.json` 现在只有 A001、A002、A003、ddpc-claude、77b-claude、ddpc-cache，**没有 agnes-service 令牌**。`resolve_added_by` 只放行 `can_act_for` 里的名字（`tts-web/auth.py:63-71`），而 DF-11 只写了 `{A001,A002,A003}`。网页通道由 agnes-service 代 ReaderNew 执行 /share（`by=ReaderNew`）时会被 403 `AUTH_FAILED`。设计稿 R2/R3 原有的 `web_channel:true` 规则，第二期方案稿 §5 没有沿用。
- 建议：§5 和 DF-11 改为「登记 `agnes-service` 写令牌：`can_act_for={A001,A002,A003}`＋`web_channel:true`」。`resolve_added_by` 增加一条规则：`added_via=web` 时令牌必须有 `web_channel`，并且 `added_by` 满足 `^[a-zA-Z0-9_.-]{3,32}$`、不匹配 `^A\d{3}$`。也可以简化成把 `ReaderNew` 直接加进 `can_act_for`；但以后再开一个网页账号就要改 TtsWeb 配置，所以推荐前一种。tokens.json 改了要重启 TtsWeb（`auth.py:3-4`）。agnes-service 侧的 `TTSWEB_TOKEN` 已经在脱敏名单里（`src/Upstream.cs:18`），不需要再改。

**P2R-11 `added_via` 不只是 DDL，/share 和 commit 都要接这个参数**（should）
- 证据：`reading.articles` 现在没有 `added_via` 列（只读核对了 14 列）；`handle_share` 的 meta 不收这个字段（`tts-web/tts_web.py:583-585`）；`store.share` 和 `store.commit` 的 INSERT 里也没有（`store.py:207-211`、`265-269`）。
- 建议：§5 补上「/share 与 commit 接受 `added_via`（agent|web），缺省按令牌推断：A00x 和驻地令牌为 agent，旧调用为 NULL」。replace 不改 `added_via`。

**P2R-12 tr_via 扩值是改 CHECK 约束，不是加列**（should）
- 证据：现行约束是 `sentences_tr_via_check`，取值为 self、claude-code、agy-gemini、human、import、provided（只读查询的 `pg_constraint` 结果，与 `reading_ddl.sql:91` 一致）；应用层还有一份白名单 `ingest.py:15 TR_VIA`。`reading_ddl.sql` 用的是 `CREATE TABLE IF NOT EXISTS`，重跑不会改 CHECK。
- 建议：朗读侧要写一个 DO 块迁移（DROP/ADD 同名约束，照 `auth_schema.sql:142-160` 的写法），同时改 `ingest.py` 的 `TR_VIA`。§6 第 4 条「DDL 只扩不改」可以保留这个意思，但措辞改成「CHECK 只放宽、不收紧」。设计稿原来说的「三处 DDL」中，「本机镜像库」在 P5 之后已经由 ddPC 的 SQLite 缓存取代（`impl/ddpc-p5/ddpc_cache.py`），请朗读侧确认实际是几处。

**P2R-13 replace 提交要回传原有的 title、lang、rate**（should）
- 证据：`plan_commit` 要求必须有 `title`（`ingest.py:93`）；`_replace` 会用请求里的值覆盖 title、lang、rate（`store.py:251-253`）。agent 通道给出的 share_id 可能是无标题的 /share 文章，这时 commit 会报 `BODY_INVALID`；如果没把 rate 原样带回，还会被重置成 `-20%`。
- 建议：编排在 commit 前先 `GET /api/v1/articles/{id}`，把 title、lang、rate、current_version 原样带回。标题为空时由 agnes-service 用「正文前 20 字」兜底（写进 versions.tool），或者请朗读侧让 replace 允许不传 title、保留原值。/add 页把标题设为必填。

**P2R-14 「同文只跑一份」用 share_id 作锁键**（nice）
- 理由：TtsWeb 的去重用的是**规范化后**的原文哈希（NFKC、换行统一，见 `tokenizer.py` 的 `normalize`；部分唯一索引见 `reading_ddl.sql:46`）。agnes-service 自己对原文算哈希会和它不一致。share-first 之后，/share 返回的 share_id 就是天然的去重键，再配合 P2R-1 的部分唯一索引即可，不必在 .NET 里复刻 normalize。

**P2R-15 L2 回填改成「自动对账」为主**（should）
- 问题：§2.5 要求执行完 L2 的人再到 agnes-service 回填。执行 L2 的是 ddPC 上的 agy，回填就意味着 ddPC 上还得再放一套 agnes-service 的 JWT 凭据。
- 建议：L2 清单接口每次被拉取时、以及后台扫描器每小时一次，对每条待办 `GET /api/v1/articles/{id}`：`status=ready` 且 `current_version > base_version` 就自动关闭，并记录新版本号和 `tr_via`。回填接口保留，只作为可选项（用于报告失败原因）。

**P2R-16 agent 通道的凭据要写清楚，不允许转交 TtsWeb 令牌**（should）
- 问题：§1.2 的「编排端点的 agent 通道仍以 TtsWeb 令牌定 added_by」和 §2.1 的「通道凭据（agent 令牌 / 网页 JWT）」可以读成：agent 把自己的 TtsWeb 令牌交给 agnes-service。这等于转手凭据。
- 建议：明确 agent 通道 = MuseAgent 账号的 JWT。agent 账号到 A00x 的映射由 agnes-service 固定：users 表加 `muse_id text CHECK (muse_id ~ '^A\d{3}$')`，或者在 .env 里写 `INGEST_AGENT_MAP`。如果 agent 已经自己 /share 过（用的是它自己的令牌，added_by 已经定好），就只提交 share_id，agnes-service 只做 replace，replace 不改 added_by。

**P2R-17 `reading_ingest` 的落地方式**（should）
- DDL：`ALTER TABLE agnes_auth.users ADD COLUMN IF NOT EXISTS reading_ingest text NOT NULL DEFAULT 'none' CHECK (reading_ingest IN ('none','web','agent'))`。用三值而不是布尔，因为 MuseAgent1/2 也需要 `agent` 权限；§1.5 的「写权限只给 ReaderNew」应改为「web 写权限只给 ReaderNew，agent 通道给 MuseAgent1/2」。
- 鉴权：把这个字段放进 `AuthService.UserState`（`src/AuthService.cs:163-201`，有 10 秒缓存），不要每个请求再查一次库。端点在**准入前**判定：匿名 401，`none` 403 `ingest_forbidden`，不扣额度。
- AdminApi：要扩，改动量小。`PatchUser` 的 `RejectUnknown` 加上该字段（`src/AdminApi.cs:310`），`ListUsers` 和 `CreateUser` 回显，审计 detail 记录 from→to，管理页加一个下拉框。
- CLI：建议同步支持（`--create-user … --reading-ingest web|agent`，再加一个 `--set-reading-ingest <name> <v>`），用于无网页时的运维。**迁移脚本里不要写死 `UPDATE … WHERE username='ReaderNew'`**，开通走 AdminApi 或 CLI，并留下审计记录。
- 防撞：`CreateUser` 和 CLI 拒绝 `^A\d{3}$` 形态的用户名（§1.2 已定口径，代码里还没有）。

### E. 安全与运维面

**P2R-18 TtsWeb 的 /share 在宽限期内允许匿名写库**（should）
- 证据：`grace_until=2026-12-02`（`tts-web/config.json`）。在此之前，不带令牌的 POST /share 照样会写进 reading 库（`tts_web.py:557-560`）。在这种状态下，「/add 保存必须登录」只是前端的门，任何人都可以绕过 /add 直接调 /share 写库。
- 建议：第二期上线时把 grace_until 提前到上线当天，或者至少不晚于第二期验收（前提是确认 A001/A003 都已经带令牌调用）。列入 §5。

**P2R-19 公网登录失败的 IP 维度是全站共享的**（should）
- 证据：`login_audit` 里全部 42 条记录的 IP 都是 `127.0.0.1`（只读查询）。也就是说，instabox 网关在本机回环转发，`TRUST_PROXY` 没开，所有公网来访者在服务看来是同一个 IP。每 IP 15 分钟 20 次失败的阈值（`src/AuthService.cs:45,310-315`）因此等于**全站共用**。ReaderNew 的用户名固定、在登录框里可见，任何人输错 5 次就能把它锁 15 分钟；再打 20 次，所有账号（包括 admin）都会被 429 挡 15 分钟。IP 封禁对回环地址豁免，但节流不豁免。
- 建议：先核实网关有没有传 `X-Forwarded-For`。如果有，开 `TRUST_PROXY=1`，取最右边一项。如果没有，就接受这个风险（私人小工具），并在文档里写明；可以考虑把每 IP 节流对回环地址的阈值调高。无论哪种，都在 /add 的登录框里把 423/429 的剩余时间如实显示出来。

**P2R-20 限制 ReaderNew 的生成类权限**（nice）
- 问题：ReaderNew 的 JWT 一样能调 /api/text、/api/image、/api/video 和 /api/analyze，消耗的是 Agnes 配额。它的口令放在浏览器保险库里，而 /add 在公网上。
- 建议：给 `reading_ingest='web'` 且 role=user 的账号单独设 `daily_quota`（例如 30），或者让生成类端点拒绝「只有 web 入库权限」的账号。后者需要再加一个标志，第一版先用前者。

**P2R-21 没有 Mock TtsWeb**（should）
- 证据：`tests/MockAgnes/Program.cs` 只模拟了 Agnes 和 Gemini，没有 /share、preview、commit。§5、§6 的「走 Mock TtsWeb 做破坏性用例」目前没有现成的实现。
- 建议：二选一。(a) 在 MockAgnes 里加 `/share`、`/api/v1/ingest/preview|commit`、`/api/v1/articles/{id}` 的假实现，支持 `dup`、`409`、`503`、`401` 等模式；(b) 起一个开发用的 TtsWeb 实例（`impl/ddpc-p5-dev` 已经有 `config_dev.json` 的先例），指向独立的开发 PG schema 或 SQLite。推荐 (a)，能稳定复现 VERSION_CONFLICT 和 T 态。

**P2R-22 L1 延迟重试不要把不同 provider 的译文拼进同一个版本**（nice）
- 问题：L1e 先按 partial 提交，之后的延迟重试只补缺失的句子。如果前一次用的是 Gemini、补的那次落到 Agnes，同一个版本里就会有两个执行者，而 commit 只支持整版本一个 `tr_via`（R7 未实现，见 `store.py:196`）。
- 建议：延迟重试一律整篇重做（Gemini 很便宜）；或者补缺失句时强制使用前一版本的 provider，做不到就整篇重做。这和 DF-10「一个版本一个执行者」保持一致。

**P2R-23 存量 partial 文章不在 L2 清单里**（nice）
- 证据：reading 库现在有 8 篇 partial（只读统计）。§2.5 的 L2 清单只覆盖经过编排的作业。
- 建议：清单接口可以选择合并「TtsWeb 里 partial 且没有活动作业的文章」（用 `GET /api/v1/articles` 过滤 status）。巡检者也可以直接用这个口径。由用户决定要不要做。

---

## 2. 朗读前端与认证

### 2.1 核对事实（决定三案优劣的前提）

1. **8791 和 8793 是同站（same-site）不同源**。公网地址形如 `https://879x-<id>.instabox.tech`。2026-11-19 拉取的 Public Suffix List 里没有 `instabox.tech`，所以两者的 site 都是 `instabox.tech`。因此，`SameSite=Strict` 的 refresh Cookie（`Program.cs:327-332`）在 8791 页面用 `fetch(..., {credentials:'include'})` 调 8793 时**会被带上**，也能被设置。设计稿 §7.4「属于第三方 Cookie，浏览器默认受限」这句**不准确**。但这建立在两个脆弱前提上：
   - 一旦 instabox 把自己登记进 PSL（托管平台常这样做），这条链路会在没有任何提示的情况下失效；
   - 所有其他 instabox 租户的子域也和你「同站」，SameSite 对他们不起作用，CSRF 只能靠 JSON Content-Type 和 CORS 白名单来挡。
2. **refresh Cookie 是 8793 主机级的单例**：名字 `agnes_rt`，路径 `/api/auth`（`src/AuthService.cs:28-29`）。A 案里，/add 登录 ReaderNew 和 8793 主站登录 admin 写的是**同一个 Cookie**，后登录的一方会顶掉前一方：主站下次刷新就变成 ReaderNew，或者 /add 刷新后拿到的是 admin 的令牌（再被 `reading_ingest` 403）。这是 A 案的硬伤（blocker）。
3. agnes-service 现在的 CSRF 模型明确依赖「**服务从不回应 CORS**」（`DESIGN.md:241-250`）。A 案要改这个模型。
4. access token 的现行做法是只放内存、不进 localStorage，页面加载时用 refresh 恢复会话（`index.html:285-299`）。/add 也应该照这个做。
5. 公网登录全部表现为 `127.0.0.1`（P2R-19）。三案都受影响，只有 B 案的情况未知。
6. 浏览器保险库：ReaderNew 现在的保险库条目是给 8793 主机存的。Chrome 的密码管理器按源匹配，在 8791 页面**不会自动填充**，需要另存一条。如果保险库按「基础域名」匹配，又会匹配到所有 `*.instabox.tech`，也就是其他租户的机器。所以每个方案都要有意识地处理这一点。
7. **dbapi 的现状**：库 `dbapi.app_user` 里只有 Proseidon(admin) 和 MuseAgent(editor) 两个账号（只读查询）。角色是 viewer、editor、admin，都是「表编辑」类角色。源码 `/data/src/db-api` 在本项目里一直不可读（`DESIGN.md:171-186`），它的 JWT claim 和有效期也没有核实过。

### 2.2 三案对比

| 维度 | A：/add 跨源直调 8793（现稿） | B：账号开在 dbapi(8792) | C：TtsWeb 同源反代（推荐） |
|---|---|---|---|
| 实现复杂度 | agnes-service 加 CORS 中间件：预检要放行 `Authorization` 和 `Content-Type`；错误响应也要带 ACAO；要 `Expose-Headers: Retry-After`；`Vary: Origin`。还要解决 Cookie 冲突：/add 用独立的 Cookie 名和路径，例如 `agnes_rt_ingest` 加 `/api/auth/ingest/*` 这一组 login/refresh/logout，即新增 3 个端点。/add 前端处理 credentials:'include'。**中等** | agnes-service 要验证 dbapi 签发的 JWT：要么共享 dbapi 的 JWT_SECRET，要么调 dbapi 做内省。可 dbapi 的源码不可读、claim 格式也没核实。8791→8792 仍然跨源，dbapi 的 CORS 我方改不了。call_log 和 usage_daily 的 `user_id` 是 NOT NULL 外键，指向 `agnes_auth.users`（`auth_schema.sql:89,122`），dbapi 用户进不来，还得再建一个影子账号。**最高，而且有一部分不受我方控制** | TtsWeb（aiohttp）加约 60–80 行的透传反代，只放行 5 条路由：`POST /api/auth/login`、`/api/auth/refresh`、`/api/auth/logout`、`POST /api/reading/ingest`、`GET /api/reading/jobs/{id}`，全部转发到 `127.0.0.1:8793`。转发时带上 `X-Forwarded-Proto`（agnes 据此决定是否加 Secure），原样回传 `Set-Cookie`（Path=/api/auth 与 TtsWeb 现有路由不冲突，TtsWeb 只有 `/api/v1/*`）。agnes-service **零改动**，不开 CORS。**低** |
| 可靠性（Cookie/CORS） | 依赖 PSL 现状（2.1-1）；Cookie 冲突必须另做隔离；CORS 的错误响应头漏一处，浏览器就只报「网络错误」 | 同 A 的跨源问题，再加上两套认证的时钟、密钥、轮换策略要对齐 | Cookie 落在 8791 主机，是第一方、同源，和 8793 主站互不干扰；不依赖 PSL；没有预检 |
| 安全面 | 8793 对 8791 源开放带凭据的跨源访问：8791 源上任何一处 XSS（v2、v3 播放页都有 `v-html`，TtsWeb 也不发 CSP）都能借 refresh 拿到 ReaderNew 的令牌。还改变了现有的 CSRF 模型 | 一个「只为粘贴入库」的账号被放进了**数据库管理系统**：即使只给 viewer，也能浏览 dbapi 管理的库，越权面最大。还要把 dbapi 的密钥扩散给 agnes-service | 8791 源的 XSS 同样能调同源反代拿令牌（与 A 相同），但 agnes-service 的攻击面不变。口令在传输中经过 TtsWeb 进程（不落盘、不打日志）。需要说明：TtsWeb 和 agnes-service 都以同一 OS 用户 `user` 运行，TtsWeb 本来就读得到 agnes 的 `.env`（含 JWT_SECRET），所以「口令不经 TtsWeb」在同机同用户的条件下**没有实际的安全增益** |
| 与记账口径衔接 | added_by=ReaderNew 取自 JWT；call_log 的 user=ReaderNew，kind=ingest。**顺畅** | dbapi 的用户名到 added_by 要做映射，call_log 要影子账号，这违反 §1.2「不做跨体系映射」。**冲突** | 同 A。**顺畅** |
| 后续维护 | 每次跨源改动都要在两个项目里联调；CORS 白名单要跟随公网地址变化（换机器、换 ID 都要改） | 两套账号体系并存；dbapi 升级不可控 | 反代路由表只改一处；公网地址变化不影响 |

### 2.3 推荐：C 案（透传反代形态）

理由：
- 它是唯一一个同时消除「跨源 Cookie 依赖 PSL」和「主站 Cookie 冲突」两个坑的方案；
- agnes-service 不需要为前端改认证模型；
- 记账口径与 A 一致；
- 工作量主要在 TtsWeb，和 §1.5「按钮与 /add 归 TtsWeb」的分工吻合。

**B 案不推荐**：它没有消除跨源问题，只是把问题换到了一个我方改不了的服务上；还会给只需要粘贴入库的账号授予数据库管理权限；call_log 的外键结构也接不住。

> 备注（不改变推荐）：如果用户不坚持 /add 必须挂在 8791 下，还有一个更省的「D 案」：/add 页由 agnes-service 自己提供（8793 同源，独立页面，不是主站页签），播放页按钮只是一个外链。这样零反代、零 CORS，还能复用现成的 8793 保险库条目，8791 源的 XSS 也拿不到任何东西。代价是保存时地址栏会换到 8793 的域名，与 DF-8 改定的「TtsWeb 的 /add 页」相悖，所以只作为退路列出。

### 2.4 采纳 C 案时，方案稿要连带改的条目

- **§1.5 用户旅程第 2 步**：「与 agnes-service 同一套 JWT 认证」保留。「登录态在浏览器内保持」改为「refresh Cookie 由 agnes-service 签发，经 TtsWeb 同源反代落在 8791 主机；access token 只放内存」。ReaderNew 的保险库条目要为 8791 主机另存一条（**精确主机匹配**，不要用基础域名匹配）。
- **§1.5「权限」第 1 条**：删掉「口令只从浏览器直达 8793 的登录接口，TtsWeb 页面与代码不接触口令」。改为「口令经 TtsWeb 反代透传到本机 8793；TtsWeb 不解析、不记录、不落盘请求体（反代日志只记方法、路径、状态码）」。
- **§1.5「权限」第 3 条（技术前提：CORS）**：整条删除，改为「TtsWeb 反代白名单（上面 5 条路由），其余 `/api/auth/*` 和 `/api/admin/*` 一律 404」。
- **§2.1 CORS 一条**：删除，改为「agnes-service 不开 CORS（`DESIGN.md` 的 CSRF 口径不变）」。
- **§5 TtsWeb 侧实现**：「保存与认证都直调 8793」改为「经 TtsWeb 同源反代」，并加上反代实现项与验收项。
- **§6 验收**：「跨源调用只对白名单源放行」改为「8793 的任何响应都不带 `Access-Control-Allow-*`；反代对白名单外的路径返回 404；/add 页和主站分别登录不同账号，互不影响」。
- **DF-9**：说明里补一句「承载方式：C 案反代」。

### 2.5 其余前端核对结论（与选哪一案无关）

1. **token 存放与过期体验**：
   - access token 只放在 JS 变量里，不进 localStorage 或 sessionStorage；
   - 页面打开先调 refresh 恢复会话；
   - 任何 401 都先单飞刷新一次再重试；如果刷新返回「already rotated」（10 秒宽限，见 `src/AuthService.cs:432-433`，多标签页同时刷新时会遇到），等 1–2 秒再刷新一次，不要直接退出登录；
   - refresh 失效（7 天不活动，或 30 天的会话家族上限）时，就地弹出登录框，**不清空已经粘贴的正文**；
   - 正文草稿存 localStorage（正文不是机密），保存成功后清除；
   - 轮询作业时 access token 会在 30 分钟后过期，走同一条 401→刷新路径。
2. **/add 的 XSS 面**：
   - /add 做成独立页 `add.html`，JS 外置成 `/add.js`；
   - `handle_add` 单独发严格的 CSP：`default-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'`；
   - 页面里所有回显（标题、错误信息、作业 trail）都用文本节点或 `{{ }}`，不用 `v-html`；
   - 播放页本身目前没有 CSP，这一点不在第二期范围内，但要记为已知风险：在 C 案下，8791 任意页面的 XSS 都等于拿到 ReaderNew 的会话。
3. **按钮放在哪里**：
   - 建议放在选文面板的面板头里，和「× 返回」并排，做成 `<a class="tog" href="/add">＋ 添加文章</a>`（位置见 `player_v3.html:423-426`），而不是放在页头。理由：页头在 P4f 刚压成一行，横屏三键的焦点样式也刚统一过（`player_v3.html:129-134`）；「添加」在语义上属于文章库；放在面板里不改变主界面的遥控焦点序，只是在面板内多一个焦点目标，`navEls()` 按 DOM 动态取，不需要另写规则。
   - /add 应该做成**独立页面**，而不是在 v3 里再开一个原位面板。认证代码不进播放页，也就不会碰「播放内核原样移植」的验收边界，还能单独发 CSP。
4. **77B 与 ddPC 共用同一份 `player_v3.html`**：两份文件逐字节相同（`cmp` 无差异）。ddPC 的 TtsWeb 是只读缓存，没有 /add，也没有反代。所以按钮要**按部署开关显示**，例如 `handle_v3` 根据 `CFG.features.add_article` 把占位符替换成 `<meta name="ttsweb-add" content="1">`，页面读这个 meta 决定是否显示按钮；ddPC 的配置不开。也可以加一条 UA 判断，在 webOS 电视上隐藏按钮（电视不适合粘贴长文）。
5. **作业状态由谁回显、文章状态从哪里读（职责边界）**：
   - **作业状态**：唯一真源是 agnes-service 的作业表（P2R-1）。/add 只轮询 `GET /api/reading/jobs/{id}`。作业的终态里带上 agnes-service 从 TtsWeb 读到的 `article_status` 和 `version`，/add 不直接读 TtsWeb 的库。
   - **文章状态（作业过期之后）**：需要 TtsWeb 补两处小改动（列入 §5）：
     - `/v3/t/<id>` 的响应加上 `status` 字段（现在没有，见 `store.py:66-105`），播放页据此显示「译文待补」角标；
     - `/v3/articles` 现在**只列 ready**（`store.py:107-118`），刚保存的 partial 文章在选文列表里找不到。建议列表也纳入 partial，带上 `status`，页面标「译文待补」；如果不想改列表口径，就由 /add 页提供「我最近保存的」列表，来自 agnes-service 的 `GET /api/reading/jobs?mine=1`，保留 30 天。
   - **短链**：/share 返回的是 `url:"/?s=<id>"`（`tts_web.py:597`），在 77B 上这会打开 v2 默认页。/add 应该拼成 `/v3?s=<id>` 再给用户。
   - **非日文文本（D8）**：/add 提供语言选择（ja/zh/en，默认 ja）。agnes-service 端再加一道兜底：假名占比太低时按非 ja 处理，只 share 不 enrich，免得中文被当成日文送去 enrich。
6. **v2 是否也加按钮**：同意推荐，只上 v3。

---

## 3. 待拍板清单逐条意见

| 项 | 意见 |
|---|---|
| DF-1 分级口径 | **同意**。 |
| DF-2 added_via | **同意加列**，但必须连同 API 改动一起做（P2R-11）。取值建议 `agent / web / legacy`，存量回填 `legacy`，比留 NULL 更好查。 |
| DF-3 share-first | **同意**。附带收益：share_id 可以直接做作业锁键（P2R-14）。 |
| DF-5 巡检与演练 | **同意**，但加上 L2 自动对账（P2R-15），巡检者只负责「拉清单 → 调 agy」，不负责回填。 |
| DF-6 熔断参数 | **基本同意，有两处修订**：① 429 按 `retryDelay` 或 `Retry-After` 冷却，没有时 2 分钟；403 quota 冷却 60 分钟（P2R-8）。② 「质量熔断」要分开定义：设计稿 E7 的「连续 3 篇 `analyze_validation_failed` → API 路熔断」要保留，方案稿 §2.2 没有明说；新增的「fixes 采纳熔断」，在 P2R-9 第 1 层上线后收益有限，因为改写类默认已经不采纳。建议第二期只做统计（`fx=` 写进 call_log），不做自动熔断；如果一定要做，阈值写成「最近 5 篇里有 ≥3 篇的改写类拒绝率 ≥50%，且每篇至少 2 条」。业务量每天只有几篇，「连续 3 篇」可能要好几天才凑得到，按窗口算比较稳。 |
| DF-7 时间与分块参数 | **给替代值**（见下表）。理由：现有代码里单次调用超时是 `ANALYZE_TIMEOUT_S=180`，整单期限是写死的常量 `DeadlineSeconds=600`（`src/Analyze.cs:44,133`），每块最多 3 次调用，两块并行（`src/Analyze.cs:130,132`）。实测 Gemini lite 15 句只要 1.9 秒，flash-latest 两次都约 180 秒超时，Agnes 15 句 199 秒（中间一次失败后降级）。单一的 `CHUNK_TIMEOUT_S=90` 对 lite 太松，对 Agnes 太紧，应该按 provider 分别设。总时限 300 秒在「只剩 Agnes、120 句长文」时一定会落到 partial：12 块 ÷ 2 并行 × 约 80 秒 ≈ 480 秒，还要受每分钟 20 次的限制。 |
| DF-8 | 已定，无意见。 |
| DF-9 | 已定。承载方式建议改为 C 案（§2.4）；`reading_ingest` 用三值，MuseAgent1/2 设为 `agent`（P2R-17）。 |
| 播放页版本 | **同意只上 v3**，按钮按部署开关显示（§2.5-4）。 |
| DF-10 | **同意**。另外补一条：L1 延迟重试也遵守「一个版本一个执行者」（P2R-22）。 |
| DF-11 令牌白名单 | **需修订（blocker）**：要**新建** agnes-service 令牌，`can_act_for={A001,A002,A003}`，再加上 `web_channel:true` 以及网页通道的用户名规则（P2R-10）。只写 `{A001,A002,A003}` 的话，网页通道必定 403。 |
| DF-12 朗读侧改动归属 | **同意**，但清单要补全：TR_VIA 的 CHECK 迁移和 ingest.py 白名单；/share 与 commit 接受 added_via；web_channel 规则；`readings_check` 接口（P2R-9）；`/v3/t` 加 status、`/v3/articles` 纳入 partial（§2.5-5）；同源反代和 /add 页（§2.4）；grace_until 提前（P2R-18）。 |
| 评审 D5 记账 | **选独立 kind `ingest`**。一次作业准入 1 次，系统发起的重试和试探不计额度（P2R-2、P2R-3）。理由：analyze 的语义是「一次 /api/analyze 请求」，混用后，call_log 的统计和 D5 的复盘都分不清直调和编排；连带改动只有一个 CHECK 迁移加上 4 处白名单，第一期已经有同样写法的先例。 |
| 评审 D6 gloss | **同意维持**，不做。 |
| 评审 D8 非 ja | **同意**，并加上服务端的假名占比兜底（§2.5-5）。 |

**DF-7 建议起步值**（都是新键，配置时注意 P2R-5 的「改了要重启」）：

| 参数 | 建议 | 说明 |
|---|---|---|
| gemini 块 | 20 句 / 1500 字（维持现状） | 与 `MakeChunks` 的默认值一致 |
| agnes 块 | 8 句 / 600 字 | 换到 agnes 时重新分块（P2R-7） |
| 单次超时 gemini-flash-lite | 45 s | 实测 1.9 s，留足余量 |
| 单次超时 gemini-flash | 90 s | 实测会挂到 180 s，越早止损越好 |
| 单次超时 agnes（3.0 / 2.5） | 120 s | 15 句约 199 s（含一次失败），8 句估计在 60–100 s |
| `INGEST_DEADLINE_S` | 420 s | 从 enrich 开始计。落在 agent 等待上限 8 分钟之内（share、preview、commit 约 20 s）；300 s 对只剩 Agnes 的长文必然 partial |
| `/api/analyze` 直调 | 180 s / 600 s 不变 | 和编排解耦；`DeadlineSeconds` 改成可配置 |

---

## 4. 实施顺序建议

**第 0 步（朗读侧先行，阻塞 agnes-service 联调）**
1. reading DDL：tr_via 的 CHECK 迁移、`added_via` 列（P2R-11、P2R-12），以及 `ingest.py` 的 TR_VIA。
2. TtsWeb 契约：/share 与 commit 接受 added_via；`web_channel` 规则；**签发 agnes-service 令牌**（P2R-10），重启 TtsWeb。
3. （可与第 1 步并行）`/v3/t` 加 status，`/v3/articles` 纳入 partial；`readings_check` 接口。

**第 1 步（agnes-service 后端，在开发实例 8795 上做）**
1. `auth_schema.sql` 迁移：`reading_ingest`（加上可选的 `muse_id`）、kind `ingest`、`ingest_jobs` 表。
2. 作业框架：POST 202、准入后立即释放 Ticket、worker 池、启动时恢复、延迟重试扫描器、L2 清单和自动对账（P2R-1、P2R-2、P2R-15）。
3. L1：全文上下文（P2R-6）、按 provider 分块和超时、换 provider 时重新分块（P2R-7）、`Upstream` 层的令牌桶（P2R-4）、熔断（P2R-8）。
4. fixes 第 1 层本地分流（P2R-9）。第 2 层等朗读侧接口就绪后再接。
5. replace 时回传 title、lang、rate（P2R-13）。
6. Mock TtsWeb（P2R-21），全量回归第一期和 text/image/video。

**第 2 步（前端）**
1. TtsWeb：同源反代（C 案）、`add.html` 和 `/add.js`（含 CSP）、v3 面板头的按钮（按部署开关显示）。
2. agnes-service 管理页：`reading_ingest` 下拉框、作业和 L2 列表、kind 筛选加 ingest。

**第 3 步（上线）**
- grace_until 提前（P2R-18）→ 生产迁移 → publish、重启 → 用一篇用户指定的真实文章走通全链冒烟。

**主要风险点**
1. **跨项目发版耦合**：第 0 步不完成，网页通道的 /share 必然 403。验收前要先对一遍令牌和白名单。
2. **systemd 冻结 .env**（P2R-5）：新参数、轮换 Key 都要重启才生效，运维容易误以为已经生效。
3. **Agnes 兜底路径的时延与每分钟 20 次的上限**：只剩 Agnes 时，长文大概率落 partial 再走延迟重试，用户等待可能长达数小时。要在 /add 上如实显示「译文待补」。
4. **公网登录共用一个 IP 维度**（P2R-19）：ReaderNew 可以被他人锁定 15 分钟，全站登录可以被节流。
5. **PSL 依赖**：只在保留 A 案时存在；选 C 案即可消除。
6. **8791 源的 XSS**：C 案和 A 案都会把 XSS 放大成 ReaderNew 会话泄露。/add 的 CSP 只能保护 /add 本身，播放页 v2、v3 至今没有 CSP，后续应该补上。

---

## 补审：保存接口与认证归属总推荐

补审日期：2026-11-19。对象：用户追加的 D 案「让 77B 后端增加一个接口，用来保存原文」。本节只追加，前文不改；本节结论**取代 §2.3 的推荐**（C 案在本节中作为「承载方式」保留，见 S.4）。

> 命名说明：§2.3 末尾备注里的「D 案」（/add 页由 8793 自己托管）与用户本次的 D 案不是一回事，本节把那个备注改称 **D0**，不参与比较。

### S.1 先界定：「保存原文」接口要解决的是什么

无论接口挂在哪，保存动作都绕不开三件事：

1. **身份**：ReaderNew 的口令换成 JWT，只有 agnes-service 能签发（agnes_auth，`src/AuthService.cs`）。也就是说**登录这一步必然要到达 8793**，只能是浏览器跨源直达，或经 TtsWeb 同源转发，二选一。专用保存接口不改变这一点。
2. **授权与准入**：JWT 的 claim 只有 `sub/name/role/ver/typ/jti`（`src/AuthService.cs:82-90`），**没有 `reading_ingest`**。`ver`（token_version）、`disabled`、`reading_ingest` 都要拿 `agnes_auth.users` 的现值来判（`AuthService.UserState`，10 秒缓存）。日额度和每分钟限额也只在 agnes-service 里（`LimitService`）。所以「能不能存」的权威判断只能在 agnes-service 做。
3. **存原文与建作业的原子性**：share 成功但作业没建，就会得到一篇没人跟进的 partial，L2 清单里也找不到它（P2R-1、P2R-15 依赖作业表）。两步由同一个进程按「先落作业行（stage=share）、再 /share、再回填 share_id」的顺序做，才能在重启后恢复。

由此可见，D 案真正的价值在于把**网页通道的写入口**从通用编排端点里**拆出一个窄口**。挂在哪个进程，要看这三件事由谁来做最自然。

### S.2 D1 与 D2 的界定

**D1：接口在 TtsWeb（例如 `POST /api/v1/add/save`，与 /add 同源）**

TtsWeb 没有账号体系，凭据只能有以下两种来路：

- **D1a：TtsWeb 本地验 JWT**。要把 agnes 的 `JWT_SECRET` 配给 TtsWeb。HS256 是对称密钥，拿到它的 TtsWeb **也能签发**任意账号（包括 admin）的令牌。同机同 OS 用户的条件下 TtsWeb 本来就读得到 `.env`（§2.2 表 C 列已说明），但把这种「事实上读得到」变成「配置上依赖它」，等于正式扩散了密钥，以后轮换 JWT_SECRET 要两边同步。本地验签也**判不了 `ver`、`disabled`、`reading_ingest`、额度**（S.1-2）：管理员禁用 ReaderNew 或改了口令后，最长 30 分钟内 TtsWeb 仍会放行。如果要补这些判断，TtsWeb 就得直接读 `agnes_auth` 库，跨了库的归属边界。
- **D1b：TtsWeb 不验，转交 agnes 验**。浏览器把 JWT 交给 TtsWeb，TtsWeb 先调 agnes 的「校验与准入」，通过后**在本进程里存原文**（直接 `store.share`，不走 HTTP），再带着 share_id 和用户的 JWT 调 agnes 建作业。问题在于：一次保存分成三跳、跨了两个进程，「已 share 但作业没建成」这种裂脑无法避免（agnes 在第二跳和第三跳之间重启或返回 429 都会出现），要靠补偿逻辑或定期对账来兜。**登录、刷新、登出仍然要经 TtsWeb 转发到 8793**，也就是 C 案的 3 条认证路由一条也省不掉。

D1 唯一的实利是：原文由 TtsWeb 自己存，added_by=ReaderNew 在 TtsWeb 进程内就定了，可以绕开 P2R-10 的「agnes 令牌代填 `web_channel`」规则。但 agnes 后续的 preview 和 commit 仍然要用自己的服务令牌，第 0 步签发令牌照样要做，只是少一条代填规则，**省得很少**。

**D2：接口在 agnes-service（例如 `POST /api/reading/save`，与编排端点 `/api/reading/ingest` 分开）**

- 职责：JWT 校验 → `reading_ingest='web'` 判定 → 准入（kind=`ingest`，P2R-2/3）→ 先落作业行 → 同步 /share（实测约 2 秒，远低于网关 20 秒）→ **在响应里直接返回 `share_id` 和 `/v3?s=<id>` 短链**，同时返回 job_id → 后台继续 preview、enrich、commit。
- 窄面体现在：入参只收 `{title, text, lang}`；`added_via=web`、`added_by=JWT name` 由服务端写死；**不接受 share_id、provider、强制重做之类编排参数**；正文长度设上限（建议 ≤ 20,000 字）；作业查询只返回 `user_id = sub` 的作业。编排端点 `/api/reading/ingest` 只留给 agent 通道，网页通道根本碰不到它。
- 认证三件事全在同一进程内完成，原子性由作业表保证（S.1-3），不需要补偿逻辑。
- **但「页面仍跨源」这一点没变**：如果浏览器直接调 8793，登录、刷新、登出加上 save、jobs，仍然要开 CORS（allow-credentials），refresh Cookie 和主站冲突（§2.1-2）、PSL 依赖（§2.1-1）、CSRF 模型改变（§2.1-3）这三处与 A 案相同，**全部保留**。接口变窄，只是把编排参数这一块攻击面去掉了，没有消除 A 案的 blocker。

所以 D2 端点要配上 C 案的同源透传反代，才能把跨源问题一并消掉。下表把这种组合单列为「D2+C」。

### S.3 六案总比较

| 维度 | A：跨源直调编排 | B：dbapi 开账号 | C：TtsWeb 同源反代（编排端点） | D1：TtsWeb 保存接口 | D2（纯）：agnes 专用保存端点、浏览器跨源直调 | **D2+C：agnes 专用保存端点，经 TtsWeb 同源反代（推荐）** |
|---|---|---|---|---|---|---|
| 实现复杂度 | 中：CORS 中间件全套，另加一组独立 Cookie 名的 login/refresh/logout | 最高，部分不可控（§2.2） | 低：TtsWeb 反代约 60–80 行，agnes 零改动 | **高**：认证路由的反代一条不少，另加 TtsWeb 保存逻辑；D1a 要分发密钥，D1b 要三跳加补偿 | 中：同 A 的 CORS 和 Cookie 隔离，另加一个 save 端点 | 低：反代同 C；agnes 多一个约 80–120 行的 save 端点，复用作业框架，入参校验也比通用端点简单 |
| 可靠性 | 依赖 PSL；Cookie 冲突；CORS 头漏一处浏览器就只报网络错误 | 同 A，还要对齐两套认证 | 不依赖 PSL、无 Cookie 冲突、无预检 | D1b：share 与建作业分在两个进程，有裂脑；D1a：吊销最长滞后 30 分钟 | 同 A | 同 C，而且**存原文与建作业同在一个进程、作业先落行**，重启可恢复；保存响应**当场带回短链**，不用靠轮询才拿到 |
| 安全面 | 8793 对 8791 开带凭据的跨源访问；改 CSRF 模型；8791 的 XSS 能拿到会话 | 越权面最大（数据库管理系统） | agnes 攻击面不变；8791 的 XSS 风险同 A | D1a：**对称签名密钥扩散到 TtsWeb**，可伪造 admin 令牌；D1b：同 C | 同 A，另外网页通道够不着编排参数 | agnes 不开 CORS；网页凭据**只能**调到 save 和自己的 jobs（反代白名单＋端点写死 added_via/added_by）；XSS 风险同 C |
| 记账衔接 | 顺畅 | 冲突（影子账号） | 顺畅 | D1b 顺畅，但准入和 share 不在一处，额度拒绝后可能已经存了原文；D1a 记账要另外回调 agnes | 顺畅 | 顺畅：kind=`ingest`，一次保存准入 1 次（P2R-2/3） |
| 维护成本 | 两项目联调 CORS；白名单跟着公网地址变 | 两套账号体系 | 反代路由表一处 | 最高：保存逻辑横跨两个项目，凭据和契约两边一起演进 | 同 A | 低：反代路由表一处；网页契约（save）与 agent 契约（ingest）各自演进，互不牵连 |
| 结论 | 不推荐（blocker §2.1-2） | 不推荐 | 可行（原推荐） | 不推荐 | 不推荐（A 的 blocker 全保留） | **推荐** |

### S.4 最终推荐：D2+C

一句话：**保存原文接口归 agnes-service（D2 的形态），浏览器经 TtsWeb 同源透传反代访问它（C 的承载）。**

理由：
1. **归属对**：身份签发、授权现值、额度、作业表都在 agnes-service（S.1），保存接口放在这里，校验、准入、share-first、建作业在一个进程里按顺序做完，不需要补偿逻辑。D1 不管走哪条凭据路线，都要么扩散对称密钥，要么拆成三跳。
2. **承载对**：同源反代是唯一能同时消掉 PSL 依赖、主站 Cookie 冲突和 CSRF 模型改动的办法（§2.3 的理由原样成立）。纯 D2 只收窄了接口，跨源的三个坑一个没少。
3. **比原 C 案多得的**：网页通道和 agent 通道的契约分开。反代白名单里不再出现通用编排端点；added_via、added_by 由服务端写死；保存响应**同步返回短链**，满足 §1.5 第 4 步「保存完点开短链已能听原文」，不依赖轮询；作业查询按属主过滤。
4. **代价小**：比原 C 案只多 agnes 的一个窄端点（复用 P2R-1 的作业框架和 P2R-2 的准入），TtsWeb 侧的工作量与 C 相同。

**落地要点**（实施时据此写契约）：
- 反代白名单（TtsWeb → `127.0.0.1:8793`）：`POST /api/auth/login`、`POST /api/auth/refresh`、`POST /api/auth/logout`、`POST /api/reading/save`、`GET /api/reading/save/{job_id}`（或 `GET /api/reading/jobs/{id}`，但必须按属主过滤）。其余路径一律 404，**`/api/reading/ingest` 不进白名单**。
- `POST /api/reading/save` 的判定顺序：匿名 401 → `reading_ingest≠'web'` 403 `ingest_forbidden`（不扣额度）→ 每分钟限额 429 → 落作业行 → /share。/share 失败时作业记 failed，返回 502/503，并且**不产生文章**。/share 成功后返回 `201 {share_id, url:"/v3?s=…", job_id, article_status:"partial"}`。
- **当日额度用完时**：建议照样存原文（share 不消耗上游），作业记为 `deferred` 并在次日重试，响应里如实告知「已保存，译文待补」。理由：用户粘贴的原文不应因为额度被丢掉。是否这样做由用户定；不这样做的话，就在额度用完时直接 429，什么都不存。
- /share 仍由 agnes-service 以 `by=ReaderNew` 代填，所以 P2R-10（服务令牌加 `web_channel` 规则）**照旧是 blocker**。
- 编排端点 `/api/reading/ingest` 只服务 agent 通道（MuseAgent JWT，P2R-16），可以晚于 save 上线，见 S.6。

### S.5 方案稿需要连带修改的条目（只列清单，不改稿）

**§1.5 粘贴入库入口**
1. 用户旅程第 2 步：「登录态在浏览器内保持」改为「refresh Cookie 由 agnes-service 签发，经 TtsWeb 同源反代落在 8791 主机；access token 只放内存」；保险库要为 8791 主机另存一条，精确主机匹配（同 §2.4 第 1 条）。
2. 用户旅程第 4 步：「以 JWT 调 agnes-service 编排端点（§2.1）」改为「以 JWT 调 agnes-service **专用保存端点 `POST /api/reading/save`**（经同源反代）」；补一句「保存响应同步返回短链和 job_id」。
3. 用户旅程第 5 步：轮询对象改为 save 的作业查询（按属主过滤）。
4. 「权限」引言：四案已比较完毕，写明定案为 **D2+C**（待用户批准），删掉「A 案写法在定案前保留为现稿」。
5. 「权限」第 1 条：删掉「口令只从浏览器直达 8793……TtsWeb 页面与代码不接触口令」，改为「口令经 TtsWeb 反代透传到本机 8793；TtsWeb 不解析、不记录、不落盘请求体」。
6. 「权限」第 2 条：「保存接口（编排端点）」改为「保存接口（专用 save 端点）」；「写权限只给 ReaderNew」改为「web 写权限（`reading_ingest='web'`）只给 ReaderNew，agent 通道（`'agent'`）给 MuseAgent1/2」（P2R-17）。
7. 「权限」第 3 条（CORS 技术前提）：整条删除，改为「TtsWeb 反代白名单（S.4 所列 5 条），编排端点与 `/api/admin/*` 不进白名单」。
8. 「分工」段：TtsWeb 的职责加上「同源反代」；agnes-service 的职责加上「专用保存端点」。

**§2 agnes-service 实现范围**
9. §2.1 第 1 条：拆成两个端点：`POST /api/reading/save`（网页通道，同步 share-first 后返回 201，入参只收 title、text、lang）和 `POST /api/reading/ingest`（agent 通道，202，可传 share_id）。作业查询按属主过滤，admin 可以看全部。
10. §2.1 第 2 条：「通道凭据（agent 令牌 / 网页 JWT）」改为「两个端点都只认 agnes JWT：save 要求 `reading_ingest='web'`，ingest 要求 `'agent'`」；同时删去「agent 通道以 TtsWeb 令牌定 added_by」的歧义（P2R-16，§1.2 第 3 条也要一起改）。
11. §2.1 CORS 一条：删除，改为「agnes-service 不开 CORS（`DESIGN.md` 的 CSRF 口径不变）」。
12. §2.1「进程内按文本哈希加锁」：改为「以 share_id 加部分唯一索引作锁」（P2R-1、P2R-14）。save 端点由于先 share，天然拿到这个锁键。
13. §2.1 补一条：作业落库（`ingest_jobs`）、先落作业行再 share、启动时恢复（P2R-1）。
14. §2.4：写明 save 和 ingest 共用 kind=`ingest`，各自准入 1 次；补上额度用完时「存原文、作业 deferred」与「直接 429」二选一，交用户定（S.4）。
15. §2.6：管理页的作业列表要能区分 save 和 ingest 两种来源（作业表的 channel 字段）。

**§5 朗读项目侧协调项**
16. 「TtsWeb 侧实现」：「保存与认证都直调 8793……（口令不经 TtsWeb）」改为「经 TtsWeb 同源透传反代调用 8793 的 auth 三路由和 save 端点」；加上反代实现项（白名单、`X-Forwarded-Proto`、原样回传 `Set-Cookie`、日志只记方法/路径/状态码），以及 `add.html`、`/add.js` 和 CSP（§2.5-2）。
17. 同一条：「与 agnes-service 侧 §2（编排端点 + CORS）互为 halves」改为「与 §2 的 save 端点互为 halves」。
18. 令牌一条：补全为「新建 agnes-service 写令牌：`can_act_for={A001,A002,A003}`＋`web_channel:true`＋网页通道用户名规则」（P2R-10、DF-11）。
19. 补上 §2.5-5 的两处小改（`/v3/t` 加 status、`/v3/articles` 纳入 partial）、`readings_check`（P2R-9）、grace_until 提前（P2R-18）。

（§5 以外顺带要动的：§4 DF-9 补「承载：D2+C」；§6 第 2 条的验收里，「跨源调用只对白名单源放行」改为「8793 任何响应都不带 `Access-Control-Allow-*`；反代白名单外 404；网页凭据调 `/api/reading/ingest` 被 403；/add 与主站分别登录不同账号互不影响；保存响应同步含短链」。）

### S.6 对实施顺序（§4）的影响

- **第 0 步不变**：P2R-10 的令牌与 `web_channel` 仍然是网页通道的前置 blocker，因为 /share 仍由 agnes 代 ReaderNew 执行。
- **第 1 步重排**：作业框架（P2R-1、P2R-2）做好后，**先做 save 端点**。它的同步部分（鉴权、准入、/share、返回短链）不依赖 L1、fixes 这些重活，可以最早拿 Mock TtsWeb（P2R-21）联调。agent 通道的 `/api/reading/ingest` 排到 L1 和 fixes 之后，和 MuseAgent 的映射（P2R-16）一起做。
- **第 2 步可以提前并行**：save 的契约（入参、201 响应、作业查询）定下来以后，TtsWeb 的反代和 /add 页就能对着开发实例 8795 开工，不必等 enrich 完工。整个过程不涉及 CORS，也就**省掉了 A 案下两个项目联调 CORS 的那一轮**。
- **可选的分段上线**：如果用户希望早点用上 /add，可以先上「save + share-first + 作业落 L2 待办」（不做 L1 自动补译），等 L1 和 fixes 就绪后再打开自动补译。这样做的前提是保存时就如实显示「译文待补」。是否分段由用户定。
- 风险清单的变化：§4 的风险 5（PSL 依赖）彻底消除；风险 6（8791 的 XSS）仍在，但网页凭据经反代**只能够到 save 和自己的作业**，泄露后的损失上限比 C 案低。

**最终结论：推荐 D2+C：「保存原文」专用接口建在 agnes-service（`POST /api/reading/save`，与 agent 编排端点分开），/add 页经 TtsWeb 同源透传反代访问它和 auth 三路由；不推荐 A、B、D1 和纯 D2。**
