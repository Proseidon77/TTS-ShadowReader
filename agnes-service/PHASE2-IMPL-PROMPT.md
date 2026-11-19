# TASK6 第二期：复核定稿并实施（77B，Opus）

你是 77B 上 agnes-service 项目的实现者。任务分两步：先复核定稿，无 blocker 才实施。全程中文写报告。

## 输入文件（先读）

- `/data/workspace/agnes-service/TASK6-PHASE2-PLAN.md` — **定稿**（2026-11-19 用户批准），本任务的唯一实施依据
- `/data/workspace/agnes-service/PHASE2-REVIEW.md` — 第二期审核报告两轮（P2R 编号的出处；定稿已并入，冲突以定稿为准）
- agnes-service 现行代码（本项目目录）、TtsWeb 现行代码（`/data/workspace/tts-web/`）、朗读设计与 DDL（`/data/workspace/tts-redesign/`）

## 第 0 步：复核（定稿 §6 第 0 步）

对照现行代码复核定稿的可实施性。**若发现 blocker 级问题**（定稿与代码/数据现状冲突、照稿实施必出错）：停下来，只写报告（见下），不要实施、不要自行改定稿口径。非 blocker 的小出入按定稿精神处理并在报告中注明。

## 实施范围（复核通过后）

### A. agnes-service（定稿 §2 全部）

在开发实例（8795 + Mock）上实现并验证：

1. 编排端点 `POST /api/reading/ingest` + 作业查询/列表；作业落库 `agnes_auth.ingest_jobs`（幂等迁移、启动恢复、BackgroundService 扫描 `next_retry_at`、share_id 部分唯一索引）；准入语义（Admit 后释放并发 Ticket、`INGEST_WORKERS=2` 独立池、call_log accepted 行 + 结束 UPDATE、系统重试不计额度）。
2. 端点鉴权：`reading_ingest` 三值列 + agent 映射（`muse_id` 或 `INGEST_AGENT_MAP`，二选一并在报告中说明选了哪个）；准入前判定 401/403 不扣额度；AdminApi 与建号 CLI 同步支持；建号拦截 `^A\d{3}$` 用户名。MuseAgent1/2 的 `reading_ingest` 开通**只写迁移/代码支持，不直接改生产账号数据** — 需要执行的开通命令写进报告的上线清单。
3. kind=`ingest` 全链（两处 CHECK 迁移、LimitService.Kinds/View、AdminApi 白名单、管理页下拉）。
4. L1 降级：enrich 带 `context.full_text`（prompt_version `enrich-v2`）；块参数挂 ProviderDef 并按定稿参数表设值（gemini 块 20 句/1500 字、agnes 块 8 句/600 字；超时 lite 45s / flash 90s / agnes 120s；`INGEST_DEADLINE_S=420` 可配置；analyze 直调 180/600 不变、`DeadlineSeconds` 改可配置）；换 provider 时重新分块；熔断按定稿（429 读 retryDelay/Retry-After、无则 2 分钟；403 quota 60 分钟；E7 连续 3 篇校验失败熔 API 路；质量熔断只统计不自动熔断）；上游令牌桶放 Upstream 层（agnes 总桶 18/min、编排子配额 12/min，覆盖全部 Agnes 调用）；延迟重试整篇重做。
5. fixes 本地分流（补齐类采纳、改写类默认不采纳、数字+量词音变放行；`fx=a/r/s` 记入作业与 call_log detail）。
6. replace 前回读 title/lang/rate/current_version，空标题用正文前 20 字兜底。
7. L2 待办清单接口 + 自动对账（拉取时 + 每小时扫描；回填接口保留可选）。
8. 管理页：作业列表、L2 待办、kind 筛选补 ingest。

### B. TtsWeb 侧（定稿 §5，在 /data/workspace/tts-web 与 tts-redesign 工作副本上实现并用开发实例验证）

1. 网页通道：v3 播放页加「添加文章」按钮；新增 `/add` 页（标题必填、原文、写令牌、保存走 /share、短链与状态回显）；保存成功后服务端经 localhost 触发 8793 编排（开发阶段对开发实例/Mock 触发即可，地址做成配置）。
2. 契约：`/share` 与 commit 接受 `added_via`；`/v3/t` 加 status；`/v3/articles` 纳入 partial。
3. readings_check 接口（`POST /api/v1/ingest/readings_check`，nbest 收集读音候选）。
4. DDL 迁移**只写迁移文件并在开发 schema/副本上验证**（added_via 列、tr_via CHECK 放宽、ingest.py 的 TR_VIA 同步改代码）；生产 reading 库的迁移 SQL 写进报告上线清单，不直接对生产库执行。
5. `tokens.json` 里 agnes-service 令牌（can_act_for={A001,A002,A003}）：生成方式与要写入的内容写进上线清单，**不要直接改生产 tokens.json**。
6. /ingest.md 补 replace 口径、L2 章节、VERSION_CONFLICT 语义。
7. grace_until 提前：只写进上线清单，不动生产 config。

### C. Mock

在 MockAgnes 里加 `/share`、`/api/v1/ingest/preview|commit`、`/api/v1/articles/{id}` 假实现（支持 dup / 409 / 503 / 401 模式），用它跑破坏性与 T 态用例。

## 红线

- **不 publish、不重启任何生产服务（8793 / 8791）、不改生产配置与生产数据**（含 .env、tokens.json、config.json、生产库数据与 DDL）。发布与上线动作由 operator 按你报告里的上线清单执行。
- 测试走开发实例（8795）+ Mock；对生产库只许只读查询核对。
- 不改定稿口径；发现定稿与现实冲突按第 0 步处理。
- 构建 0 错误；第一期自检（--self-test）与既有 Mock 验收必须全过，text / image / video 不回归。

## 交付

把报告写到 `/data/workspace/agnes-service/PHASE2-IMPL-REPORT.md`，结构：

1. **复核结论**（通过 / 有 blocker——逐条列明）
2. **实现清单**（按 §2 / §5 逐项：做了什么、动了哪些文件）
3. **测试结果**（自检、Mock 验收、新增用例的通过数；失败项如实写）
4. **上线清单**（operator 要执行的操作，按顺序：publish 步骤、要执行的 DDL/迁移、账号开通命令、tokens.json 写入、config 改动、重启对象、生产冒烟建议）
5. **遗留与偏差**（没做完的、与定稿不一致的、建议后续处理的）

你运行结束时的最终回复用三五句中文概括：复核结论、实现完成度、测试结果、上线清单要点。
