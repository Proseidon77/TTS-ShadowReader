# TASK6（修订版）：agnes-service 日语解析 + 朗读一键入库（深接法）

状态：方案修订定稿（2026-11-18 用户拍板改深接法），待用户点模型后开工。本文件是实现任务书，口径与验收标准以此为准。

## 0. 背景与目标

- japanese-analyzer 的解析做法已核查：运行时调 LLM（OpenAI 兼容 chat completions），提示词让模型返回结构化 JSON（word / pos / furigana）。借鉴其提示词规则与 provider 配置模式，不用其运行时。
- 朗读升级方案（design_proposal.md 定版）的入库流水线是：`/share` 存原文 → TtsWeb `preview`（分句 + fugashi 骨架）→ **执行者**翻译/校对读音/gloss → TtsWeb `commit`（服务端校验 + 版本切换）。执行者当前是 Claude 驻地（ddPC/77B），受会话可用性与用量限额制约。
- 本任务按**深接法**落地：agnes-service 新增一个**编排端点** `POST /api/reading/ingest`，调用方只调一次，agnes-service 内部依次完成 preview → LLM 翻译/校对 → commit，返回短链。Agnes / Gemini API 自此成为朗读入库的正式执行者（与 Claude 驻地并列，互为替补）。
- 为支撑编排，先建两块底座：**provider 注册表**（Agnes 已通、Gemini 留位）と **analyze 引擎**（含 enrich 模式 = 定版 §2.2 第 ④ 步的执行者契约），并保留独立端点 `POST /api/analyze` 供调试与其它调用方使用。

## 1. 范围与分期

**第一期（底座，可立即开工，不依赖朗读 P2）**
1. provider 注册表（agnes、gemini）与统一调用函数（超时、兜底、策略字段）。
2. `POST /api/analyze` 两模式：`raw`（裸句解析）、`enrich`（骨架翻译/校对）。
3. 服务端校验与修复重试；接入 JWT / 限额 / call_log（新 kind `analyze`）。
4. `/health` 增加 analyze 后端就绪状态；管理页调用记录 kind 筛选加「分析」。

**第二期（编排，依赖朗读 P2 的 preview/commit 可用后开工）**
5. `POST /api/reading/ingest` 编排端点（preview → enrich → commit → 返回短链，含去重与降级）。
6. 协调项落地：reading 的 `tr_via` DDL 变更（三处同落）、TtsWeb 侧给 agnes-service 登记可写令牌、/ingest.md 执行者名单的补充文字（交付文本，由朗读项目合并）。

不做：
- 不改 TtsWeb 的**代码**（第二期只动它的配置：令牌登记；DDL 按 §9 的时序执行）。
- 不做纯 LLM 替代 fugashi 骨架的正路（与朗读定版冲突）；raw 模式只是工具端点。
- 不动 japanese-analyzer 仓库；不给前端加解析/入库页签（管理页筛选除外）。
- 不实现 streaming。

## 2. 环境与现状（实现者先复核再动手）

- 机器：77B（instabox `instabox-77-instabox`），项目 `/data/workspace/agnes-service/`，.NET SDK 在 `/data/dotnet-sdk/dotnet`，ASP.NET Core 10，minimal API。
- 生产：systemd `agnes-service`，端口 8793，公网 https://8793-agq6dov7ieyr5kl67vfd2cqvce.instabox.tech 。
- 测试纪律（历轮沿用）：先在开发实例全量验证，最后才 publish + 重启生产。**注意端口**：朗读项目 P2a 的开发版 TtsWeb 正在用 8794，本任务的开发实例先查占用、另选空闲端口（如 8795），不要抢占。
- 现有结构：`Program.cs`（路由 + `Generate()` 管线）、`src/Agnes.cs`、`src/LimitService.cs`、`src/CallLog.cs`、`src/Db.cs`、`src/Settings.cs`、`src/AdminApi.cs`、`auth_schema.sql`、`index.html`；测试有 `tests/MockAgnes`（127.0.0.1:18793）。
- 数据库：`db77database`，schema `agnes_auth`。`usage_daily` 与 `call_log` 两处 kind 的 CHECK 目前都是 ('text','image','video')（约束名实现时以 pg_constraint 实查为准）。
- TtsWeb（朗读服务）与 agnes-service **同在 77B**：生产 TtsWeb 在 127.0.0.1:8791（77B 侧实例即朗读库真源所在，ddPC 侧是另一实例、走缓存读取，不要调它入库）。P2 改造后的 TtsWeb 提供 preview / commit / share / 只读 GET，契约以其 `/ingest.md` 与 `/ingest_prompt.md` 的**落地版本**为准（第一期完工时若 P2 尚未交付，第二期开工前必须先读落地契约再写编排，契约与本文件的假设不一致处在回报中列明并以落地契约为准）。
- 上游实测结论（2026-11-18 真实调用）：Agnes 认 `response_format:{type:"json_object"}`、容忍多余字段、模型名必须真实存在；`agnes-2.5-flash`、`agnes-3.0-flash` 均能按解析提示词返回合格 JSON（拼接逐字一致）；`agnes-2.5-pro` 返回 403 insufficient_user_quota（**不要把 pro 设为默认或兜底**）。
- Gemini 现状：没有可直调的 Gemini API Key（agy 的 OAuth 会话不是 API Key）。按「Key 为空 = 未就绪、自动跳过」实现，用户日后填入即生效。
- 硬规矩：任何 Key/令牌只从 .env 或服务配置读，禁止打印、写日志、写进代码；用户侧 Key 的值由用户本人填，实现者只加空占位；服务间令牌（§8）由实现者生成并双边登记、只回报「已配置」；改现有文件先做带日期后缀备份；生产切换后跑冒烟并给证据。

## 3. Provider 注册表

| 字段 | agnes | gemini |
|---|---|---|
| name | `agnes` | `gemini` |
| 端点 | `Settings.AgnesBase + /v1/chat/completions`（复用 AGNES_BASE_URL） | `GEMINI_API_URL`，默认 `https://generativelanguage.googleapis.com/v1beta/openai/chat/completions` |
| Key | `AGNES_API_KEY`（已有） | `GEMINI_API_KEY`（新增，可空） |
| 就绪判定 | Key 非空 | Key 非空 |
| 模型白名单 | `agnes-3.0-flash`（默认）、`agnes-2.5-flash` | `gemini-flash-latest`（默认）、`gemini-flash-lite-latest` |
| response_format 策略 | `{type:"json_object"}` | `{type:"json_schema", json_schema:{name, strict:true, schema}}`（schema 按模式给） |
| 专属字段 | 无 | `reasoning_effort`：flash-latest 用 `low`、flash-lite 用 `minimal`（analyzer 生产做法） |

调用函数：拼 messages → 按策略加字段 → POST（Bearer）→ 取 `choices[0].message.content`（Agnes 附带的 reasoning_content 忽略）→ 返回文本与实际 provider/model。超时 `ANALYZE_TIMEOUT_S`（默认 180）。

选择与兜底：请求 `provider` > `ANALYZE_DEFAULT_PROVIDER`（默认 agnes）> `ANALYZE_PROVIDER_ORDER`（默认 `agnes,gemini`）。触发兜底：网络错误、超时、HTTP 5xx、403（配额类）、429。不兜底：请求参数 400 类、校验失败（走 §6 修复重试）。未就绪跳过；全不可用 → 503 `no_provider_available`。单次业务请求最多 4 次上游调用（含修复与兜底）。

## 4. 底座端点：POST /api/analyze

鉴权 Bearer JWT；经生成管线，kind=`analyze`。

请求：

```jsonc
{
  "mode": "raw" | "enrich",          // 必填
  "provider": "agnes" | "gemini",    // 可选
  "model": "agnes-3.0-flash",        // 可选，须在所选后端白名单内，否则 400 invalid_model
  "text": "彼女は静かな図書館で本を読んでいる。",   // raw 必填，1–2000 字，一个分析单元（长文分块由调用方负责）
  "sentences": [ { "seq": 0, "text": "…", "toks": [ /* preview 骨架原样 */ ] } ]  // enrich 必填，1–200 句
}
```

响应（200）：

```jsonc
{
  "mode": "raw", "provider": "agnes", "model": "agnes-3.0-flash",
  "prompt_version": "analyze-v1", "attempts": 1,
  "toks":   [ { "s": "彼女", "pos": "pron", "r": "かのじょ", "ruby": [{"t":"彼女","r":"かのじょ"}] } ],  // raw：reading 形状（主）
  "tokens": [ { "word": "彼女", "pos": "代名詞", "furigana": "かのじょ" } ],                            // raw：analyzer 形状（兼容视图）
  "sentences": [ { "seq": 0, "tr": "她在安静的图书馆里看书。",                                          // enrich
                   "fixes": [ { "tok_i": 3, "reading": "しずかな" } ],
                   "gloss": { "5": "图书馆" } } ]
}
```

形状约定：
- 模型只出 analyzer 形状（word / pos=学校文法日文标签 / furigana）。服务端映射 reading 形状：`s=word`、`r=furigana`、`pos` 按映射表（名詞→noun、代名詞→pron、動詞→verb、形容詞→adj、形容動詞→adjn、副詞→adv、連体詞→adn、接続詞→conj、感動詞→intj、助詞→particle、助動詞→aux、記号→sym、改行→sym；实现时与 reading 现行枚举对齐并回报差异）；`l / pd / conj` 模型给不出，**省略不编**。`ruby` 由服务端按 §6 算法推导。
- enrich 的 `fixes[].tok_i` 是该句 toks 下标；`gloss` 键为 tok_i 字符串，可缺省。enrich **不返回修改后的 toks**（commit 侧按 fixes 套用，定版契约）。

## 5. 提示词（版本化常量，随响应返回版本号）

- `analyze-v1`（raw）：规则**参照** analyzer 的 buildAnalyzePrompt 自行撰写（AGPL-3.0，不逐字复制文本；规则是事实约束，可完整覆盖）：学校文法切分、助动词拆分、て形+补助动词拆分、形容動詞整体、助词分离、两种「ない」、同形多读结合语境、furigana 一律平假名、pos 封闭集合、标点与换行 token 化、拼接逐字等于原文、只返回 JSON。gemini 时同规则另以 json_schema 表达结构（strict）。
- `enrich-v1`（enrich）：输入整篇句子+骨架 JSON。约束：不改原文、不改分词边界与 token 顺序；只产出每句 `tr`（自然意译，定版第 5 条）、`fixes`（只列要改的读音，下标+平假名）、可选 `gloss`（生词难点简短中文释义，不求全覆盖）；数字+量词音变、同形多读为校对重点；只返回 JSON。gemini 的 json_schema 同步给 enrich 结构。

## 6. 服务端校验与修复重试

raw：① content 合法 JSON 且含 tokens 数组；② word 顺序拼接逐字等于输入 text；③ furigana 为平假名或空；④ pos 在封闭集合内。
enrich：① seq 集合与输入完全一致；② 每句 tr 非空；③ fixes 的 tok_i 不越界、reading 纯平假名；④ gloss 键不越界。

任一不过：同一后端带修复提示重试 1 次（把失败项具体列给模型，如第一个差异的位置）。再不过：换兜底后端从头来（计入 4 次上限）。仍不过：502 `analyze_validation_failed`，details 带最后一次失败清单；call_log 的 detail 同步记（outcome 用 `analyze_validation_failed`）。

ruby 推导（确定性独立函数 + 单测）：s 纯假名/符号或 r 空 → ruby=null；求 s 与 r 的最长公共后缀（限 s 尾部假名段）为送假名部分，ruby=[{t: 词干, r: 读音去后缀}] +（后缀非空时）{t: 后缀}（如 買っ→[{t:"買",r:"か"},{t:"っ"}]，与方案 2.4 一致）；无公共后缀且 s 含汉字 → [{t:s, r:r}]；边界情况按「宁可整词注音」。

## 7. 限额、记录与健康

- kind 扩展 `analyze`：LimitService 的 kind 集合、两处 DB CHECK（usage_daily、call_log）同步扩展，迁移写进 `auth_schema.sql`（幂等 DO 块替换约束）并对现库执行。
- 计数口径：analyze 计入每日总量、每分钟、并发（与 text 同等待遇，无独立日配额；video 独立配额不受影响）。复核 LimitService 总量算法是否自动包含新 kind，硬编码求和则改对并说明。**编排端点的一次入库只算 1 次 analyze 准入**（内部的 preview/commit 是服务间调用，不重复计 agnes-service 的限额）。
- call_log：kind=`analyze`；model 记 `provider:model`；prompt_preview：raw 取 text 前 120 字，enrich/编排取第一句（或全文）前 120 字；detail 记兜底轨迹、最终校验结果，编排时加 `ingest:<share_id 或 raw>` 标记。
- `/health` 增加 `"analyze": {"providers": {"agnes": {"ready": true, "models": [...]}, "gemini": {"ready": false, "models": [...]}}}`（ready 只看 Key 配置，不发探测请求）。
- 管理页：调用记录 kind 筛选加 `analyze`、kindZh 加 `analyze:"分析"`。

## 8. 编排端点：POST /api/reading/ingest（第二期核心）

鉴权 Bearer JWT；kind=`analyze` 准入一次。

请求：

```jsonc
{
  "text": "（全文，1–5000 字，与 reading.articles 的上限对齐）",   // 必填
  "title": "可选",
  "added_by": "可选，代填发起人（见权限）",
  "lang": "ja", "rate": "-20%",          // 可选，透传给 TtsWeb
  "provider": "agnes" | "gemini", "model": "可选",   // 选 enrich 后端
  "replace": false, "base_version": 3     // 可选，透传 commit 的版本语义（以落地契约为准）
}
```

内部流程：
1. **preview**：以 agnes-service 的服务令牌调 TtsWeb `preview`（本机回环 `TTSWEB_BASE_URL`，默认 `http://127.0.0.1:8791`），得规范化原文、分句与骨架。若 preview/契约表明该文已存在且未带 `replace` → **直接返回已有短链**（`deduplicated:true`），不浪费 LLM 调用。
2. **enrich**：走 §3–§6 的引擎（provider 选择、校验、修复、兜底同 analyze 的 enrich 模式）。实际出力的后端决定出处：provider=agnes → `tr_via='agnes-api'`，gemini → `'gemini-api'`；`tr_by='agnes-service'`；versions.tool 记 `{executor:'agnes-service', model, prompt_version:'enrich-v1'}`（字段名以落地契约为准）。
3. **commit**：按落地契约提交（译文 + fixes + gloss + 出处）。commit 的业务错误（如 TR_MISSING、版本冲突）→ 把缺项反馈给 enrich **修复重试 1 次**；仍失败 → 走降级（第 4 步）并把 commit 的错误码原样带回响应 details。
4. **降级**：enrich 整体失败或 commit 最终失败 → 调 TtsWeb `/share` 只存原文，响应 `status:"raw"` + `warnings` 写明原因（与 ingest 手册第 6 步的人工降级同口径：原文永不丢，朗读不受影响）。若落地契约支持「只提交骨架」的 partial 提交，则优先 partial、其次 raw，**以契约为准，不自创状态**。
5. 成功响应：

```jsonc
{ "share_id": "ab12cd34ef", "status": "ready", "version": 1, "sentences": 15,
  "provider": "agnes", "model": "agnes-3.0-flash", "prompt_version": "enrich-v1",
  "deduplicated": false, "warnings": [] }
```

权限：`added_by` 默认取 JWT 用户名；传了与本人不同的 `added_by` 时，要求 JWT 角色为 admin，否则 403（TtsWeb 侧对 agnes-service 令牌的代填白名单是第二道闸，见 §9）。
幂等：同一 text 重复调用，靠 reading 的 content_hash 去重收敛到同一 share_id（第 1 步的 preview 探测或 commit 的去重返回），不产生重复文章；并发重复调用的收敛以 TtsWeb 契约为准，本端不另加锁。

## 9. 协调项（第二期执行，时序钉死）

1. **tr_via DDL**：`reading.sentences.tr_via` 的 CHECK 增加 `'agnes-api'`、`'gemini-api'`。三处同落：共享 DDL 文件（本机 `~/workspace/tts-redesign/reading_ddl.sql`）、77B 的 db77database、本机镜像库。**时机**：P2a 回报之后、P2b 之前；动 DDL 文件前与朗读项目确认不在编辑同一文件。变更向后兼容（只扩取值），现存数据不动。
2. **TtsWeb 令牌登记**：在改造后 TtsWeb 的配置文件令牌表里登记 `agnes-service`（可写；代填 added_by 白名单 {A001, A002, A003}，与驻地令牌同等待遇）。令牌值由实现者生成（32 字节随机），同时写入 TtsWeb 配置与 agnes-service 的 .env（`TTSWEB_TOKEN=`），两处文件权限 600，只回报「已配置」。
3. **/ingest.md 补充文字**：本任务产出一段「执行者：agnes-service（API 自动入库）」的说明文本（含端点、适用场景、与驻地执行者的分工），放进回报，由朗读项目合并进 /ingest.md —— **不要在本任务里直接改 TtsWeb 的 ingest.md 文件**（P2 施工中，避免抢文件）。

## 10. 配置新增（.env / Settings）

```
GEMINI_API_KEY=            # 用户本人填写；留空 = gemini 未就绪（正常状态）
GEMINI_API_URL=            # 留空用默认 Google 兼容端点
ANALYZE_DEFAULT_PROVIDER=agnes
ANALYZE_PROVIDER_ORDER=agnes,gemini
ANALYZE_TIMEOUT_S=180
TTSWEB_BASE_URL=http://127.0.0.1:8791
TTSWEB_TOKEN=              # 第二期由实现者生成并双边登记（见 §9.2）
```

同步 `.env.example`（或 README 配置节）。Settings 热重读机制应覆盖新键，复核并说明。

## 11. 验收

构建与单测（两期共同）：`dotnet build -c Release` 0 警告 0 错误；ruby 推导单测覆盖送假名/纯假名/数字词/无后缀/整词注音（无测试工程时以隐藏自检入口代替并说明）；前端 JS 改动过 `node --check`。

第一期（开发实例 + MockAgnes，Mock 按需扩展 analyze 场景：正常 JSON、拼接错误、非法 JSON、403、超时）：
1. raw 正常 → 双形状齐、pos 映射与 ruby 推导对；
2. 先错后对 → 修复重试生效（attempts=2）；
3. agnes 形状 403 + gemini 形状（开发实例把 GEMINI_API_URL 指向 mock 并填 dummy key）正常 → 兜底生效、call_log 有轨迹；
4. enrich 正常全过；越界 tok_i 被校验拦下；
5. 未登录 401；测试账号配额改小后超额 429 且 call_log 有 kind=analyze 记录；
6. /health 的 analyze 段与配置一致。
真实 Agnes 冒烟（各 1 次）：raw 用 `彼女は静かな図書館で本を読んでいる。` 与 `一本のペンと十八年の手帳。`；enrich 用任一现成骨架。回报附完整输出与「默认模型是否合适」的一句话结论。

第二期（先对 P2 的开发/已切换实例，再对生产）：
1. 新短文一键入库 → 返回 share_id、`status:ready`；用 TtsWeb 只读 GET（或 /t/<id>?with=sentences）核对：句数、逐句 tr、fixes 已套用（读音确已改）、tr_via/tr_by 出处正确、versions 有本版记录；
2. 同一 text 再调 → `deduplicated:true`、无新版本、无 LLM 调用（call_log/attempts 为证）；
3. `replace:true` 再调 → 新版本生成、current_version 切换；
4. 故意让 enrich 失败（mock 后端全挂）→ 降级 raw 成功、短链可打开、原文在库；
5. 非 admin 传他人 added_by → 403；admin 代填 → 落库 added_by 正确；
6. 本机镜像在下一次 pull 后能看到该文（行数/哈希对账由 pull 机制自带，回报其结果）；
7. 旧短链抽查 2 条仍可打开（/share 兼容未被编排影响）。

生产切换（每期各一次）：publish → 重启 → 本机与公网验 /health、未登录 401、前端含「分析」筛选；测试账号与数据清理干净。

## 12. 回报格式

做了什么（文件清单 + 备份名）、构建结果、§11 每项的实测证据（命令与关键输出）、真实冒烟完整输出、与任务书的偏差（逐条及原因）、协调项执行凭证（DDL 三处落地与对账结果、令牌已配置、ingest.md 补充文本全文）。遇到任务书没覆盖、需要用户拍板的新问题：停下并在回报中列明，不要自行扩范围。
