# 审核任务：评审 TASK6（深接法）方案，不要实现

你是在 77B 上新开的 session，身份是**评审者**，不是实现者。本任务只产出一份评审报告文件，不许动任何代码、配置、数据库和服务。

## 必读

1. 本目录（/data/workspace/agnes-service）的 `TASK6.md` —— 被评审的方案全文。
2. agnes-service 现有代码：`Program.cs`、`src/*.cs`、`auth_schema.sql`、`index.html`、`tests/`、README/DESIGN（按需）。
3. 朗读项目在 77B 上的现状（自行定位）：P2a 正在改造的 TtsWeb 工作目录与它的 preview/commit 契约草案或实现（ingest.md / ingest_prompt.md / 任务书等，以现场文件为准）；`db77database` 里 `reading` schema 的现状（表、约束，特别是 sentences.tr_via 的 CHECK 定义）可用 psql **只读**查询核对。

## 硬约束

- 只读评审：不改代码、不改配置、不写数据库、不重启服务、不 publish。唯一允许写的文件是评审报告 `TASK6-REVIEW.md`（放本目录）。
- `.env` 只许看键名是否存在，不许打印、引用任何值；任何 Key/令牌同理。
- 不调用真实 Agnes API（不花配额）；不需要为评审跑构建，但可以读构建配置与跑只读命令（psql SELECT、ss/netstat 查端口、cat 代码）。

## 评审要求（逐项给出证据，引用文件路径/行号或查询结果）

1. **假设核对**：把 TASK6 里所有关于现有环境的主张逐条对实况核对，至少包括：Generate() 管线形状、LimitService 的 kind 集合与每日总量算法、usage_daily 与 call_log 的 kind CHECK 约束名与现值、Settings 的 .env 热重读机制、/health 现有字段形状、MockAgnes 的现有能力、8791/8793/8794 各端口现在实际在跑什么、reading.pos 枚举与 TASK6 §4 映射表的对齐情况、reading.articles 的字数上限。
2. **契约风险**：enrich 的出入参与 P2a 实际在建的 preview/commit 契约对不对得上（字段名：fixes 的形状、tr 的提交方式、versions.tool 的字段、tr_by/tr_via 的记法、added_by 与令牌身份模型、去重到底发生在 preview 还是 commit、TR_MISSING 等错误码的实际语义）。对不上的逐条列明，并给出 TASK6 应如何改口径。
3. **时序与依赖风险**：与 P2a 并行施工的冲突点（DDL 文件、端口、同一数据库）、第二期的开工前提是否真实成立、有没有被 TASK6 忽略的前置条件。
4. **简化与优化**：结合现有代码指出 TASK6 哪里过度设计（可复用现有实现而非新建的）、哪里设计不足（例如编排链路的超时预算、并发槽占用、长文 enrich 的 token 量、失败分类），给出具体改法。
5. **遗漏项**：TASK6 没写但实现时一定会撞到的事情。

## 报告格式（写入 TASK6-REVIEW.md）

- 总体结论：方案可行性判定（可行 / 修改后可行 / 不可行）+ 一句话理由
- 假设核对表：主张 → 实况 → 成立与否
- 必须修改（blocker）：逐条，含证据与改法
- 建议优化：逐条，含收益与代价
- 需用户拍板：逐条
- 修订后的实施顺序建议

最后在 stdout 用简短中文总结：结论 + blocker 条数 + 最重要的 3 条修改。
