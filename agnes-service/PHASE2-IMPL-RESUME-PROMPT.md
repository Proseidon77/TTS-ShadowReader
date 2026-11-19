# TASK6 第二期：实施续跑（77B，Opus）

你是 77B 上 agnes-service 项目的实现者。本任务是 2026-11-19 18:44 首跑的**续跑**：首跑 19:14 撞 Claude 5 小时限额中断，当时 agnes-service 侧 §2 代码已写大半、TtsWeb 侧 §5 未动、报告未产出。中断后定稿又有修订（网页保存认证设计定案）。全程中文写报告。

## 输入文件（先读）

- `/data/workspace/agnes-service/TASK6-PHASE2-PLAN.md` — **定稿（2026-11-19 晚回写版）**，本任务的唯一实施依据。注意 §1.5／§2.1／§2.6／§4／§5／§6 已按认证设计回写，与首跑时读到的版本不同，以当前文件为准。
- `/data/workspace/agnes-service/PHASE2-AUTH-DESIGN.md` — 网页保存认证设计（已批准），其 **§7 实施要点清单**是网页通道部分的实施清单。
- `/data/workspace/agnes-service/PHASE2-REVIEW.md` — 第二期审核报告（P2R 编号出处；冲突以定稿为准）。
- 首跑任务书 `PHASE2-IMPL-PROMPT.md`（本机与项目内应有副本）— 仅作对照：其中 **B.1（TtsWeb 网页通道）已作废**、B.5 的 can_act_for 已扩为含 `web`，其余范围仍然有效。

## 第 0 步：盘点中断点（先盘点，后动手）

1. 盘点首跑已落盘的成果：agnes-service 侧新增/修改文件（如 IngestService、FixTriage、Circuit、TtsWebClient 等）、p2 测试、构建状态。逐项对照**现行定稿**核对：已写且正确的**保留、不重写**；与新定稿冲突的（尤其是网页通道旧口径：`X-Ingest-Key` 旁路、「web 通道只收 share_id」）按新定稿修正或删除；写错或未写完的补齐。
2. 确认 TtsWeb 侧（`/data/workspace/tts-web/`、`/data/workspace/tts-redesign/`）首跑未动，如现状有出入在报告中注明。
3. 复核定稿可实施性：若发现 blocker 级问题（定稿与代码/数据现状冲突、照稿实施必出错），停下来只写报告，不实施、不自行改定稿口径。非 blocker 小出入按定稿精神处理并在报告注明。
4. 盘点结论写进报告第 1 节（保留了什么、改了什么、删了什么）。

## 实施范围（盘点与复核通过后）

### A. agnes-service（定稿 §2 全部，含续跑修正）

在开发实例（8795 + Mock）上实现并验证：

1. 编排端点 `POST /api/reading/ingest` + 作业查询/列表；作业落库 `agnes_auth.ingest_jobs`（幂等迁移、启动恢复、BackgroundService 扫描 `next_retry_at`、share_id 部分唯一索引）；准入语义（Admit 后释放并发 Ticket、`INGEST_WORKERS=2` 独立池、call_log accepted 行 + 结束 UPDATE、系统重试不计额度）。
2. 端点鉴权：`reading_ingest` 三值列 + agent 映射（`muse_id` 或 `INGEST_AGENT_MAP`，二选一并在报告说明）；准入前判定 401/403 不扣额度；AdminApi 与建号 CLI 同步支持；建号拦截 `^A\d{3}$` 用户名。账号开通**只写迁移/代码支持与上线清单命令，不直接改生产账号数据**。
3. kind=`ingest` 全链（两处 CHECK 迁移、LimitService.Kinds/View、AdminApi 白名单、管理页下拉）。
4. L1 降级：enrich 带 `context.full_text`（prompt_version `enrich-v2`）；块参数挂 ProviderDef 并按定稿参数表设值（gemini 块 20 句/1500 字、agnes 块 8 句/600 字；超时 lite 45s / flash 90s / agnes 120s；`INGEST_DEADLINE_S=420` 可配置；analyze 直调 180/600 不变、`DeadlineSeconds` 改可配置）；换 provider 时重新分块；熔断按定稿（429 读 retryDelay/Retry-After、无则 2 分钟；403 quota 60 分钟；E7 连续 3 篇校验失败熔 API 路；质量熔断只统计不自动熔断）；上游令牌桶放 Upstream 层（agnes 总桶 18/min、编排子配额 12/min，覆盖全部 Agnes 调用）；延迟重试整篇重做。
5. fixes 本地分流（补齐类采纳、改写类默认不采纳、数字+量词音变放行；`fx=a/r/s` 记入作业与 call_log detail）。
6. replace 前回读 title/lang/rate/current_version，空标题用正文前 20 字兜底。
7. L2 待办清单接口 + 自动对账（拉取时 + 每小时扫描；回填接口保留可选）。
8. 管理页：作业列表、L2 待办、kind 筛选补 ingest。
9. **网页通道（新增，按认证设计 §7 第 1–9 条）**：
   - `IngestApi.Caller` 删除 `X-Ingest-Key` 分支（及 WebUser/ServiceUser 在此的用途，无其他引用时一并删）；JWT 用户按 `reading_ingest` 分流：`agent` 走现状，`web` 返回 `(u, "web", "web")`，其他 403。
   - web 分支入参收窄：必须有 `text` 与 `title`，拒绝 `share_id` 与 `rate`（400 `unknown_field`），标题为空 400 `title_required`。
   - 202 响应与作业视图的 `url`：配了 `TTSWEB_PUBLIC_URL` 时拼 8791 绝对地址，没配保持相对路径；`.env.example` 加该键并注明改后须重启。
   - `Program.cs` 加 `GET /add` 路由别名（复用 ServeIndex）；`index.html` 加「添加文章」页签（标题／语言默认 ja／原文＋字数上限 5000／保存按钮／短链与作业状态结果区／「最近保存」列表），pathname 为 `/add` 时登录后直开该页签；草稿存 localStorage、成功后清除、401 不清空；回显一律 `textContent`；补错误文案 `ingest_forbidden`、`title_required`。
   - `/health` 的 `ingest.web_trigger` 改为 `ingest.public_url_configured`（布尔）。
   - 测试：`tests/p2_accept.py` 的 C 段（X-Ingest-Key）改写为「web 账号用 JWT 提交 text」，并按设计 §7 第 6 条补用例（none 403、web 带 share_id 400、缺 title 400、agent 行为不变、TtsWeb 故障时 202+share_id=null 后 worker 补上、同文重复 200 duplicate）；`tests/ingest_*.env` 删 `INGEST_WEB_KEY`。
   - README、DESIGN 补网页通道新链路与 P2R-19 已知限制。
   - 可选小修（设计 §7 第 9 条）：`/api/auth/refresh` 遇 `already rotated`（10 秒宽限内）不清 Cookie，前端稍等再刷。做了在报告注明，没做也注明。

### B. TtsWeb 侧（定稿 §5 契约项，在工作副本上实现并用开发实例验证）

1. **网页通道一概不做**：不加播放页按钮、不做 /add 页、不做服务端保存入口与保存口令（已作废）。播放页文件保持不动。
2. 契约：`/share` 与 commit 接受 `added_via`；`/v3/t` 加 status；`/v3/articles` 纳入 partial。
3. readings_check 接口（`POST /api/v1/ingest/readings_check`，nbest 收集读音候选）。
4. DDL 迁移**只写迁移文件并在开发 schema/副本上验证**（added_via 列、tr_via CHECK 放宽、ingest.py 的 TR_VIA 同步改代码）；生产 reading 库的迁移 SQL 写进报告上线清单，不直接对生产库执行。
5. `tokens.json` 的 agnes-service 令牌（`can_act_for={A001,A002,A003,web}`）：生成方式与要写入的内容写进上线清单，**不要直接改生产 tokens.json**，令牌值不写进报告。
6. /ingest.md 补 replace 口径、L2 章节、VERSION_CONFLICT 语义。
7. grace_until 提前：只写进上线清单，不动生产 config。

### C. Mock

在 MockAgnes 里加 `/share`、`/api/v1/ingest/preview|commit`、`/api/v1/articles/{id}` 假实现（支持 dup / 409 / 503 / 401 模式），用它跑破坏性与 T 态用例（首跑若已部分实现，盘点后补齐）。

## 红线

- **不 publish、不重启任何生产服务（8793 / 8791）、不改生产配置与生产数据**（含 .env、tokens.json、config.json、生产库数据与 DDL）。发布与上线动作由 operator 按报告的上线清单执行。
- 测试走开发实例（8795）+ Mock；对生产库只许只读查询核对。
- 不改定稿口径；发现冲突按第 0 步处理。
- 构建 0 错误；第一期自检（--self-test）与既有 Mock 验收必须全过，text / image / video 不回归。

## 交付

把报告写到 `/data/workspace/agnes-service/PHASE2-IMPL-REPORT.md`，结构：

1. **盘点与复核结论**（中断点盘点：保留/修正/删除清单；复核通过 / 有 blocker 逐条列明）
2. **实现清单**（按定稿 §2 / §5 与设计 §7 逐项：做了什么、动了哪些文件）
3. **测试结果**（自检、Mock 验收、新增用例通过数；失败项如实写）
4. **上线清单**（operator 要执行的操作，按顺序：publish 步骤、要执行的 DDL/迁移、账号开通命令（含给用户本人账号开通 `reading_ingest=web`）、tokens.json 写入、config 改动（含 `TTSWEB_PUBLIC_URL`）、重启对象、生产冒烟建议（照设计 §7 第 13 条验收项））
5. **遗留与偏差**（没做完的、与定稿不一致的、建议后续处理的）

你运行结束时的最终回复用三五句中文概括：盘点与复核结论、实现完成度、测试结果、上线清单要点。
