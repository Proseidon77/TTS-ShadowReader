# 审核任务：TASK6 第二期方案（只审核，不实施）

你已经在 agnes-service 项目目录里。审核对象：`TASK6-PHASE2-PLAN.md`（本目录）。这是第二期（编排端点 + 网页粘贴入库 + L1/L2 降级执行）的待审核稿，已经过用户多轮改定。你的任务是**审核并提出修改意见**，写成报告 `PHASE2-REVIEW.md` 放本目录。**不许改任何代码、不许 publish、不许重启服务、不许写生产库**（只读核对可以）。

## 审核范围与依据

1. 对照本项目真实代码（src/、publish 结构、.env.example、tests/）核对方案里 agnes-service 侧每一项的可行性：异步作业模型、L1 分块/熔断/上游令牌桶、call_log 与限额记账、CORS、users 表 reading_ingest 标记等。指出与现有代码冲突或重复造轮子之处。
2. **重点审核朗读前端新增部分**（方案 §1.5、§5）：播放页（TtsWeb，代码在 `/data/workspace/tts-web/`，设计与 DDL 在 `/data/workspace/tts-redesign/`，均可读）要加「添加文章」按钮与 `/add` 粘贴页，且保存走 agnes-service 的 JWT 登录认证（固定账号 ReaderNew，只需密码）。请具体核对：
   - 跨源（8791 页 → 8793 接口）调用登录/刷新/编排/作业查询的完整链路：CORS 配置、refresh Cookie 在跨源 fetch 下的实际行为、access token 在 /add 页的保存方式与 XSS 面、token 过期时的用户体验；
   - ReaderNew 固定账号 + reading_ingest 标记的落地方式（DDL、AdminApi 是否要扩、CLI 是否要支持）；
   - /add 页与播放页 v3 的现有结构（player_v2/v3 的代码组织）是否容得下按钮与新页面，有没有更贴合现有结构的放法；
   - TtsWeb 侧与 agnes-service 侧的职责边界有没有漏项或重叠（例如作业状态到底由谁回显、文章状态从哪读）。
3. 审核 §2.3 fixes 核对层：agnes-service 是 .NET、词典在 TtsWeb 的 Python/fugashi 一侧 — 核对层放哪、怎么拿到 UniDic 读音依据，给出可落地的实现形态与代价，不要只说「可行」。
4. 审核 §5 朗读侧协调项（tr_via 扩值、added_via、服务令牌 can_act_for）与 reading DDL 现状的一致性（读 tts-redesign 的 DDL 与现状说明核对）。
5. 方案 §4 的待拍板清单：逐条给出你的意见（同意推荐 / 给替代案并说明理由），特别是 DF-7 的分块与超时参数（结合你对代码里现有超时的了解）、DF-6 熔断、D5 记账 kind 的选择（ingest 独立 kind 还是沿用 analyze）。

## 追加比较题：保存认证的归属（用户 2026-11-19 提出备选，必须比较）

方案现稿是 A 案：/add 页跨源调 agnes-service 的 JWT 登录（固定账号 ReaderNew）。用户提出备选 B 案：**若跨源登录太麻烦，直接在 dbapi 上开账号** — dbapi 是同机 8792 的数据库管理 API，同为 JWT 体系（账号 Proseidon、MuseAgent 等，认证与用户管理现成），账号开在它那边、用它的认证。再把 C 案一并纳入比较：/add 页只跟 TtsWeb 同源打交道，由 TtsWeb 服务端代理登录与保存（浏览器不见跨源，服务端之间走本机调用）。请就三案对比：实现复杂度、可靠性（Cookie/CORS/token 存放各自的坑）、安全面、与记账口径（added_by=ReaderNew 或对应账号）的衔接、后续维护成本，给出**明确推荐**与理由，写进报告的「朗读前端与认证」一节。若推荐 B 或 C，说明方案稿 §1.5/§2 需要连带改哪些条目。

## 报告格式（PHASE2-REVIEW.md）

- 结论先行：可行 / 修改后可行 / 不可行，一句话总评。
- 修改意见逐条编号（P2R-1、P2R-2…），每条含：问题、证据（文件/代码位置）、建议改法、严重程度（blocker / should / nice）。
- 单列一节「朗读前端与认证」汇总第 2 点的结论。
- 单列一节「待拍板清单逐条意见」。
- 最后列「实施顺序建议」：哪些先做、哪些依赖朗读侧先行、预计的主要风险点。
- 全文用中文。不要改方案稿本身，意见只写报告里（后续由用户拍板后另行并入）。
