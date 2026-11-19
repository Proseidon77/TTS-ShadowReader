# TASK6 第二期方案（编排 + 粘贴入库 + 降级执行）· 定稿

状态：**2026-11-19 用户批准定稿**（待拍板项全部按推荐+审核修订值落定）。**2026-11-19 晚回写**：网页保存认证设计 `PHASE2-AUTH-DESIGN.md`（Claude Opus 出稿）已经用户批准，§1.5 认证口径按该设计整段替换，§2.1／§2.6／§4／§5／§6 相应条目同步修订（差异清单见设计 §4.7）；其余各节不变。实施续跑按本稿＋设计 §7 实施要点清单执行（首跑 18:44 撞限额中断，续跑先盘点、已写对的不重写）。
日期：2026-11-19
依据：`TASK6.md`（深接方案）、`TASK6-REVIEW.md`（评审）、`TASK6-FALLBACK-DESIGN.md`（降级设计）、`PHASE2-REVIEW.md`（第二期审核两轮）、第一期实现与 2026-11-19 全部实测数据。本稿与上述文件冲突处，**以本稿为准**。

---

## 1. 已定口径（实施照此，不再讨论）

### 1.1 执行者梯队（DF-1 已定）

| 级 | 执行者 | 说明 |
|---|---|---|
| L0 | API 路一次做完 | agnes-service 编排 → Gemini API 为主、Agnes API 兜底 |
| L1 | API 路内部降级 | 修复重试 → 同 provider 换模型 → 换 provider → 缩块 → 到时限提交已完成部分；另有延迟重试（15 分钟 / 1 小时 / 4 小时，整篇重做，见 §2.2） |
| L2 | **agy 的 Gemini agent** | agy 部署在 **ddPC** 上（已就绪，见 §5），A001、A003 均可直接调度；按朗读方案 §2.2④ 契约整篇接手：preview → 自译+校对 → commit（走 replace）。tr_via 记 `agy-gemini` |
| L3 | 原文保底 | 文章停在 partial，照常可朗读，译文后补 |
| T 态 | TtsWeb / 77B 不可用 | 不算降级，只重试，最长 24 小时 |

- **Claude 完全退出朗读流水线**（省着用）：ddPC 驻地 Claude 不作执行者、不作 L2、不作抽查审计；77B Claude 仅作编码实现者，不作朗读执行者。
- agnes-service **只判定、不调度 L2**：负责把文章标成「待 L2」并记录原因。L2 调度：agent 通道发起的入库落 L2 时，由**发起 agent 自己**在 ddPC 调 agy 当场接手；网页通道的 L2 待办由巡检者（A001，每小时 1 次，DF-5）拉清单后，**用其自有 agy（A001 的 VM 本地）直接执行完成**（2026-11-19 晚用户改定：巡检者本就是 A001，绕 ddPC 调 agy 只增一跳延迟；ddPC 上的 agy 仍供 A003 在 agent 通道发起时当场接手用，两处 agy 为同一 Google 账号、共用同一额度池）。L2 完成状态以**自动对账**为准（§2.5），巡检者不负责回填。
- 每月 1 次 L2 演练（DF-5）：拿一篇短文走完整 L2 链，验证 agy 侧凭据与手册仍可用。

### 1.2 身份与记账（DF-2 已定）

- added_by 按提交通道记真实提交者，不做跨体系映射：agent 通道记 Muse 编号（A001/A003），网页通道记 `web`。`^A\d{3}$` 形态保留给 Muse agent 防撞（建号处代码拦截）。
- 新增 `added_via` 列区分通道，取值 `agent / web / legacy`，存量文章回填 `legacy`；/share 与 commit 接受该参数（§5），replace 不改它。
- agent 通道凭据＝**MuseAgent 账号的 JWT**（MuseAgent1＝A001、MuseAgent2＝A003，A002 不用）；agent 账号到 A00x 的映射由 agnes-service 固定（users 表加 `muse_id` 或 .env 写 `INGEST_AGENT_MAP`，实施时二选一）。**不允许把 TtsWeb 令牌转交给 agnes-service**；agent 已自行 /share 的，只提交 share_id，agnes-service 只做 replace（不改 added_by）。
- API 路出处：tr_via 新增 `gemini-api` / `agnes-api` 两值（实际出力的 provider 决定），tr_by＝`agnes-service`，逐句记 tr_model（DDL 由朗读项目侧实施，见 §5）。

### 1.3 先存原文（share-first，DF-3 已定）

- 任何通道入库，第一步先 `/share` 存原文拿到短链（约 2 秒可朗读，状态 partial），其后各级只补译文。
- 补译一律走 commit 的 **replace** 路径出新版本；普通 commit 对已存在文章只返回 duplicate。VERSION_CONFLICT 视为「对方已完成」的收敛信号，不算失败。
- 去重按状态分支：已 ready 的同文直接返回既有短链；partial / raw 状态的照常继续 enrich + commit，不许被去重挡住。
- 「同文只跑一份」用 **share_id 作锁键**（TtsWeb 的去重哈希是规范化后算的，.NET 侧不复刻），配合 §2.1 作业表的部分唯一索引。

### 1.4 编排必须异步（DF-13，实测强制）

- `POST /api/reading/ingest` 返回 **202 + job_id**，调用方轮询作业状态；不做同步长调用。理由见 §3：instabox 公网网关约 20 秒掐断长请求。
- 作业状态至少含：阶段（share / preview / enrich / commit / done / l2-pending / failed）、provider 与模型、分块进度、错误码与 trail、最终 share_id 与文章状态。

### 1.5 粘贴入库入口（网页通道，8793 JWT，2026-11-19 晚按 PHASE2-AUTH-DESIGN 回写）

**分离原则**：播放界面纯播放，保存走独立的另一条路 — 保存页是独立页面、独立链路，播放页（8791 v3）不承载任何保存功能与逻辑，**不加按钮、也不加链接**。

**形态（设计 R 案，用户 2026-11-19 晚批准）**：入口与保存都在 **8793（agnes-service）** — 现有管理页 `index.html` 里新增「添加文章」页签，另加路由别名 `GET /add` 返回同一个 `index.html`（前端见 pathname 为 `/add` 时登录后直开该页签），可收藏或加到手机主屏。用户沿用自己现有的 8793 账号登录：会话复用现成的 refresh Cookie（`agnes_rt`，7 天滑动续期、家族上限 30 天），口令由浏览器密码管理器保存自动填充，手机上几乎不用再输。此前三版口径（浏览器带 TtsWeb 写令牌调 /share、用户自选保存口令＋TtsWeb 服务端比对、口令走保险库）与审核推荐的 D2+C 同源反代、A 案跨源 CORS **全部作废**。审核第 4 个 blocker「/add 与主站共用 refresh Cookie 互顶」在本形态下不成立：只有一个账号、一个页面、一个会话，没有第二个身份可被顶掉（ReaderNew id 31 保留不动、不再参与网页通道）。

用户旅程：

1. 打开收藏的 `/add`（或管理页点「添加文章」页签）；无会话时就地登录（密码管理器自动填充，登录走现成的锁定与节流）。
2. 填标题（**必填**，≤120）、粘贴原文（≤5000 字）、选语言（ja/zh/en，默认 ja）→ 点「保存」。页面只提交 `title / text / lang` 三个字段，不接受 `share_id`、`rate`，`added_by / added_via` 由服务端写死。
3. 保存走现有的 `POST /api/reading/ingest`（web 通道分支）：JWT 验签 → 账号 `reading_ingest='web'` 判定（否则 403、不扣额度）→ 准入 → **先落 ingest_jobs 作业行（含原文）** → agnes-service 进程用 `TTSWEB_TOKEN` 经 127.0.0.1 代调 TtsWeb `POST /share`（`by:"web"`、`added_via:"web"`）拿短链 → 202 返回 `{job_id, share_id, url}`，`url` 为 8791 公网绝对地址（新配置键 `TTSWEB_PUBLIC_URL` 拼接 `/v3?s=<id>`）。**保存与补译是同一个作业**，不存在「存完再触发编排」的一跳。
4. 作业异步推进：preview 分句骨架 → enrich（Gemini 主、Agnes 兜底）翻译+读音 → fixes 核对层 → commit 转 ready。页面显示短链（点开即可听原文）并轮询 `GET /api/reading/jobs/{id}` 看进度；页签另列「最近保存」（`GET /api/reading/jobs?mine=1`）。
5. 降级：API 路中断或质量熔断 → 文章保持 partial 照常可朗读，作业转 L2 待办（agy 接手，执行环境见 §1.1）；补译完成后同一短链打开即是 ready 版，无需用户再操作。TtsWeb 暂时不可用时作业带原文落库、202 且 `share_id=null`，worker 按 T 态退避最长 24 小时自动补上短链（/share 按原文哈希去重、幂等）。8793 不可用时不能保存（草稿存 localStorage），但播放不受影响。
6. 非日文文本：只保存原文（partial 即终态），不走 enrich（D8 已定；服务端另加假名占比兜底判定，防语言选错）。

权限：**读开放、写受控，机器令牌只在服务端之间流转**。浏览器只与 8793 同源交互，只拿得到自己账号的短效 JWT（30 分钟、只放内存）；`TTSWEB_TOKEN` 只在 agnes-service 进程里、经 127.0.0.1 发给 TtsWeb，不进页面、用户不经手。TtsWeb 侧 `tokens.json` 登记 `agnes-service` 条目（只存哈希，`scope=write`，`can_act_for={A001,A002,A003,web}`，比 DF-11 多一个 `web`），明文备份在 `tts-web/tokens/agnes-service.txt`（0600），改后重启生效；**`tokens.json` 不新增 `web` 条目**，`web` 只是该令牌可代填的名字。谁可以保存：仅 `reading_ingest='web'` 的账号（用户本人日常账号，上线时 operator 在管理页开通、有审计）；MuseAgent1/2 保持 `agent`，互不影响。不开 CORS、不改现有 CSRF 模型（业务端点只认 Bearer、Cookie 端点有 JSON Content-Type 门槛与 SameSite=Strict）。登录防护复用现成机制（连错 5 次锁 15 分钟等）；已知限制 P2R-19（公网来访 IP 全是回环、他人可把账号临时锁住）属可用性问题、接受并写明。

归属记账（设计定值）：reading 库 `added_by=web`、`added_via=web` — 只记通道、不记 agnes 账号名（守 §1.2「不做跨体系映射」）；「谁点的保存」由 agnes 侧记（`ingest_jobs.user_id / username`、call_log kind=`ingest`、detail 写 `ch=web;by=web`），`ingest_jobs.channel=web`。

分工：页签、`/add` 别名、web 通道分支全归 **agnes-service** 实现（见 §2 与设计 §7 实施要点清单第 1–9 条）；TtsWeb 侧**不为网页通道写任何代码**，只剩令牌登记与 §5 其余契约项（设计 §7 第 10–11 条）。现有未上线代码里的 `X-Ingest-Key` 旁路（`INGEST_WEB_KEY` / `INGEST_WEB_USER`、「web 通道只收 share_id」）随实施删除。

### 1.6 provider 次序（已上线，本稿确认）

- analyze 默认 provider 已改为 **gemini 优先、agnes 兜底**（2026-11-19 上线）；gemini 内部次序为 flash-lite-latest（默认）→ flash-latest（兜底）；agnes 内部为 3.0-flash → 2.5-flash。
- 运维口径（审核 P2R-5 修正）：systemd 的 `EnvironmentFile=` 在启动时把 `.env` 灌进进程环境、且环境变量优先 — **`.env` 里启动时已有的键全部被冻结**，Key 看似热生效只是因为启动时为空。故所有配置键（含第二期新增参数）**改后必须重启 agnes-service 才生效**。unit 的 `EnvironmentFile=` 本期不删（保持现状，只把口径写对）。

---

## 2. 第二期实现范围（agnes-service 侧）

### 2.1 编排端点与作业

- 端点：`POST /api/reading/ingest` — 异步作业（202 + job_id）、状态查询、按文本/通道的作业列表（admin 可见全部，普通查询按属主过滤）。
- 请求：原文（或已 share 的 share_id）、标题、lang、通道凭据。调用方（2026-11-19 晚按认证设计回写）：agent 通道以 MuseAgent JWT 提交；**网页通道由浏览器同源调用**（8793 管理页的「添加文章」页签），账号 `reading_ingest='web'`，**只收 `title`（必填）/ `text`（必填）/ `lang`**，拒绝 `share_id` 与 `rate`（400 `unknown_field`，标题为空 400 `title_required`），`added_by / added_via` 由服务端写死 `web`。agnes-service **不开 CORS**（网页通道是同源调用，不需要）。
- **作业必须落库**（审核 P2R-1，blocker）：`auth_schema.sql` 幂等迁移新增 `agnes_auth.ingest_jobs`，字段至少含：id uuid、user_id、username、channel、added_by、share_id、base_version、stage、status、provider_model、chunks jsonb、trail、error_code、l2_reason、attempt、next_retry_at、created_at、updated_at、expires_at。服务启动时把未完作业（stage ∈ {share, preview, enrich, commit}）重新入队或标 retry；延迟重试由后台扫描器（BackgroundService，每分钟扫 `next_retry_at`）驱动。「同文只跑一份」用部分唯一索引 `UNIQUE (share_id) WHERE status IN ('queued','running','retry')`。
- **准入语义**（审核 P2R-2）：POST 时同步执行 Admit（429 与日额度当场返回），成功后立即释放并发 Ticket — 作业**不占** Generate 管线的每账号/全局并发槽；作业并发由独立 worker 池承担（`INGEST_WORKERS=2`）。call_log 在 POST 时写一行（kind=`ingest`、outcome=`accepted`），作业结束时 UPDATE 回填 model / outcome / duration / detail，不重复计额度。系统发起的重试与熔断试探不计用户额度，只记 detail（`retry` / `probe`）。
- **端点鉴权**（审核 P2R-16、P2R-17）：`agnes_auth.users` 加 `reading_ingest` 三值列（`none / web / agent`，默认 `none`）与 agent 映射（`muse_id` 或 .env `INGEST_AGENT_MAP`）。MuseAgent1/2 设 `agent`（开通走 AdminApi 或 CLI 并留审计，迁移脚本不写死 UPDATE）；**`web` 值本期启用**（2026-11-19 晚回写），授予用户本人日常账号，上线时由 operator 在管理页开通。端点在**准入前**判定：匿名 401、`none` 403 `ingest_forbidden`，均不扣额度。`reading_ingest` 进 `AuthService.UserState` 缓存，不逐请求查库。AdminApi 与建号 CLI 同步支持该字段的回显、修改与审计。
- **replace 前回读**（审核 P2R-13）：commit（replace）前先 `GET /api/v1/articles/{id}`，把 title、lang、rate、current_version 原样带回；标题为空时用正文前 20 字兜底。

### 2.2 L1 内部降级（参数 DF-7 已定，用审核起步值）

- **分块翻译必须带全文上下文**（审核 P2R-6，blocker）：每块 enrich 请求附 `context.full_text`（全文纯文本 ≤5000 字，只读、不要求输出），守朗读侧「全篇上下文翻译」硬规矩；prompt_version 升 `enrich-v2`。译名表第一版不做（块间并行，串行累积会拖慢）。
- **换 provider 时重新分块**（审核 P2R-7）：块大小挂在 ProviderDef 上（ChunkSents / ChunkChars / CallTimeoutS）；候选切换到另一个 provider 时，超其上限的块走现有拆块路径再拆。
- 参数起步值（均为新键，注意 §1.6：改后须重启）：

| 参数 | 定稿值 | 说明 |
|---|---|---|
| gemini 块 | 20 句 / 1500 字 | 与现行 MakeChunks 默认一致 |
| agnes 块 | 8 句 / 600 字 | 换到 agnes 时重新分块 |
| 单次超时 gemini-flash-lite | 45 s | 实测整篇 1.9 s，留足余量 |
| 单次超时 gemini-flash | 90 s | 实测会挂到 180 s，早止损 |
| 单次超时 agnes（3.0 / 2.5） | 120 s | 15 句约 199 s（含一次失败），8 句估计 60–100 s |
| `INGEST_DEADLINE_S` | 420 s | 从 enrich 开始计；300 s 对只剩 Agnes 的长文必然 partial |
| `/api/analyze` 直调 | 180 s / 600 s 不变 | 与编排解耦；`DeadlineSeconds` 改成可配置 |

- **熔断**（DF-6 已定，用审核修订）：按 provider+模型分别熔断，分瞬时 / 配额 / 鉴权三类。429 优先读响应 `retryDelay` / `Retry-After` 按它冷却，没有时冷却 2 分钟；403 带 quota 字样冷却 60 分钟；鉴权类熔 6 小时、每天试探 ≤6 次。保留降级设计稿 E7：同一 provider 连续 3 篇 `analyze_validation_failed` → 熔断其 API 路。**质量熔断本期只统计不自动熔断**：fixes 核对结果以 `fx=` 记入 call_log detail（§2.3），跑一段后按数据复盘再定阈值。熔断状态放内存可接受（重启后闭合，最坏多试一次）。
- **上游令牌桶放 Upstream 层**（审核 P2R-4）：按 provider 设桶（`UPSTREAM_RPM_AGNES=18`，编排子配额 ≤12/min 给交互请求留余量；gemini 按其项目限额），覆盖 text / image / video / analyze / ingest 全部 Agnes 调用 — Agnes 免费档有效 20 次/分钟是同类 Key 共享池，修复重试与换模型兜底都计入。拿不到令牌时编排排队等待，交互请求快速 429。
- **延迟重试**（网页通道 15 分钟 / 1 小时 / 4 小时）：一律**整篇重做**（审核 P2R-22），不把不同 provider 的译文拼进同一版本（守 DF-10「一个版本一个执行者」）；仍失败 → 标 L2 待办。

### 2.3 fixes 质量防线（实测强制）

实测：两家 provider 提的 fixes 均出现误改（把本来正确的读音改错，见 §3）。enrich 的 fixes **不许直接落库**，commit 前过核对层。本期做**本地分流**（审核 P2R-9 第 1 层，零依赖）：

- **补齐类**（原 token `r == null`、或 `unk=true`、或 `pos=num`）：过现有校验（平假名、送假名覆盖）即采纳 — 这是数字读音的主要收益来源。
- **改写类**（原 token `r` 非空）：默认**不采纳**，只记为可疑。仅两种放行：① 数字＋量词音变命中本地量词表（如 一/六/八/十/百 + 本/匹/杯/分/発/階… 的促音变）；② 经 TtsWeb 词典佐证接口（§5）确认。实测 3 条误改（大雪山「大」、日本一「日本」等）全属改写类，本层即可挡掉。
- 核对结果记入作业与 call_log detail：`fx=a{applied}/r{rejected}/s{suspect}`，作为后续质量复盘依据。
- 核对**不塞进 TtsWeb 的 commit 校验**（commit 也服务 L2 的 agy 与人工，人工读音可能超出词典）。
- 上线初期（前 20 篇 L0 文章）保留抽查：巡检每日汇总当日 L0 入库清单（含 fixes 采纳/拒绝明细），供用户或 A001 抽看；抽查期结束与否由用户定。

### 2.4 限额与记账（D5 已定）

- 编排用**独立 kind＝`ingest`**。连带改动列全（审核 P2R-3）：`auth_schema.sql` 两处 kind CHECK（usage_daily、call_log）幂等迁移加 `'ingest'`；`LimitService.Kinds` 与 `View`（used / counted_endpoints）；`AdminApi.ListCalls` 的 kind 白名单；管理页 kind 下拉。
- 一次 ingest 作业只准入 1 次；内部上游调用不重复计用户配额（但计入 §2.2 上游令牌桶）。
- call_log 的 model 记 `provider:model`、detail 记 trail 与 fixes 核对统计（沿用第一期格式）。

### 2.5 L2 待办的标记与交接（只判定不调度）

- 文章落 L2 时，agnes-service 在作业与文章侧写明：原因分类（api-down / quota / quality / deadline）、已尝试 trail、建议执行者固定为 agy-gemini。
- 提供只读的 L2 待办清单接口（供巡检者 A001 拉取）。**完成状态以自动对账为主**（审核 P2R-15）：清单被拉取时、以及后台扫描器每小时一次，对每条待办查 TtsWeb 文章状态，`status=ready` 且 `current_version > base_version` 即自动关闭并记录新版本号与 tr_via。回填接口保留为可选（用于报告失败原因）。
- L2 执行手册（ingest.md 的 L2 章节）由朗读项目侧维护（见 §5），执行者是 agy agent，不是本服务的代码职责。

### 2.6 前端（agnes-service 侧）

- 做粘贴入口（2026-11-19 晚回写，见 §1.5）：管理页 `index.html` 加「添加文章」页签（标题／语言／原文＋字数／保存按钮／短链与作业状态结果区／「最近保存」列表），`Program.cs` 加 `GET /add` 路由别名返回同一个 `index.html`，pathname 为 `/add` 时登录后直开该页签；草稿存 localStorage、保存成功后清除、401 弹登录框时不清空；所有回显用 `textContent`、不用 `innerHTML`。细节照设计 §7 第 4–5 条。
- 管理页另补：作业列表与 L2 待办可见（admin），kind 筛选补 `ingest`。
- 实施时删除 `X-Ingest-Key` 旁路（`IngestApi.Caller` 的 WebUser/ServiceUser 分支、`INGEST_WEB_KEY` / `INGEST_WEB_USER` 配置），`/health` 的 `ingest.web_trigger` 改为 `ingest.public_url_configured`；202 响应与作业视图的 `url` 在配了 `TTSWEB_PUBLIC_URL` 时拼 8791 绝对地址（设计 §7 第 1–3、7 条）。可选小修（设计 §7 第 9 条）：`/api/auth/refresh` 遇 `already rotated`（10 秒宽限内）不清 Cookie，前端稍等再刷一次，防双标签页互相登出。

---

## 3. 实测依据（2026-11-19，北海道一文全文 + 若干单句）

| 项 | Gemini（flash-lite-latest） | Agnes（3.0-flash / 2.5-flash） |
|---|---|---|
| enrich 整篇 15 句 | **1.9 秒**，一次成功 | 199 秒，3.0 失败后落 2.5 |
| raw 读音（汉字 token 对校对版） | **98/99**，唯一异读为可接受变体（街中 まちなか），硬伤 0 | 64/69，硬伤 4（北海道、訪れる、街中、下位），另 1 句整句失败 |
| 译文 | 整体忠实；句13 排名方向译反 1 处 | 整体可用、个别更润色；句13 方向正确 |
| fixes | 提 2 条，**全误改**（大雪山「大」、日本一「日本」） | 提 1 条，**误改**（大雪山「大」） |
| 其他 | flash-latest 本体两次超时约 180 秒，已降为兜底 | 免费档有效 RPM 20（官网口径），重试与兜底均计入 |

环境实测：
- instabox 公网网关约 **20 秒**掐断长请求（回平台话术、服务端请求随客户端断开被取消）→ 编排必须异步（§1.4）；agent 调用走内网/本地口或异步轮询。
- agnes-service 自身限额 10 次/分钟/用户，与上游 20/min 是两层账，都要节流（§2.2）。
- 整篇 raw（522 字）不可用：112 秒后校验失败（3.0 不合格 → 修复不合格 → 2.5 长度截断）。raw 只作短句调试，编排一律走 enrich。

---

## 4. 决策记录（2026-11-19 全部落定）

- DF-1 分级口径：采纳（含 T 态、执行者梯队 §1.1）。
- DF-2 added_via：加列，取值 agent / web / legacy，存量回填 legacy；连同 /share、commit 参数一起做。
- DF-3 share-first：采纳；share_id 兼作作业锁键。
- DF-5 巡检与演练：A001 **每小时 1 次**巡检（2026-11-19 晚用户定，由每日加密：拉 L2 清单 → 自有 agy 直接完成网页通道待办，L2 待办最长等约 1 小时；agent 通道落 L2 仍由发起者在 ddPC 调 agy），每月 1 次 L2 演练；L2 完成靠自动对账，巡检者不回填。**巡检时段：08:00–24:00（JST），夜间 00:00–08:00 不巡**（2026-11-19 用户定）；夜间落 L2 的待办由次晨 08:00 首巡处理。巡检定时任务随第二期上线时由 operator 一并建立（上线前接口不存在、不空转）；巡检口径：清单为空静默，只在处理了待办或失败时报告。
- DF-6 熔断参数：按 §2.2 审核修订值；质量熔断只统计（`fx=` 入 call_log），不自动熔断。
- DF-7 时间/分块参数：按 §2.2 定稿值表起步，冒烟后回填校准。
- DF-8 入口位置：8793 管理页「添加文章」页签 + `/add` 别名（2026-11-19 晚按认证设计改定，取代原「播放页按钮 + 8791 /add」）；播放页不设任何入口。
- DF-9 粘贴权限：网页通道走 8793 JWT（账号 `reading_ingest='web'`），由 agnes-service 用服务令牌代调 /share（2026-11-19 晚按认证设计改定，取代原「全在 8791 实现」口径）；ReaderNew 不参与；D2+C 反代与 A 案 CORS 作废；用户自选保存口令口径作废。
- DF-10 混合作业：L2 接手整篇重译、不拼半成品；L1 延迟重试同样整篇重做。
- DF-11 令牌白名单：登记 agnes-service 的 TtsWeb 服务令牌，`can_act_for={A001,A002,A003,web}`（2026-11-19 晚回写：网页通道经此令牌代调 /share，`web` 为其可代填的名字之一）。
- DF-12 朗读侧改动归属：由朗读项目实施，本服务只交付契约与令牌信息；清单按 §5 补全版。
- 评审 D5 记账：独立 kind `ingest`，一次作业准入 1 次，系统重试/试探不计额度。
- 评审 D6 gloss：第一版不做（enrich 有则透传），维持。
- 评审 D8 非 ja 文本：直接 /share 存原文、不走 enrich；服务端加假名占比兜底。
- 播放页版本：不再适用（播放页没有按钮，2026-11-19 晚回写）；只保留「短链指向 `/v3?s=`」。
- 审核 P2R-1…23：全部采纳并已并入 §2 / §5（其中 P2R-10 的 web_channel、P2R-19 / P2R-20 随 DF-9 改定不再适用，按新口径处理；P2R-17 的 web 值已按 2026-11-19 晚回写启用；P2R-23 存量 partial 并入 L2 清单列为实施可选、不阻塞验收）。审核第 4 个 blocker（A 案 refresh Cookie 冲突）随认证设计 R 案（单账号单会话）消解。

---

## 5. 朗读项目侧协调项（不属 agnes-service 实现，但第二期开工前要齐）

- **前置：ddPC 上的 agy** — 已就绪（2026-11-19 安装 v1.5.12、Google OAuth 登录完成、无头冒烟通过；账号 proseidon@gmail.com）。A001、A003 均可直接调度（供 agent 通道发起时当场接手）。我方 VM 上的 agy 为 A001 巡检处理网页通道 L2 待办的执行环境（2026-11-19 晚改定）；两处 agy 同一 Google 账号、共用额度池，tr_via 均为 `agy-gemini`。
- **TtsWeb 侧（网页通道）**：**不做任何网页通道实现**（2026-11-19 晚回写）— 播放页按钮、/add 页、服务端保存入口、保存口令脚本一概不做，播放页文件保持不动；网页通道入口在 agnes-service 侧（§1.5、§2.6）。
- **服务令牌签发**（DF-11）：在 TtsWeb `tokens.json` 登记 `agnes-service` 写令牌（只存哈希），`can_act_for={A001,A002,A003,web}`；明文写入 `tts-web/tokens/agnes-service.txt`（0600），再由 operator 写进 agnes-service `.env` 的 `TTSWEB_TOKEN`；两边改后都要重启。agnes-service 侧 `TTSWEB_TOKEN` 已在脱敏名单。令牌值不进任何文档、聊天或记忆。
- **reading DDL 与契约**（DF-12 补全版）：
  - `articles` 加 `added_via` 列；`/share` 与 commit 接受 `added_via`（缺省按令牌推断，旧调用留 legacy）。
  - tr_via 是 **CHECK 约束放宽**（不是加列）：写 DO 块迁移 DROP/ADD 同名约束加 `gemini-api` / `agnes-api`，同步改 `ingest.py` 的 `TR_VIA` 白名单；口径「CHECK 只放宽、不收紧」。DDL 实际涉及几处（本机镜像在 P5 后已由 ddPC SQLite 缓存取代）由朗读侧确认。
  - **词典佐证接口**（§2.3 第 2 层）：新增 `POST /api/v1/ingest/readings_check`（read 权限），入参 `[{sentence, tok, s, r}]`，服务端对该句 nbest（N≈20）收集与 token 边界对齐的读音候选，返回 `{supported, candidates}`；不可用时 agnes-service 退回本地分流。
  - `/v3/t` 响应加 status；`/v3/articles` 纳入 partial 文章。
- **宽限期提前**（审核 P2R-18）：TtsWeb `config.json` 的 `grace_until=2026-12-02` 意味着此前匿名可写 /share；第二期上线时把宽限结束提前到上线当天（前提：确认 A001/A003 都已带令牌调用）。
- TtsWeb /ingest.md 补：replace 补译口径、L2（agy 执行者）章节、VERSION_CONFLICT 收敛语义。
- **Mock TtsWeb**（审核 P2R-21）：在 MockAgnes 里加 `/share`、`/api/v1/ingest/preview|commit`、`/api/v1/articles/{id}` 的假实现，支持 dup / 409 / 503 / 401 等模式，稳定复现 VERSION_CONFLICT 与 T 态。
- 测试隔离：破坏性用例走 Mock；真实库只做只读核对与最终一篇真实冒烟（冒烟文由用户指定，入库即正式篇目，不做测完删除）。

---

## 6. 实施与验收顺序

0. **复核**：实施者（Claude）先复核本定稿与现行代码的一致性；发现 blocker 级问题先报告、不带病开工。
1. **朗读侧先行**（阻塞联调）：服务令牌签发、reading DDL 迁移、readings_check 等契约项（§5）。
2. **agnes-service 开发实例（8795）实现** §2 → Mock 全量回归（含第一期自检与 Mock 验收、原有 text / image / video 不回归）。
3. **agnes-service 侧实现网页通道**（§1.5、§2.6 与设计 §7 第 1–9 条：web 通道分支、删 X-Ingest-Key 旁路、/add 别名与页签、`TTSWEB_PUBLIC_URL`）；TtsWeb 侧只做令牌登记（设计 §7 第 10 条）。验收追加设计 §7 第 12–13 条（开通用户账号 `reading_ingest=web`、手机 /add 全链路、none 账号 403、无 CORS 头、页面无令牌、播放页 diff 为空、停 8791 时 202+share_id=null 等）。
4. **发布**：由 operator 执行 publish + 重启生产（8793 / 8791），生产冒烟（含一篇用户指定真实文章全链路），并建立 A001 每小时 L2 巡检任务（08:00–24:00）。
5. **回滚**：代码回退上一 publish 备份；DDL 只扩不回退，作业表与新列保留无害。
