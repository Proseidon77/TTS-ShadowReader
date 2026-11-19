# 实现任务：TASK6 第一期（analyze 底座）

你是在 77B 上新开的 session，身份是实现者。工作目录 /data/workspace/agnes-service。

## 依据文件（同目录，先全读再动手）

- `TASK6.md`：方案原文，第一期 = 其 §1 的 1–4 项（provider 注册表、/api/analyze、校验与修复重试、接入 JWT/限额/call_log、/health 与管理页筛选）。
- `TASK6-REVIEW.md`：评审报告。**与 TASK6 冲突处以评审为准**，本期必须落实：B3（pos 映射实值）、B5 中 enrich/gloss 的部分、B6（紧凑投影+分块+按块校验）、B7（Upstream.Call 抽取+脱敏扩展）、B8（.env 注释单独成行+就绪格式校验）、B9 中管线记账部分；优化 O1（同 provider 模型级兜底优先）、O2（Gemini 先只做 json_object+reasoning_effort，不做 strict json_schema）、O4（复用现有实现）、O5（失败分类）、O9（Mock 场景前缀）。
- `TASK6-FALLBACK-DESIGN.md`：降级设计。其中 L1（API 路内部降级：修复→换模型→换 provider→缩块）的顺序与失败分类口径适用于本期的 analyze 引擎；L2、粘贴入库、share-first 等属于第二期，本期不做。

## 本期范围（只做这些）

1. **DDL 迁移**：`auth_schema.sql` 增加幂等 DO 块，把 `usage_daily` 与 `call_log` 的 kind CHECK 扩为含 `analyze`（约束名保持 `usage_daily_kind_check`/`call_log_kind_check` 不变）；对现库执行并查 pg_constraint 留证。只扩取值，向后兼容。
2. **Upstream.Call 抽取（B7）**：把 `Agnes.Call` 改成通用 `Upstream.Call(name, baseUrl, key, …)`，Agnes 原有三条路径改为传参调用、行为不变；`Redact` 改为遍历敏感键集合（AGNES_API_KEY、JWT_SECRET、GEMINI_API_KEY、TTSWEB_TOKEN）。复用 `Agnes.Http`，不要另建 HttpClient。
3. **Provider 注册表**：静态字典+record 即可（O4，不要 DI/接口层）。agnes：模型顺序 [agnes-3.0-flash（默认）, agnes-2.5-flash]，策略 json_object；gemini：端点 GEMINI_API_URL（默认 Google 兼容端点）、模型 [gemini-flash-latest（默认）, gemini-flash-lite-latest]，策略 json_object + reasoning_effort（latest=low、lite=minimal，O2）。选择顺序：请求指定 > ANALYZE_DEFAULT_PROVIDER > ANALYZE_PROVIDER_ORDER；**兜底先同 provider 换模型、再换 provider**（O1）。失败分类按 O5：参数类 4xx 不兜底；403/429 配额类、5xx、超时、网络错误 → 兜底；JSON 解析/校验失败 → 同模型修复重试；finish_reason=length → 判定为块太大、对半拆块（B6）。单块上游调用上限与整单上限要在回报里写明你定的数值与理由。
4. **POST /api/analyze**（TASK6 §4 契约）：raw 双形状（reading 形状为主 + analyzer 兼容视图）与 ruby 推导按 §4/§6 保留实现；pos 映射按 B3 修正（代名詞→pronoun、形容動詞→adj、接頭/接尾→affix、数詞→num），且**实现前先到 /data/workspace/tts-redesign/impl 的 TtsWeb 代码里取 pos 枚举全集**核对映射表，差异写进回报。enrich 按 B6：紧凑投影 [{i, s, r, unk}] 发给模型；按约 20 句或约 1500 字分块，逐块校验/修复/兜底，失败只重做失败块；fixes 回传带 i 与原 s、服务端核对 s 一致（O8）；unk/无读音 token 排除在 fixes 之外（评审遗漏项 6）；gloss 默认关（提示词不要求、响应有则透传，B5）。
5. **管线接入（B9）**：`Generate` 加 preview 选择器（raw 取 text、enrich 取第一句，各截 120 字）与 outcome 记录；退额规则：全部上游 4xx 才退额，兜底后成功不退。kind=`analyze` 一次请求只准入 1 次，计入每日总量/每分钟/并发，不占视频独立配额。`LimitService` 的 Kinds、View、counted_endpoints 补 analyze 与新端点；`AdminApi` 的 kind 白名单补 analyze；call_log 的 detail 改紧凑编码并把截断上限提到约 1000 字。
6. **/health**：加 `analyze.providers` 段（每家 ready + 模型名；ready 只看 Key 配置 + URL 可解析（B8），不发探测、不暴露任何 URL）。
7. **前端**：`index.html` 管理页调用记录筛选加 analyze、kindZh 加 `analyze:"分析"`；JS 过 `node --check`。不加解析页签。
8. **配置**：.env 与 .env.example 加 GEMINI_API_KEY（**留空**，用户日后自填）、GEMINI_API_URL、ANALYZE_DEFAULT_PROVIDER=agnes、ANALYZE_PROVIDER_ORDER=agnes,gemini、ANALYZE_TIMEOUT_S=180；注释一律单独成行（B8）。复核 Settings 热重读覆盖新键。
9. **MockAgnes 扩展（O9）**：场景前缀（scenario:json_ok / json_bad_concat / json_invalid / 403 / sleep:N），并加一条 /gemini 路由校验另一个 dummy key，使兜底验收能在 call_log 里区分 provider。

## 不做（第二期或禁区）

- /api/reading/ingest 编排、粘贴入库、reading 库的任何 DDL（tr_via/added_via）、TtsWeb 的任何改动（8791 是生产朗读服务，只许只读参考其代码与契约）、streaming、Gemini strict json_schema。
- 现有 text/image/video 三条管线的行为不许有任何变化。

## 流程与验收

1. 改任何现有文件先做带日期后缀的备份。
2. 开发实例用 **8795**（新建 tests/port8795.env 等，不要用 8794）。先在 8795 上跑：现有 text/image/video 回归（沿用既有 smoke/测试办法）+ TASK6 §11 第一期验收 1–6（raw 正常、修复重试、agnes 403→gemini(mock）兜底且 call_log 有轨迹、enrich 正常+越界拦截、401、配额 429 且有 kind=analyze 记录、/health analyze 段一致）+ ruby 推导单测。`dotnet build -c Release` 0 警告 0 错误。
3. 真实 Agnes 冒烟（各 1 次，计 analyze 用量）：raw 用 `彼女は静かな図書館で本を読んでいる。` 与 `一本のペンと十八年の手帳。`；enrich 用一个小骨架（从 reading 现有数据或 impl 示例取 2–3 句的真实 toks 形状，回报里注明来源）。
4. **发布闸门**：8795 上全部回归与验收通过，才 publish + 重启生产 + 生产冒烟（/health 含 analyze 段、未登录 401、真实 raw 1 次、管理页筛选可见）；任一项失败就停在开发实例、不发布，回报中写清失败点。测试账号与测试数据最后清理干净。
5. 回报：写 `PHASE1-REPORT.md`（文件清单+备份名、构建结果、每项验收的命令与关键输出、真实冒烟完整输出、与任务书的偏差及理由、为未拍板项采用的推荐口径清单），stdout 用简短中文总结同样的要点。

## 硬规矩

- 任何 Key/令牌不打印、不写日志、不进代码；.env 只加空占位。
- 数据库只许执行第 1 项的 kind 迁移，不许动其他表与数据（测试数据除外，测完清理）。
- 不重启除 agnes-service 以外的任何服务；发布重启前确认没有在途的长请求（评审遗漏项 4：TimeoutStopSec=20）。
