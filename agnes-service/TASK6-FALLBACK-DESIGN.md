# TASK6 降级配合设计稿：API 深接（agnes-service）× 原驻地方案的 L0–L3 分级

日期：2026-11-19 ｜ 身份：设计者（只读设计，不实现）｜ 产出：仅本文件
依据：`TASK6.md`、`TASK6-REVIEW.md`（B1–B9、O5、D1–D8）、`/data/workspace/tts-redesign/design_proposal.md`（§2.2、§7.3.4、§8.2–8.3、待拍板第 4/18/20/23/26/32/36 条）、`/data/workspace/tts-redesign/impl/reading_ddl.sql`、P2a 落地代码与报告（`impl/service/{ingest.md, ingest_prompt.md, ingest.py, auth.py, store.py, P2a_report.md}`）、生产 TtsWeb 目录 `/data/workspace/tts-web/`、agnes-service 现有代码（`Program.cs`、`src/{Agnes,ApiError,CallLog,LimitService}.cs`、`auth_schema.sql`、`agnes-service.service`）。

> **本轮核实方式**：读文件；对本机端口只做 GET（`127.0.0.1:8791/ingest.md`、`127.0.0.1:8793/health`）和 `ss -ltn`。没有改动代码、配置、数据库或服务，没有调用 Agnes API；`.env` 只看了键名（`AGNES_API_KEY`、`JWT_SECRET`、`AGNES_DB_CONNECTION`）。
> **路径更正**：TASK6 §9.1 写的 `~/workspace/tts-redesign/reading_ddl.sql`，实际位置是 `/data/workspace/tts-redesign/impl/reading_ddl.sql`。

---

## 0. 先交代实况：评审时的前提已有变化（2026-11-19 核实）

| # | 事实 | 证据 | 对本设计的影响 |
|---|---|---|---|
| F1 | **P2 已在 8791 上线**。生产 TtsWeb 目录的代码与 P2a 落地稿逐字相同，`GET :8791/ingest.md` 返回 200 | `diff -q /data/workspace/tts-web/tts_web.py …/impl/service/tts_web.py` 无差异；`server.log` 在 2026-11-18 23:03 重启于 0.0.0.0:8791；`tts-web/config.json` 已指向 PG `db77database`/`reading_rw` | 评审 B1 的开工前提 (a)(b) 已经满足，(c) 令牌机制已存在（`tokens.json`），但还没有 `agnes-service` 这一项。下文的契约已对照**落地代码**核过，不再是「示意」 |
| F2 | commit 命中去重时**不看状态**：只要哈希命中，就返回 `duplicate:true` 并原样返回已有 status，**什么都不写** | `store.py` `commit()` → `_find_live_by_hash` 命中即 return | partial 文章不能靠「再 commit 一次」补译，**必须走 `replace:true + share_id + base_version`**。这是评审 B4 在 TtsWeb 侧的同一问题，现行 `/ingest.md` 的驻地运行手册没有写这一点（见 §4.3） |
| F3 | `/share` 现在也生成骨架，落为 **`partial`**（v1，`tool.kind=share`）；**PG 不可用时**改写本地 SQLite，并返回一个 reading 库里**不存在**的随机 ID | `tts_web.py:545-597`、`ingest.py plan_share` | 「降级到 raw」在新管线下实际就是 **partial（有骨架、无译文）**；`raw` 只剩迁移存量才有。另外 PG 宕机时 /share 回落 SQLite 拿到的短链，在 PG 恢复后会 404（`get_legacy` 先查 PG）——这是一个隐患 |
| F4 | commit 的 `tr_via` 由**代码**校验（`ingest.py` 中的 `TR_VIA` 集合），**DB CHECK 也校验** | `ingest.py:15`；`reading_ddl.sql:91` | 新增 `agnes-api`/`gemini-api` 不只是改 DDL，**TtsWeb 代码也要改一行**。这和 TASK6 §1「不改 TtsWeb 代码」冲突，须由朗读项目来改 |
| F5 | 一次 commit 的所有句子共用一组 `tr_by/tr_via/tr_model`（`tr_by` = 请求体 `executor`）；`versions.created_by` = 令牌名 | `store.py _insert_version`、`commit()` | 同一版本里**无法逐句记出处**。混合作业的出处规则必须按版本来定（§4.4） |
| F6 | 代填规则：`added_by` 为空或等于令牌名 → 通过；在令牌的 `can_act_for` 里 → 通过；否则 403 `AUTH_FAILED`。**只有 `ddpc-claude` 有 `can_act_for={A001,A002,A003}`，`77b-claude` 没有** | `auth.py resolve_added_by`；生产 `tokens.json`（只看了名字和作用域） | ① 网页通道把 Agnes 账号名写进 `added_by`，需要 TtsWeb 增加一条规则；② 77B 驻地顶上 L2 时，`added_by` 会记成 `77b-claude`，与第 23 条口径不符 |
| F7 | commit 要求「含汉字或数字的 token 读音齐全」，否则返回 `BODY_INVALID`（details.missing）；带 `allow_partial` 时落为 partial | `ingest.py:167-176, 206` | partial 有两种成因：缺译文，**或者缺读音**。enrich 必须产出 `readings`（数字是重灾区） |
| F8 | TtsWeb `/health` 只返回 `{"ok":true}`，不碰 PG | `tts_web.py:725` | 探测「TtsWeb 能写」不能用 /health，要用 `GET /api/v1/articles?since=<now>`（会访问 PG，PG 不可用时返回 503 `RETRY_LATER`） |
| F9 | agnes-service 会把 Agnes 的 401/403/429 **原样透传**给客户端（`Agnes.cs:90`）；`Generate()` 遇到上游 4xx 就退额度（`Program.cs:209-214`） | 代码 | 编排场景下，Agnes 401（key 失效）会被调用方误读成「自己的 JWT 失效」。编排端点必须把上游错误改包成自己的码（§2.1） |
| F10 | agnes_auth 角色只有 `user`/`admin`；用户名格式 `^[a-zA-Z0-9_.-]{3,32}$` | `auth_schema.sql:15,17` | 「A001」本身就是合法的 Agnes 用户名，**仅凭取值形态区分通道不可靠**（§7.2） |
| F11 | 77B 是 agnes-service、TtsWeb、PG 三者共同的宿主；77B 闲置 30 分钟会暂停 | design_proposal §8 前提；TASK6 §2 | **77B 不可用不属于 API 故障**，L2 也救不了：驻地同样要写 77B。这种情况单列为「T 态」，不算降级级别 |

---

## 1. 总览：分级表

**核心口径**：先存原文，再补译文（share-first）。不管 API 好坏，**第一步都是把原文经 `/share` 落成 partial、拿到短链**，此后各级都只是「谁、什么时候把这篇从 partial 升成 ready」。因此朗读和短链永远不依赖 API，降级只影响译文何时到齐。

| 级别 | 定义 | 触发条件 | 进入后做什么 | 成功标准 | 再失败往哪掉 | 决策者 |
|---|---|---|---|---|---|---|
| **L0 正常** | API 主路一次做完 | 默认；熔断器闭合 | `/share` 占位（partial，v1）→ preview → 分块 enrich（默认 `agnes:agnes-3.0-flash`）→ commit `replace:true, base_version=v1` | 文章 `status=ready`，当前版本的 `tr_via ∈ {agnes-api, gemini-api}` | L1 | agnes-service（自动） |
| **L1 API 路内部降级** | 仍由 API 执行，按以下顺序逐级收缩：<br>**L1a** 同模型修复重试（校验失败时带失败清单重试，每块 ≤1 次）<br>**L1b** 换模型：`agnes-3.0-flash → agnes-2.5-flash`（评审 O1）<br>**L1c** 换 provider：gemini（Key 已就绪时才轮到）<br>**L1d** 缩块：`finish_reason=length` 时对半拆块，不算校验失败<br>**L1e** partial 提交：到总时限时，已完成的块按 `allow_partial` 提交，其余句子 tr 为空；网页通道另排**延迟重试** | 某块或某次调用失败（分类见 §2.1）；或者到 `INGEST_DEADLINE_S` 还有块没完成 | 按块执行 L1a→L1d；整单到时限执行 L1e | 全部块通过 → ready（仍算 API 路成功，响应里 `level=L1`）；L1e 后的延迟重试补齐 → ready | L2（条件见右栏与 §2.3） | agnes-service（自动） |
| **L2 驻地接管** | 放弃 API 路，原方案执行者接手 | ① 熔断器显示 **API 路整体不可用**（所有已就绪 provider 都熔断）；② L1 用尽仍未 ready（agent 通道：本单结束即转；网页通道：延迟重试用尽或熔断持续 ≥2 h）；③ agnes-service 本身不可达或超过等待上限；④ 质量熔断（连续校验失败，§2.2）；⑤ agnes-service 写 TtsWeb 被拒（令牌 `AUTH_FAILED`） | 按原执行者顺位：**ddPC 驻地 → 77B 驻地（人工）→ agent 自翻**。驻地按 `/ingest.md` 续作：GET 文章 → 已 ready 就停 → preview → 整篇翻译 → commit `replace:true + share_id + base_version` | ready，当前版本 `tr_via=claude-code`（或 `self`），`tr_by=ddpc-claude/77b-claude/A00x` | L3 | **发起的 agent**（agent 通道）；**巡检 agent**（网页通道，§2.3）。agnes-service 只**声明**需要 L2，不调度 Claude |
| **L3 原有保底** | 原方案已有的降级：文章停在 partial，照常朗读 | 驻地也失败（commit 重试 2 次仍失败、额度耗尽、OAuth 过期） | 不再自动做任何事；驻地或 agent 按现行手册报告错误码；等下次巡检或人工处理 | 原文和骨架在库，短链可播，ruby 可显示 | （终点；任何一级恢复后都可以把它升成 ready） | 人 |
| **T 态（不算降级级别）** | 写入目标不可用：TtsWeb 挂了、PG 挂了，或者 77B 睡着 | preview 或 /share 连不上；`/api/v1/*` 返回 503 `RETRY_LATER`；或 agnes-service 连同 TtsWeb 一起不可达 | **不进入 L2**（驻地写的也是同一个 TtsWeb）。agnes-service 把原文留在作业表里退避重试（网页通道「保存不失败」靠这一步）；agent 通道直接返回 503 `ttsweb_unavailable`，由 agent 稍后重试 | TtsWeb 恢复后从 L0 继续 | 超过 24 h 仍不可用 → 作业标 `failed_ttsweb`，告知用户（原文仍在作业表里） | agnes-service（网页通道）；agent（agent 通道） |

与原方案降级口径的关系：
- 原方案「④ 失败只落骨架、status=partial、不阻塞朗读」（design_proposal §2.2 兜底）**原样保留**，对应 L3，而且在本设计里是**每篇文章的起点**（share-first），不是失败后才补的动作。
- TASK6 §8 第 4 步写的 `status:"raw"` 改为 **`partial`**：新管线下 /share 一律生成骨架（F3），`raw` 只剩迁移存量。响应里的状态**只用 reading 的三值**，不自创状态（遵守 TASK6 自己的「以契约为准」）。

---

## 2. 故障分类与判定

### 2.1 故障分类表（O5 细化 + 落地契约实况）

| # | 故障 | 识别方式 | 处理 | 是否计入熔断 | 是否触发 L2 |
|---|---|---|---|---|---|
| E1 | 上游超时或网络错误 | `Agnes.Call` 抛 504 或 502「Cannot reach」 | L1：同模型再试 1 次 → L1b → L1c | 计入（瞬时类） | 熔断打开后触发 |
| E2 | 上游 5xx | `UpstreamStatus` 为 5xx | 同 E1 | 计入（瞬时类） | 同上 |
| E3 | 上游 429 | `UpstreamStatus=429` | 按 `Retry-After`（没有就 20 s）等一次，再 L1b | 计入（瞬时类） | 同上 |
| E4 | 上游 403 配额（`insufficient_user_quota`） | 403，body 带 quota 字样 | **该 provider:model 立即熔断**（Agnes 的配额按模型生效，pro 403 时 flash 仍正常，见 TASK6 §2）→ L1b/L1c | 计入（致命类，一次即熔断） | 全部熔断后触发 |
| E5 | 上游 401 / key 失效 / key 未配置 | 401，或 503「not configured」 | **该 provider 全模型熔断**，冷却 6 h；改包成 502 `provider_auth_failed`，**不能**把 401 透传给调用方（F9） | 计入（致命类） | 全部熔断后触发 |
| E6 | 上游 400 参数类（非配额） | 其他 4xx | 视为本端 bug：不兜底，本块失败；告警进 call_log | 不计入 | 不触发（换执行者也解决不了「我们发错了」，但文章仍按 L1e 处理） |
| E7 | 返回非 JSON 或 JSON 校验不过 | §6 校验 | L1a 修复重试 1 次 → L1b | 不计入 provider 熔断；**计入质量熔断** | 质量熔断打开后触发 |
| E8 | 截断 `finish_reason=length` | 响应字段 | L1d 对半拆块 | 不计入 | 不触发 |
| E9 | commit 返回 `BODY_INVALID` 缺读音（details.missing） | TtsWeb 422/400 | 把 missing 清单交给模型，对涉及的块修复 1 次；仍缺 → `allow_partial`（L1e） | 不计入 | 不直接触发 |
| E10 | commit 返回 `TR_MISSING` | 同上 | 对缺句所在块修复 1 次 → `allow_partial` | 不计入 | 不直接触发 |
| E11 | commit 返回 `SPLIT_MISMATCH` | 同上 | 本端 bug（应该原样使用 preview 文本）；本单停在 partial，告警 | 不计入 | 触发（让驻地接手，同时修 bug） |
| E12 | commit 返回 `VERSION_CONFLICT` | 409 | **收敛规则**（§4.2）：重新 GET；已 ready → 本单以 `done_by_other` 结束；仍 partial → 以新 base 重提 1 次 | 不计入 | 不触发 |
| E13 | TtsWeb `AUTH_FAILED`（agnes-service 令牌） | 401/403 | 配置问题；API 路**写不进去**，等同 API 路不可用 | 打开「写入熔断」 | **触发**（驻地用自己的令牌能写） |
| E14 | TtsWeb 不可达、返回 503 `RETRY_LATER`、PG 不可用 | 连接失败或 503 | **T 态**：退避重试；不进 L2 | 不计入 API 熔断 | **不触发** |
| E15 | TtsWeb `BODY_INVALID`（原文问题：超 5000 字等） | 400 | 输入错误：直接返回 400 给调用方，不降级 | 不计入 | 不触发 |
| E16 | `NOT_FOUND`（410 文章在作业期间被删） | 404/410 | 作业以 `cancelled` 结束 | 不计入 | 不触发 |
| E17 | agnes-service 自身不可达（进程挂了、重启中、77B 睡着） | 调用方连不上，或 POST 超过 15 s 没有响应 | 调用方自行判定 | — | **触发**（agent 通道，由 agent 判定）；如果连 TtsWeb 也不可达，就是 T 态 |

### 2.2 熔断与冷却（参数建议，全部可配，放 `.env`）

熔断器按 **`provider:model`** 分别维护，另设两个全局熔断器。状态只放在进程内存里：单实例，重启后全部恢复闭合，这样可以接受，最坏也只是在一个已经死掉的模型上多试一次。

| 熔断器 | 打开条件 | 冷却 | 半开试探 |
|---|---|---|---|
| `provider:model`（瞬时类 E1–E3） | 10 分钟窗口内**连续 3 次**失败 | 5 分钟起，每次重新打开翻倍，上限 60 分钟 | 冷却到期后，**下一次真实调用**就是试探（只放行 1 个块）：成功则闭合、冷却复位；失败则重新打开 |
| `provider:model`（配额类 E4） | **1 次**即打开 | 60 分钟（Agnes 配额的重置口径**未核**，待实测后再调） | 同上 |
| `provider`（鉴权类 E5） | 1 次即打开（该 provider 全部模型） | 6 h；`.env` 的 key 发生变化时立即复位（注意评审 A-4：`AGNES_API_KEY` 由环境变量注入，改了要重启） | 同上 |
| **质量熔断**（E7） | 连续 **3 篇**文章最终 `analyze_validation_failed`（同一 `prompt_version`） | 不自动恢复，24 h 后半开 1 篇 | 下一篇成功才闭合；期间新单直接 L2 |
| **写入熔断**（E13） | 1 次 `AUTH_FAILED` | 不自动恢复（令牌配置问题），重启或改配置后复位 | — |
| **API 路整体** | 派生状态：所有 **ready 的** provider 的所有白名单模型都处于打开状态，或者质量/写入熔断打开 | — | — |

- **新单遇到「API 路整体」打开**：不在 API 上白等。agent 通道立即返回 `level=L2`（附 share_id 和 base_version）；网页通道直接排入待 L2，延迟重试仍按时间表执行，谁先完成都由 base_version 收敛。
- **低流量下的半开问题**：业务一天只有几篇，冷却到期后可能长时间没有真实调用来试探。规则：**只有在熔断打开、且有作业在等待时**，冷却到期才发 1 次最小真实调用（enrich 1 句，约数百 token）作为试探，每天最多 6 次。**熔断闭合时不做定时探测**（不花配额），这一点见 §5。
- 熔断状态写进 `/health` 的 `analyze.circuit`（只给状态和到期时间，不给 URL 和 key；`/health` 是公开端点，见评审遗漏项 9）。

### 2.3 判定者：切换决策落在真实存在的角色上

agnes-service **无法调用**驻地 Claude（没有通道、没有凭据；design_proposal 第 6 条也规定不设无人值守流水线）。因此：

| 决策 | 判定者 | 依据 |
|---|---|---|
| L0 → L1 的各子级 | agnes-service | 都在它自己的进程里完成 |
| 声明「本单需要 L2」 | agnes-service（写进作业状态和响应 `level:"L2"`，附 `handoff`） | 熔断状态和失败分类只有它看得到 |
| **执行 L2（agent 通道）** | **发起入库的 agent**（A001/A002/A003） | 它本来就是原方案里「经 Claude 协作通道转交驻地」的那个角色（design_proposal 8.2、第 6 条）；它还能自行判定 E17（agnes-service 不可达） |
| **执行 L2（网页通道）** | **巡检 agent**（推荐 A001，见 DF-5）：每天 1 次读取待补清单，逐篇转交驻地 | 网页通道没有 agent 在场，必须有一个定期出现的真实角色；用户也可以随时口头让任一 agent「补译」 |
| ddPC 驻地 → 77B 驻地 | 用户或 A001 人工 | 原方案第 32 条：77B 为人工备份，不做自动主备 |
| L3 的处置 | 人 | 原方案口径 |

**agent 通道的等待上限**：agent POST 后轮询作业，**最多等 8 分钟**（= 总时限 5 分钟 + commit 与余量）；超过上限或作业落到 `awaiting_l2`，就立即转驻地。agnes-service 之后才完成也不会冲突（§4.2 收敛）。

**巡检的待补清单从哪里来**：以 **TtsWeb 的 `GET /api/v1/articles`（status=partial）为准**（这是真源，agnes-service 挂了也能读），agnes-service 的 `GET /api/reading/ingest/jobs?state=awaiting_l2` 只作补充（附失败原因）。这样存量 partial 文章（评审 A-16）也会被顺带补齐。

---

## 3. 时间预算

### 3.1 各级超时参数（建议值）

| 参数 | 值 | 说明 |
|---|---|---|
| 分块 | 约 20 句或约 1500 字一块；发紧凑投影 `[{i,s,r,unk}]` | 评审 B6 |
| `CHUNK_TIMEOUT_S` | 90 | 单次上游调用；替代 TASK6 的 `ANALYZE_TIMEOUT_S=180` 在编排中的用法（`/api/analyze` 单独调用时仍用 180） |
| 每块尝试上限 | 3（含修复和换模型） | 评审 B6 |
| 块并行度 | 2 | 不额外占用 LimitService 的全局槽（编排只准入 1 次），但要控制上游压力 |
| `INGEST_DEADLINE_S` | 300 | 从 enrich 开始计；剩余时间不足时裁短单次超时；到点 → L1e |
| TtsWeb 单次请求超时 | preview 10 s；share/commit/GET 15 s | 都是秒级操作；超时按 E14 处理 |
| 延迟重试（仅网页通道） | +15 min、+1 h、+4 h，共 3 次 | 每次都受 300 s 总时限约束；熔断打开时推迟到半开 |
| agent 通道等待上限 | 8 min | §2.3 |
| T 态退避 | 1、5、15、60 分钟，之后每小时 1 次，最长 24 h | — |

### 3.2 一篇新文章从提交到 ready 的耗时估算

> 上游时延**没有实测**（本轮禁止调用 API）。单块 15–45 s 是按 flash 级模型、20 句输入输出量估的，须在第一期真实冒烟时实测并回填本表（标「待实测」）。驻地时长取自 design_proposal 8.2.3。

| 场景 | 短链可用 | 2000 字（约 4 块） | 5000 字（约 10 块） | 最坏 |
|---|---|---|---|---|
| L0 | **约 2 s**（share-first） | 约 1–1.5 min | 约 2–4 min | 5 min（到时限转 L1e） |
| L1（模型或 provider 兜底成功） | 约 2 s | 约 2–3 min | 约 4–5 min | 5 min 内 ready，或者 L1e partial |
| L1e + 延迟重试（网页通道，API 后来恢复） | 约 2 s | — | — | ≈ 5 min + 15 min + 1 h + 4 h + 3×5 min ≈ **5.5 h** |
| L2 · agent 通道 · 熔断已打开 | 约 2 s | 约 0.5 min 转交 + 2–8 min 驻地 | 0.5 + 3–10 min | **约 11 min** |
| L2 · agent 通道 · API 跑满 5 min 才失败 | 约 2 s | — | — | 8 min 等待上限 + 0.5 + 10 ≈ **19 min** |
| L2 · 驻地额度受限 / 需要 77B 人工 | 约 2 s | — | — | **数小时**（原方案 8.2.3 已接受） |
| L2 · 网页通道 | 约 2 s | — | — | 转入待 L2（延迟重试用尽约 5.5 h，或熔断持续 2 h）+ 巡检间隔 24 h + 驻地 10 min ≈ **30 h** |
| T 态（TtsWeb 或 77B 不可用） | **网页通道：恢复后才有**（页面先显示「已保存，短链生成中」）；agent 通道：返回 503 | — | — | 最长 24 h，之后报失败（原文在作业表里） |

### 3.3 用户实际感受到什么

- **短链和朗读**：除 T 态外，任何一级都在**约 2 秒内**拿到短链，能播放，骨架带 ruby（例外：数字 token 在补读音前 `r=null`，暂时不注音，F7）。
- **译文**：只有到达时间不同，L0 为分钟级，L2 为十几分钟到数小时，网页通道最坏约一天。页面 v3 对 partial 显示「译文待补」（design_proposal §2.2），v2 本来就不显示译文。
- **保存永不失败**：网页通道在 T 态下也能接住，原文先进作业表；只有 agnes-service 本身挂掉时页面才会报错，因为页面就是它提供的。

---

## 4. 交接与状态语义

### 4.1 作业状态（agnes-service 侧，新表 `agnes_auth.ingest_jobs`）

一篇文章在 reading 里只有三种状态（`raw/partial/ready`），**不新增**。编排的进度记在 agnes-service 自己的作业表里：

```
queued ─share ok─▶ shared(share_id, base_version=1)
   │                   │
   │ TtsWeb 不可达      ├─enrich+commit ok──────────────▶ done(ready, level L0/L1)
   ▼                   ├─L1e 部分提交───▶ partial_committed ─延迟重试 ok─▶ done
waiting_ttsweb ──恢复─▶ │                       │ 重试用尽或熔断≥2h
   │ 24h                ├─API 整体熔断────────▶ awaiting_l2 ◀┘
   ▼                   │                       │ 巡检 agent 接手 → 驻地 commit
failed_ttsweb          │                       ▼
                       ├─GET 发现已 ready──────▶ done_by_other（被他人先完成，丢弃本端结果）
                       └─输入错误/文章被删─────▶ failed_input / cancelled
```

建议列：`id uuid PK, channel ('agent'|'web'), user_id, username, added_by, text, title, lang, rate, share_id, base_version, state, level, attempts, next_retry_at, last_error_code, provider_trail, created_at, updated_at, finished_at`。保留 30 天，跟 call_log 的 90 天剪枝放在同一个定时器里（`CallLog.Prune` 的做法）。这张表同时解决 D4（异步作业）、网页轮询和 T 态下原文暂存三件事。

### 4.2 API 路死在半途时文章处于什么状态，以及驻地如何安全续作

因为 share-first，所以**任何半途状态下，reading 里都有一篇 partial 的文章（v1，有骨架、无译文），或者一篇 L1e 产生的 partial v2**：

| 死在哪一步 | reading 里的状态 | 续作方怎么做 |
|---|---|---|
| /share 之前（T 态） | 不存在 | agnes-service 恢复后从头开始；驻地如果抢先入库，content_hash 去重会让 agnes-service 的 /share 返回同一个 ID，下一步 GET 发现已经 ready → `done_by_other` |
| enrich 进行中（进程被重启杀掉，评审遗漏项 4） | partial v1 | 进程重启后扫描作业表里非终态的作业，重新执行（网页通道）；agent 通道在超过等待上限后已经转交 L2 |
| enrich 做了一半（L1e） | partial v2（部分 tr，`tr_via=agnes-api`） | 见 §4.4：驻地**整篇重译**，不复用 |
| commit 失败 | partial v1 | 同「enrich 进行中」 |

**续作的四条安全规则**（对 API 延迟重试和驻地同样适用，写进 `/ingest.md`）：
1. **先 GET 再动手**：`GET /api/v1/articles/{share_id}`。`status=ready` → 停止，什么都不写。
2. **补译一律走 replace**：`replace:true, share_id, base_version=<GET 得到的 current_version>`。**不要**对已存在的文章做普通 commit：它会返回 `duplicate:true`，什么都不写（F2）。
3. **`VERSION_CONFLICT` 就是收敛信号**：重新 GET；已经 ready → 停止；仍然 partial → 用新的 base_version 重提**1 次**，再冲突就停止并报告。这样两条路同时完成时，后到的一方要么发现已 ready 而放弃，要么拿到 409，**不会出现两个 ready 版本互相覆盖**。
4. **去重以 content_hash 为准**：同一原文无论从哪条路进来，都收敛到同一个 share_id（部分唯一索引 `articles_content_hash_uq`）。agnes-service **不另设**按文本加锁的机制来跨进程去重；进程内同文串行（评审 O6）可以后补。

**并发窗口**：「GET 看到 partial」到「replace 提交」之间可能被别人插入一版，由 base_version 的乐观校验兜住（`store.py _replace` 中的 `FOR UPDATE` + 比对 `current_version`），已核实。

### 4.3 base_version 与重复 commit 的收敛（对照落地代码）

- share-first 产生的是 v1，所以 **L0 的 commit 永远是 `replace` + `base_version=1`**，成功后生成 v2（`tool.kind=replace`）。代价是每篇文章多一个版本（v1 = share 骨架），体积可以忽略，版本全部保留（第 16 条）。
- `_replace` 会检查「替换后的原文与另一篇文章相同」（`BODY_INVALID existing_share_id`）。补译不改原文，不会触发。
- **P2a 遗留的分词差异**（P2a 报告 §4）：已迁移的 82 句是旧切法，preview 的下标可能与库里旧版本不一致。补译时一律以**当次 preview** 为准整篇提交（replace 会重切），不要拿库里旧 toks 的下标去填 readings。

### 4.4 出处记录：tr_via / tr_by / tr_model / versions

**版本粒度规则**：一个版本只有一个执行者（与 F5 的落地实现一致）。

| 场景 | `tr_by`（= commit 的 `executor`） | `tr_via` | `tr_model` | `versions.created_by`（= 令牌名） | `versions.tool` 追加 |
|---|---|---|---|---|---|
| L0/L1 全部由 Agnes 完成 | `agnes-service` | `agnes-api` | 实际模型，如 `agnes-3.0-flash` | `agnes-service` | `executor, model, prompt_version:"enrich-v1", level:"L0"\|"L1", channel, job_id, chunks:[{range, provider:model, attempts}]` |
| L1 中各块由不同模型或 provider 完成 | `agnes-service` | **句数最多的那个 provider**（同 provider 不同模型：`tr_via` 不变） | 句数最多的那个模型 | `agnes-service` | `chunks` 精确记录每块的 `provider:model`（版本级精确，句级近似） |
| L1e 部分提交 | `agnes-service` | 同上 | 同上 | `agnes-service` | `partial:true, done_chunks/total_chunks` |
| L2 驻地接手 | `ddpc-claude` / `77b-claude` | `claude-code` | 如 `claude-opus-5-5` | 驻地令牌名 | `prompt_version`（`ingest_prompt.md` 的 v1）、`takeover_from:{job_id, reason}`（可选） |
| L2 agent 自翻 | `A00x` | `self` | 自填 | `A00x` | — |

**混合作业（一半 API、一半驻地）的规则：不在同一版本里混合。** 驻地接手时**整篇重译**，不复用 API 已译的块，理由有三：① 落地契约做不到逐句记出处（F5），复用会把 API 的译文错记成 `claude-code`；② 「通读全篇、风格统一」是硬规矩（修订注记 4/5），拼接两种来源违背它；③ 驻地翻一篇只要几分钟，省下的很少。API 留下的部分译文**保留在历史版本里**（v2 partial），可以回看比对。
- 如果日后确实要复用：恢复原 7.2 设计里的「item 级 `translator` 覆盖」（commit 的 sentences[] 可以带 `tr_via/tr_model`），属于 TtsWeb 代码改动，列为「可后补」（§6.2 R7）。

`added_by` 不受降级影响：始终是 share-first 那一步写进去的值（agent 通道是 A00x，网页通道是 Agnes 账号名）。replace **不改** `added_by`（`_replace` 的 UPDATE 不含该列，已核实）。

---

## 5. 备份保鲜清单（最小可执行版）

原则：每一项都有**明确的执行者、频率和记录位置**，并且尽量复用已有的东西，不新建运维系统。

| # | 项 | 做法 | 频率 | 执行者 | 记在哪里 |
|---|---|---|---|---|---|
| K1 | 驻地 OAuth 续期 | 沿用第 36 条：ddPC 在 2026-12-10、77B 在 2026-12-13 到期，各提前 3 天提醒 | 按到期日 | 已有提醒机制 | 已有 |
| K2 | 驻地令牌有效 | 驻地用自己的 `TTSWEB_TOKEN` 执行 `GET /api/v1/articles?since=<now>`（读接口，写令牌也能用；不写库）；200 即有效 | 合并到 K4 演练里 | 演练执行者 | 演练文章的版本记录 |
| K3 | `/ingest.md` 和 `ingest_prompt.md` 时效 | 两份文件头部已有 `ingest_api: v1`、`prompt_version` 与日期；**规则**：TASK6 或降级相关的改动合并进 `/ingest.md` 时，必须同步升级 `ingest_api` 版本并改日期；演练时驻地先读取并回报版本号 | 每次契约变更，加上 K4 | 朗读项目维护者（第 34 条：77B 侧） | 文件头 |
| K4 | **L2 演练** | 固定一篇**演练专用文章**（标题 `[drill] L2 演练`，约 100 字，内容固定，含数字和同形多读各一处）。演练 = agent 绕开 agnes-service，直接把 share_id 转交驻地 → 驻地按 `/ingest.md` 执行「GET → preview → 翻译 → **replace**」。每次演练产生一个新版本，**版本表本身就是演练记录**（created_by、tr_model、prompt_version、时间），不需要清理，也不需要另建日志 | **每月 1 次**（建议每月 1 日）；契约或模板改版后加演 1 次；OAuth 续期后加演 1 次 | 巡检 agent（A001，DF-5）发起；ddPC 驻地执行；每季度改由 77B 驻地执行 1 次（演练人工备份） | reading.versions（演练文章）；失败时 agent 向用户报告错误码 |
| K5 | API 路健康（被动） | `/health.analyze`：provider 是否 ready（只看 key 配置）+ 熔断状态（§2.2）。**不定时发真实调用** | 实时 | agnes-service | /health |
| K6 | API 路健康（主动、有代价） | 只在熔断打开且有作业在等时，做半开试探（每天最多 6 次 × 约数百 token）；另外每月 K4 演练日**顺带**用 API 路入库一篇 1 句的测试文 = 一次真实小调用（每月 1 次，配额代价可以忽略） | 见左 | agnes-service；agent | call_log（kind=analyze，detail 带 `probe`） |
| K7 | agnes-service → TtsWeb 写令牌 | API 路每次 L0 都会实际使用；失效即触发写入熔断 → 新单自动 L2，并在 `/health` 显示 | 实时 | agnes-service | /health、call_log |

不做的：不建监控面板，不定时跑全链路合成调用，不做驻地心跳。按一天几篇的业务量，这些没有人看，也没有人执行。

---

## 6. 两侧改动清单

标注：**【必须先定】**＝实现前必须定稿；**【可后补】**＝上线后再补也不影响正确性。

### 6.1 TASK6 侧（agnes-service）

| # | 改动 | 标注 |
|---|---|---|
| A1 | **share-first 编排**：§8 流程改为「/share → GET 取 base_version → preview → enrich → commit replace」；`status:"raw"` 改为 `partial`；对已存在文章按 status 分支（ready → `deduplicated:true`；partial → 照常补译，响应 `upgraded_from:"partial"`），以此落实评审 B4 | 【必须先定】 |
| A2 | **异步作业**：`POST /api/reading/ingest` 在 share 成功后立即返回 `{job_id, share_id, url, state, level}`（HTTP 202）；`GET /api/reading/ingest/{job_id}` 用于轮询；新表 `agnes_auth.ingest_jobs`（§4.1），写进 `auth_schema.sql` 的幂等段。此项落实 D4，推荐异步 | 【必须先定】 |
| A3 | **错误码区分可重试和致命**：响应和作业里加 `retryable: bool` 与 `level`；编排端点自有错误码：`ttsweb_unavailable`(503, retryable, T 态)、`ttsweb_rejected`(400，原码放进 details，致命)、`provider_auth_failed`(502，替代透传 401，见 F9)、`api_unavailable`(用作作业态/level=L2 的原因码)、`analyze_validation_failed`(502)、`ingest_forbidden`(403，通道权限)。call_log 的 outcome 用同一套码 | 【必须先定】 |
| A4 | **熔断器**（§2.2）+ `/health.analyze.circuit` 段（只给状态） | 【必须先定】（不做熔断就会出现「每篇都先在死 API 上白等 5 分钟」） |
| A5 | 分块、紧凑投影、`CHUNK_TIMEOUT_S`、`INGEST_DEADLINE_S`、L1d 拆块（评审 B6）。**分块不得违反「全篇上下文翻译」硬规矩**（修订注记 4/5）：每块请求都附上**全文纯文本**（≤5000 字）作为上下文，外加一张随文累积的译名表（前面各块已经定下的人名、地名译法），只要求输出本块各句；骨架投影只发本块 | 【必须先定】 |
| A6 | enrich 输出对齐 commit 契约：`fixes{tok_i,reading}` 改为 commit 的 **`readings{tok, s, r}`**（带 `s` 用于核对，正好落实评审 O8）；**必须**给出所有 `r=null` 的含汉字或数字 token 的读音（F7）；`unk` 的拉丁字母 token 不给读音（评审遗漏项 6）；gloss 默认不提交（B5，契约没有落点） | 【必须先定】 |
| A7 | **commit 出处字段**：`executor:"agnes-service"`、`tr_via` 按 provider、`tr_model`、`prompt_version:"enrich-v1"`；`versions.tool` 的追加键按 §4.4，依赖 TtsWeb 把这些键透传进 tool（R6） | 【必须先定】 |
| A8 | **通道与身份**：请求体 `channel: "agent"|"web"`；agent 通道 `added_by ∈ {A001,A002,A003}`（必填），需要账号具备 agent 通道权限；网页通道 `added_by` 由服务端强制写成 JWT 用户名，请求体里的值忽略。取代 TASK6 §8「admin 才能代填」的规则（§7.1） | 【必须先定】 |
| A9 | 权限列 `agnes_auth.users.reading_ingest text NOT NULL DEFAULT 'none' CHECK (reading_ingest IN ('none','web','agent'))`；admin 视为两者皆可；管理页可编辑（§7.1） | 【必须先定】 |
| A10 | 网页通道延迟重试 + T 态退避（单个后台 `PeriodicTimer`，每 60 s 取到期作业，串行执行，同时最多跑 1 个），进程启动时恢复非终态作业 | 【必须先定】（「保存不失败」的承诺靠它） |
| A11 | 前端「入库」页签（§7.4 方案 a） | 【必须先定】（用户追加需求） |
| A12 | commit 阶段使用独立的 CancellationToken（客户端断开不影响已在跑的作业；异步化后自然满足，评审遗漏项 5） | 【必须先定】（A2 落地后自动成立） |
| A13 | `Redact` 增加 `TTSWEB_TOKEN`、`GEMINI_API_KEY`（评审 B7） | 【必须先定】 |
| A14 | 质量熔断与写入熔断 | 【可后补】（缺了它们也只是多等几篇失败才转 L2） |
| A15 | 进程内同文串行锁（评审 O6） | 【可后补】 |
| A16 | 作业列表接口 `GET /api/reading/ingest/jobs?state=awaiting_l2`（给巡检用，附失败原因；巡检的主清单仍是 TtsWeb 的 partial 列表） | 【可后补】 |

### 6.2 朗读方案侧（TtsWeb / reading / 文档）

| # | 改动 | 标注 |
|---|---|---|
| R1 | **`tr_via` 扩值 `agnes-api`、`gemini-api`**：三处 DDL（`impl/reading_ddl.sql`、77B 库、本机镜像库）**再加** `ingest.py` 的 `TR_VIA` 集合（F4，代码改动，归朗读项目） | 【必须先定】 |
| R2 | **登记 `agnes-service` 写令牌**：`can_act_for={A001,A002,A003}`（agent 通道）+ 新属性 `web_channel:true`（网页通道，§7.2）；令牌哈希写入 `tokens.json`，按评审遗漏项 11 的交付方式办理 | 【必须先定】 |
| R3 | **通道标记**：`articles` 增加列 `added_via text CHECK (added_via IN ('agent','web','legacy'))`（可空，存量为 NULL 或 `legacy`）；`/share` 与 commit 接受 `added_via`；`resolve_added_by` 增加规则：`added_via=web` 时要求令牌带 `web_channel:true`，`added_by` 匹配 `^[a-zA-Z0-9_.-]{3,32}$` 且**不匹配** `^A\d{3}$`（§7.2） | 【必须先定】（DF-2 如果选「不加列」，改走后备方案） |
| R4 | **`77b-claude` 令牌补上 `can_act_for={A001,A002,A003}`**（F6），否则 L2 落到 77B 时 `added_by` 会记错 | 【必须先定】 |
| R5 | **`/ingest.md` 修订**（要点见下） | 【必须先定】 |
| R6 | commit 把请求体中的 `tool_extra`（对象，≤2 KB）合并进 `versions.tool`，用于记录 level/channel/job_id/chunks | 【可后补】（缺了只是少记一些审计信息） |
| R7 | commit 的 `sentences[]` 支持 item 级 `tr_via/tr_model` 覆盖（逐句出处） | 【可后补】 |
| R8 | `/share` 在 PG 不可用回落 SQLite 时，响应里加 `"fallback":"sqlite"`（现在调用方分辨不出，F3），让 agnes-service 能把它当 T 态处理 | 【可后补】（agnes-service 先用「share 后立即 GET，404 或 503 即视为 T 态」来规避） |
| R9 | design_proposal 增加修订注记 12（§8 成稿） | 【必须先定】 |

**R5：`/ingest.md` 的改法要点**
1. 新增一节「执行者顺位」：**① agnes-service API（自动，L0/L1）→ ② ddPC 驻地 → ③ 77B 驻地（人工）→ ④ agent 自翻**；说明 agent 何时绕开 ①（agnes-service 不可达、作业 `level=L2`、等待超过 8 分钟）。
2. 驻地运行手册新增**第 0 步「续作检查」**：任务带 share_id 时，先 GET；`ready` 就停；`partial` 就记下 `current_version`。
3. 第 5 步改为：**已存在的文章用 `replace:true + share_id + base_version`**，新文章才用普通 commit；明写「对已存在文章做普通 commit 会返回 duplicate:true 并且什么都不写」。
4. 第 6 步补充：`VERSION_CONFLICT` 时重新 GET，已 ready 就停，否则以新 base 重提 1 次。
5. 第 6 步的「失败就 /share 存原文」保留（新文章场景）；对已有 share_id 的续作不需要再 /share。
6. 回报格式新增 `takeover_from`（作业号或原因，可选）。
7. 鉴权一节：说明 `agnes-service` 令牌的两种代填形态（A00x 与 Agnes 账号名），驻地令牌只能代填 A00x。

### 6.3 调用方（agent 入库手册与流程）

| # | 改动 | 标注 |
|---|---|---|
| C1 | agent 入库默认改为：`POST agnes-service /api/reading/ingest {channel:"agent", added_by:"A00x", text, title}` → 立即拿到短链并可回报用户「已可播放」→ 轮询作业（每 15 s，最多 8 分钟）→ `done` 就回报；`level=L2`、`awaiting_l2`、超时或连不上 → 按原流程转交 ddPC 驻地，**并带上 share_id**（让驻地走续作分支） | 【必须先定】 |
| C2 | agent 需要一个 Agnes 账号（推荐每个 agent 一个，`reading_ingest='agent'`，例如 `muse-a001`），JWT 用账号密码登录换取；凭据放进该机器的 Secrets | 【必须先定】（DF-4） |
| C3 | 巡检任务（A001）：每天 1 次，读 TtsWeb partial 列表 → 逐篇转交驻地；每月 1 日执行 K4 演练 | 【必须先定】（网页通道 L2 靠它） |
| C4 | 入库完成后顺带触发镜像 pull（第 38 条，原有动作，不变） | 不变 |

---

## 7. 前端粘贴入库

### 7.1 授权

- **入口可见性**：页签只在登录后、且 `/api/auth/me` 返回的用户具备 `reading_ingest ∈ {web, agent}` 或 `role=admin` 时显示；匿名用户看不到。端点本身另外校验：匿名 401，没有权限 403 `ingest_forbidden`（**准入前**判定，不扣额度）。
- **权限粒度（推荐）**：**按账号单独开通**，即新列 `reading_ingest`（A9），不是「所有登录用户」。理由：入库会写进家庭朗读库的公共真源，还会消耗 Agnes 配额。现有的 `role` 只有 user/admin 两档，不适合为一个功能新增角色；一列开关最便宜，管理页加一个下拉框即可。
  - `none`：默认，看不到入口；
  - `web`：可以使用粘贴入口，`added_by` 强制为本人账号名；
  - `agent`：给 agent 用的账号，可以使用 agent 通道并在 {A001,A002,A003} 内代填（同时允许 web）；
  - `admin`：两者皆可。
- **准入与计数**：一次粘贴保存 = **1 次 `analyze` 准入**（与 TASK6 §7 的编排口径一致），计入每日总量、每分钟和并发；延迟重试、半开试探属于**系统发起**，不再向用户计数（只记 call_log，`detail` 标 `retry`/`probe`）。T 态下作业还没有调用上游，也照样计 1 次（准入发生在保存时，保持简单）。D5 是否按块计数，仍按评审的待拍板项处理，本设计不改变它。

### 7.2 身份口径（按用户已拍板的口径：不做映射）

- **区分来源的方式（推荐：加通道标记列，不只靠取值形态）**：`reading.articles.added_via ∈ {agent, web, legacy}`（R3），`added_by` 保持「真实提交者」的原值：agent 通道为 `A001/A002/A003`，网页通道为 Agnes 账号名（如 `TestUser002`）。
  - 不能只靠形态区分的理由：Agnes 用户名的规则 `^[a-zA-Z0-9_.-]{3,32}$` 允许出现 `A001` 这种名字（F10），只看形态会产生歧义；加一列的成本可以忽略，而且可以和 R1 的 `tr_via` DDL 同一批落地（反正都要动 DDL 三处）。
  - 同时做一道**防撞**：TtsWeb 在网页通道拒绝 `^A\d{3}$` 形态的 `added_by`；agnes-service 管理页新建账号时也禁止使用这一形态。即使有人没看 `added_via` 只看 `added_by`，也不会把网页用户误认成 Muse agent。
  - 后备方案（DF-2 如果否决加列）：`added_via` 改记在 `versions.tool.channel`（jsonb，不需要 DDL）+ 上述防撞规则。缺点是要查 v1 的 tool 才知道文章来源。
- **TtsWeb 契约配合**：
  - 驻地令牌（`ddpc-claude`、`77b-claude`）的白名单 {A001,A002,A003} **只管 agent 通道**，不变（R4 给 77b-claude 补上）。
  - `agnes-service` 令牌：`added_via=agent` → `added_by` 必须在 `can_act_for={A001,A002,A003}` 内；`added_via=web` → 令牌须带 `web_channel:true`，`added_by` 满足账号名格式且不是 `A\d{3}`，**契约明确放行这一形态**。其他令牌传 `added_via=web` 一律 403 `AUTH_FAILED`。
  - **防冒名边界**：TtsWeb 无法核实「这个账号名确实是登录用户」，这一点由 agnes-service 保证（网页通道的 added_by 只能取自 JWT，请求体的值被忽略，A8）。信任链是「TtsWeb 信任 agnes-service 令牌，agnes-service 信任 JWT」，这一点写进 `/ingest.md` 的鉴权节。

### 7.3 降级衔接（写进分级表与状态语义）

| 用户点「保存」时 API 路的状态 | 立即看到 | 之后 |
|---|---|---|
| L0/L1（正常或内部降级） | 「已保存」+ 短链（可以直接打开朗读）+ 「译文生成中」 | 页面轮询作业，1–5 分钟后显示「译文已就绪」 |
| API 整体熔断（L2 状态） | 同上，提示改为「译文待补（API 暂不可用，恢复后自动补齐，或由驻地补译）」 | 延迟重试；用尽后转待 L2，由巡检交给驻地 |
| T 态（TtsWeb/77B 写不进） | 「已保存，短链生成中」（原文已进作业表） | 退避重试；恢复后页面显示短链；24 h 仍失败则显示「保存失败，原文已保留，请联系管理员」并提供原文复制按钮 |
| 输入错误（超 5000 字等） | 准入前就报错，**不算保存**，不计数 | — |

结论：**保存永不失败，译文异步补齐**。唯一的例外是 agnes-service 本身不可用，这时页面根本打不开，属于 E17。

### 7.4 入口位置两案评估

| 角度 | (a) agnes-service 前端新页签 | (b) 朗读 v3 页（P4，Vue）加入口，调编排端点 |
|---|---|---|
| 跨域与令牌 | **同源**：现有 JWT（内存）+ refresh cookie 直接可用，不需要 CORS | v3 在 8791 的 instabox 子域，agnes-service 在 8793 子域，属于**跨域**：要给 agnes-service 开 CORS；refresh cookie 是 8793 主机的 cookie，在 8791 页面上属于第三方 cookie，浏览器默认受限 → v3 需要自建一套登录和 token 保存，等于把账号体系复制一份到另一个站点 |
| 用户动线 | 登录 agnes-service → 「入库」页签 → 粘贴保存 → 点短链跳到朗读页。多一次跳转，但有权限的人本来就在用 agnes-service（文本、图片、视频页签都在那里） | 在朗读页里直接添加，最顺手；但朗读页的主要场景是手机和**电视 webOS**，在那里输入长文本不现实 |
| 维护归属 | 全部在 agnes-service 一个仓库：页签、端点、权限、作业都由同一方维护；TtsWeb 只做 R1–R4 | 前端在朗读项目，后端在 agnes-service，**跨项目耦合**：两边的契约变化要同步发版 |
| P4 工期 | 不影响（P4 = 1.5 人日，保持不变） | 要加登录、CORS 联调、权限显示，估计 +0.5–1 人日，并且要排在已经很紧的「播放内核原样移植、webOS 真机」验收里，**增加 P4 风险** |

**推荐 (a)**。否决 (b) 的理由：跨域加第三方 cookie 会迫使朗读页另建一套登录，违背「复用 agnes-service 现有账号体系」的要求；同时把 P4（播放核心移植）和一个与播放无关的写入功能绑在一起发版，平白增加 P4 风险；主要终端（电视）也不适合粘贴长文。
**可后补**：v3 页角放一个普通链接「＋添加文章」，跳到 agnes-service 的入库页签（零耦合，不涉及跨域调用），列为 DF-8。

---

## 8. 拟并入 design_proposal 的修订注记（成稿，可直接粘贴到文首注记区）

> **修订注记 12（2026-11-19，API 执行者并入与多级降级·待用户确认）**：入库执行者新增 **agnes-service API（Agnes / Gemini，TASK6 深接法）作为主路**，原驻地方案降为**随时可以接手的备份**，两者按以下分级配合：**L0** API 一次做完；**L1** API 路内部降级（同模型修复 → 换模型 → 换 provider → 缩块 → 到时限 partial 提交，网页通道另有延迟重试）；**L2** 放弃 API，按原顺位 ddPC 驻地 → 77B 驻地（人工）→ agent 自翻接手；**L3** 即原有保底（partial、只有骨架、照常朗读）。TtsWeb 或 77B 不可用不算降级，按「稍后重试」处理（L2 同样要写 77B）。配套口径：① **先存原文、后补译文**：每篇文章先经 `/share` 落为 partial 并拿到短链，各级只负责把它升成 ready；② **补译一律 `replace:true + share_id + base_version`**，动手前先 GET，已 ready 就停，`VERSION_CONFLICT` 视为收敛信号；③ **一个版本只有一个执行者**，驻地接手时整篇重译，不与 API 的部分译文拼接，API 的部分译文留在历史版本；④ 新增 `tr_via` 取值 `agnes-api`、`gemini-api`（`tr_by='agnes-service'`），`articles` 新增 `added_via ∈ {agent, web, legacy}`；`added_by` 按通道记真实提交者（agent 通道记 A00x，网页通道记 Agnes 账号名，不做映射）；⑤ `77b-claude` 令牌补上与 `ddpc-claude` 相同的代填白名单；⑥ L2 的切换由发起的 agent（agent 通道）或每日巡检 agent（网页通道）执行，agnes-service 只声明不调度；⑦ 保鲜：每月 1 次 L2 演练（固定演练文章 replace，版本表即记录），OAuth 续期提醒沿用第 36 条。待拍板第 4、6、23、26、32 条据此补充，不推翻：仍然没有 Claude API，仍然不设无人值守的 Claude 流水线（agnes-service 的延迟重试只调用它自己的 API，不调度 Claude）。详见 `agnes-service/TASK6-FALLBACK-DESIGN.md`。

---

## 9. 不做什么

| 排除项 | 理由 |
|---|---|
| 两条路**同时**开跑、谁先完成用谁（双写竞速） | 每篇都要花两份成本（API 配额 + Pro 额度）；同一篇会出现两个版本竞争；而 L2 的分钟级延迟是可以接受的（design_proposal 8.2.3：速度不是选择依据） |
| agnes-service 直接调度驻地 Claude 会话（SSH、`claude -p`） | 它没有通道和凭据，也不该拥有；违背第 6 条（不设无人值守的 Claude 流水线）；会把 Claude 订阅凭据暴露给一个公网服务 |
| 驻地与 API 在**同一版本**里逐句拼接译文 | 落地契约记不了逐句出处（F5），并且违背「全篇统一翻译」的硬规矩（§4.4） |
| 把「TtsWeb、PG 或 77B 不可用」当作 API 故障转 L2 | 驻地写的是同一个目标，转过去也写不进（O5、F11）；只做 T 态重试 |
| 自动的 ddPC → 77B 驻地主备切换 | 原方案第 32 条：人工备份，自动切换不现实也不必要 |
| 定时全链路合成探测、监控面板、驻地心跳 | 业务量一天几篇，没有人看；改为被动熔断 + 有作业等待时才试探 + 每月演练 |
| 熔断状态持久化、多实例共享 | 单实例；重启后状态清空，最坏多试 1 次 |
| 跨体系身份映射表 | 用户已拍板（B2 关闭） |
| 网页通道的 `added_by` 由前端填写 | 防冒名：只能取自 JWT |
| agnes-service 在 TtsWeb 不可用时自己写 SQLite 或 PG reading | 第二条写入路径，绕过 TtsWeb 的校验和去重；原文暂存在作业表里足够 |
| 在朗读 v3 页（P4）里做入库入口 | §7.4 |
| 为降级额外引入队列中间件（Redis 等） | 一张作业表 + 一个进程内定时器就够了 |

---

## 10. 需用户拍板

| # | 事项 | 推荐答案 |
|---|---|---|
| **DF-1** | 分级口径：L0/L1/L2/L3 + 不算级别的 T 态；L1 子级顺序为「修复 → 换模型 → 换 provider → 缩块 → partial」 | **按 §1 采纳** |
| **DF-2** | 通道来源区分方式：`articles.added_via` 新增列（与 tr_via DDL 同批，三处同落），还是不改 DDL、记进 `versions.tool.channel` | **加列**，并同时规定 `^A\d{3}$` 形态保留给 Muse agent |
| **DF-3** | share-first（每篇先 /share 占位，L0 也走 replace，每篇多一个 v1 版本） | **采纳**：短链立即可用，各级交接统一走 replace |
| **DF-4** | agent 通道使用哪个 Agnes 账号：每个 agent 一个账号（`reading_ingest='agent'`），还是三个 agent 共用一个 | **每个 agent 一个**（call_log 能分辨是谁，吊销互不影响） |
| **DF-5** | 网页通道 L2 的巡检者与频率 | **A001，每天 1 次**；同时负责每月 1 日的 L2 演练；用户可以随时口头触发「补译」 |
| **DF-6** | 熔断参数：瞬时类连续 3 次 / 5→60 分钟翻倍；配额类 1 次 / 60 分钟；鉴权类 1 次 / 6 h；质量熔断连续 3 篇；熔断时才试探，每天最多 6 次 | **按 §2.2 采纳**，配额冷却待第一期实测 Agnes 重置口径后再调 |
| **DF-7** | 时间参数：`CHUNK_TIMEOUT_S=90`、`INGEST_DEADLINE_S=300`、agent 等待上限 8 分钟、网页延迟重试 15 分钟/1 小时/4 小时、T 态最长 24 h | **采纳**，第一期冒烟实测单块时延后回填 §3.2 |
| **DF-8** | 入口位置 | **(a) agnes-service 新页签**；v3 页角放跳转链接，可后补 |
| **DF-9** | 粘贴入库的权限粒度：按账号开通（`reading_ingest` 列），还是所有登录用户可用 | **按账号开通**，默认 `none` |
| **DF-10** | 混合作业：驻地接手时整篇重译、不复用 API 的部分译文；逐句出处（R7）推迟 | **采纳** |
| **DF-11** | `77b-claude` 令牌补上 `can_act_for={A001,A002,A003}`（改朗读项目配置） | **补**，与第 23、26 条一致 |
| **DF-12** | TASK6 §1「不改 TtsWeb 代码」的例外：`TR_VIA` 扩值（R1）和 `added_via`、`web_channel` 规则（R3）需要改 TtsWeb 代码，由朗读项目实施 | **同意例外，由朗读项目改**（P2 已上线，不存在抢文件问题；改完后按 R5 同步 `/ingest.md`） |
| **DF-13** | 编排同步还是异步（即评审 D4） | **异步作业**（202 + job_id 轮询）；网页通道的「保存不失败」和 L2 交接都依赖它 |

> 评审 D1 已由事实替代（F1：P2 已在 8791 上线），不再列入。D3（tr_via 取值）并入 DF-2 和 R1 的推荐：新增 `agnes-api`、`gemini-api`，不复用 `agy-gemini`。D5–D8 本设计不涉及，维持评审原状。

---

## 附：仍未核实、需要在实现期确认的事项

- Agnes 403 配额的重置时间（按天还是滚动），影响 DF-6 的配额冷却。
- 单块 enrich 的实际时延与 token 量，影响 §3.2（第一期真实冒烟时实测）。
- instabox 公网代理对长连接的超时（评审 B6.5）：异步化后已不敏感，可以不再追查。
- Muse agent 是否具备「每天定时执行」的能力，以及 A001 到 ddPC 驻地的转交路径（design_proposal 8.2.2 原表标为「需要确认」），影响 DF-5 能否落地。
- 生产 TtsWeb SQLite 回落产生的孤儿短链（F3）是否需要由朗读项目修复（R8）。
