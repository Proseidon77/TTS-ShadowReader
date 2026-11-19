# TASK6 评审报告（深接法：analyze 底座 + /api/reading/ingest 编排）

评审日期：2026-11-18 ｜ 评审对象：`TASK6.md`（修订版）｜ 评审方式：只读（代码阅读、psql SELECT、`ss -ltn`、对本机端口的 GET 探测）。未改任何代码/配置/数据库/服务，未调用 Agnes API。

> **评审范围限制（须先知悉）**：本 session 的文件权限只覆盖 `/data/workspace/agnes-service/`。读取 `~/workspace/tts-redesign/`（TASK6 §9.1 指向的 DDL 文件）被权限拒绝，P2a 的 TtsWeb 工作目录及 `ingest.md` / `ingest_prompt.md` / 任务书草案**均未能读到**；运行中的 TtsWeb（8791）也不提供这两个文件（GET 返回 404，见 A-7）。因此「契约风险」一节只能对照**数据库里已落地的 reading DDL**（列、CHECK、索引、列注释、现存数据）来核，凡需对照 P2a 草案文本的条目标为「未核」。这一点本身就是第二期的开工闸门（见 B1）。

---

## 一、总体结论

**修改后可行。** 第一期（provider 注册表 + `/api/analyze`）的地基与现有代码契合度高，改几处就能开工；第二期的编排端点目前有**身份模型、pos 枚举、去重语义、gloss 落点、长文超时/并发预算**几处与实况直接冲突，且所依赖的 P2 preview/commit 在 77B 上尚未运行、契约文本未能核对，须按本报告改口径并把第二期改成「契约落地后再定稿」才能实施。

---

## 二、假设核对表

| # | TASK6 的主张 | 实况（证据） | 成立？ |
|---|---|---|---|
| A-1 | `Generate()` 管线：鉴权→读 body→校验→限额准入→上游→call_log，可直接接 kind=`analyze` | 形状成立（`Program.cs:190-228`）。但有三处硬编码与 analyze 不匹配：① prompt 摘要只取 `body["prompt"]`（`Program.cs:195`），analyze 的 `text`/`sentences` 会记成 null；② 只要 `ApiError.UpstreamStatus` 是 4xx 就**退还日额度**并记 `upstream_rejected`（`:209-214`）——兜底后成功、或 TtsWeb 返回 4xx 时都会记错账；③ 成功时的模型名取 `result["model"]`（`:225`），没有 `provider:` 前缀 | 部分成立 |
| A-2 | LimitService 的 kind 集合是 text/image/video；需复核每日总量是否自动包含新 kind | `Kinds = { "text","image","video" }`（`LimitService.cs:27`）。总量 `used.Values.Sum()`（`:182`, `:295`），而 `ReadUsage` 对 DB 里出现的任何 kind 都直接赋值（`:110` `used[r.GetString(0)] = …`）→ **总量会自动包含 analyze，不是硬编码求和**。但 `View()` 的 `used` 对象只写了三种 kind（`:317`），`counted_endpoints` 也写死了三个端点（`:330`）→ `/api/limits`、`/api/auth/me` 看不到 analyze 用量 | 成立（View 需补） |
| A-3 | `usage_daily` 与 `call_log` 的 kind CHECK 都是 ('text','image','video')，约束名待实查 | 实查 `pg_constraint`：`agnes_auth.usage_daily.usage_daily_kind_check`、`agnes_auth.call_log.call_log_kind_check`，定义都是 `CHECK ((kind = ANY (ARRAY['text','image','video'])))`；`auth_schema.sql:91`, `:124` 一致 | 成立 |
| A-4 | Settings 有 .env 热重读，新键应能被覆盖 | `Settings.Get` 是通用按名读取，按 mtime 重读文件（`Settings.cs:69-97`），新键**自动覆盖**。但**环境变量优先于文件**（`:89-90`），而 systemd 单元用 `EnvironmentFile=-…/.env`（`agnes-service.service:15`）把 .env 在启动时灌进了进程环境 → 启动时**非空**的键，之后改 .env **不生效，要重启**；启动时为空的键（`KEY=`）会回落读文件，所以「GEMINI_API_KEY 日后填入即生效」成立，「轮换 TTSWEB_TOKEN 即生效」不成立 | 部分成立 |
| A-5 | `/health` 现有字段形状，可加 `analyze` 段 | 实测 `GET :8793/health` 返回 `status/version/stack/key_configured/auth{…db{…calls_ready}}/limits_default/upstream/uptime_s`，与 `Program.cs:79-104` 一致；顶层加 `analyze` 不冲突。注意 `/health` 无需登录（公开） | 成立 |
| A-6 | MockAgnes 在 127.0.0.1:18793，可按需扩展 analyze 场景 | 存在（`tests/MockAgnes/Program.cs`）。现有能力仅：固定 key `mock-key-not-real`（非此即 401）、`sleep:N`、含 `reject`→400、含 `boom`→500、chat 固定回 `"mock reply"`。**没有** JSON 内容、403、按 provider 区分的能力；gemini 路径若指向它，dummy key 必须恰好是 `mock-key-not-real`。18793 当前未被占用 | 成立（扩展量不小） |
| A-7 | 生产 TtsWeb 在 **127.0.0.1:8791**，是 77B 侧真源实例 | `ss -ltn`：8791 由 `python` 监听在 **0.0.0.0**（非 127.0.0.1）；响应头 `Server: Python/3.13 aiohttp/3.14.5`，首页是 `player_v2.html`。`/ingest.md`、`/ingest_prompt.md` 均 404；`/t/a798e5d069` 能取到（77b 来源），`/t/ce605b3d6e`（ddpc 来源、已在 reading 库）404 → **8791 仍是 P2 前的旧版 TtsWeb，不读 reading 库，没有 preview/commit** | 端口成立，绑定地址与「可调 preview/commit」不成立 |
| A-8 | 8793 = agnes-service 生产 | `dotnet` 监听 0.0.0.0:8793，`/health` uptime ≈ 6.6 h | 成立 |
| A-9 | P2a 开发版 TtsWeb 正在用 8794 | 评审时 **8794 无监听**（P2a 开发实例当时未运行）；但本项目 `tests/port8794.env`、`tests/run_test_8794.sh` 仍硬编码 8794。8795 空闲；8792 被一个本用户看不到属主的进程占用 | 「已占用」此刻不成立，「应避开」仍成立 |
| A-10 | reading 现行 pos 枚举与 §4 映射表对齐（pron/adjn/adn/intj…） | reading 库现存 toks 的 pos 实值（82 句全量统计）：`noun particle verb aux sym affix num adv adj pronoun conj`。对照：§4 的 `pron` ≠ 实值 **`pronoun`**；形容動詞→`adjn` 不成立——reading 把形状詞记成 **`adj`**（例：`静か` pos=adj, pd=形状詞-一般）；reading 有 `affix`、`num`，§4 表里没有来源；`adn`/`intj` 在现存数据里未出现（无法从数据证实或证伪，需看 P2a 分词代码）。另 toks 还有 `sub`、`unk` 两个键，§4 未提 | **不成立** |
| A-11 | reading.articles 字数上限 5000 | `articles_text_check: char_length(text) BETWEEN 1 AND 5000` | 成立 |
| A-12 | `sentences.tr_via` 需扩取值 | 实查 `sentences_tr_via_check`：`('self','claude-code','agy-gemini','human','import','provided')`。注意已有 **`agy-gemini`** | 成立（需决定与 agy-gemini 的关系） |
| A-13 | 出处记在 `tr_by/tr_via`，模型记在 `versions.tool` | sentences 另有 **`tr_model`**、`tr_at` 两列；versions 另有 **`created_by`**、`note` 两列。`versions.tool` 列注释：`{kind: import\|commit\|replace, executor, model, prompt_version, splitter, tokenizer, dict, align, ...}`——TASK6 的 executor/model/prompt_version 与注释吻合，但 `tool.kind` 由 commit 侧决定 | 部分成立（漏 tr_model/created_by） |
| A-14 | 去重靠 reading 的 content_hash | `articles_content_hash_uq UNIQUE (content_hash) WHERE deleted_at IS NULL`；软删后同文可重新入库 | 成立 |
| A-15 | `added_by` 默认取 JWT 用户名；TtsWeb 代填白名单 {A001,A002,A003} | reading 现存 `added_by` 是 `A001`/`A003`（`CHECK char_length ≤ 40`）；agnes_auth 用户名是 `TestUser001`(admin)、`TestUser002`(user)。两套身份**没有映射** | **不成立** |
| A-16 | 「已存在则直接返回短链」能省 LLM | reading 现存 6 篇**全部 `status=partial`**、82 句 `tr/tr_via/tr_model` 全为空。按 §8 第 1 步，这些文章再入库会被「去重」直接返回——**永远拿不到译文** | **不成立**（语义错误） |
| A-17 | enrich 产出 gloss，commit 时提交 | sentences 无 gloss 列；toks 键集合（`conj l pd pos r ruby s sub unk`）无 gloss 字段；`versions.tool` 注释也未提 → 现行 DDL 中**无 gloss 落点** | 未核到落点 |
| A-18 | 三处 DDL：共享文件 `~/workspace/tts-redesign/reading_ddl.sql`、77B db77database、「本机镜像库」 | DDL 文件读权限被拒（未核）；77B 库 reading schema 存在、属主 `user`（与 agnes-service 连接身份相同）；「本机镜像库」——任务在 77B 执行，而镜像在 ddPC，「本机」指代不清 | 未核 / 表述有歧义 |
| A-19 | Agnes 认 json_object、2.5-pro 403 等上游实测结论 | 按评审约束未调用真实 API | 未核（沿用） |
| A-20 | 有现成测试工程可放 ruby 单测 | 无测试工程；`AgnesService.csproj:12` 把 `tests/**` 排除出编译；包从本地 NuGet 缓存还原（`:17` 注释），xunit 是否在缓存里未知 → 「隐藏自检入口」才是现实方案 | 按 TASK6 的备选方案成立 |

---

## 三、必须修改（blocker）

### B1. 第二期的开工前提目前不成立，§8 的字段名不能当定稿
- **证据**：A-7（8791 是旧版 aiohttp，无 preview/commit/ingest.md，不读 reading 库）；A-9（8794 此刻无实例）；P2a 草案文件不可读（见开头说明）。
- **问题**：TASK6 把「P2a 回报」当第二期的时间锚点，但编排要调的是**77B 真源上已切换的** TtsWeb。只有开发实例可用时，第二期验收又会直接写进真源库（见 B9）。§8 里 `replace/base_version`、`TR_MISSING`、`tool.executor`、partial 提交等写成了具体口径，现在都无法与契约对照。
- **改法**：
  1. 第二期开工前提改写为三条，并逐条要求凭证：(a) P2 的 preview/commit **已在 8791 上线**（或明确给出编排应调的、写真源的端口）；(b) `ingest.md`/`ingest_prompt.md` 落地版可 GET；(c) 服务令牌表机制已在 TtsWeb 中实现。
  2. §8 改成「契约映射表待填」：列出编排需要的字段（规范化原文、句/骨架、去重命中及已有文章 status、版本号、提交 tr/fixes/出处的字段、错误码表、partial 提交是否存在），第二期第一步按落地契约填表，回报中逐项对照。§8 现有字段名降级为「示意」。

### B2. 身份模型对不上：`added_by` 不能默认取 JWT 用户名
- **证据**：A-15。reading 用 `A001/A003` 这类编号，agnes_auth 是 `TestUser001/002`；§9.2 让 TtsWeb 对 agnes-service 令牌只放行 {A001,A002,A003}。
- **后果**：非 admin 用户每次调用，`added_by` 要么被 TtsWeb 白名单拒（commit 失败→降级 raw，降级的 /share 也带 added_by，可能同样被拒），要么把 `TestUser002` 这样的名字写进朗读库，污染出处。
- **改法**：引入显式映射——推荐在 `agnes_auth.users` 加可空列 `reading_added_by text CHECK (char_length ≤ 40)`（管理页可编辑），或用 .env 键 `READING_ADDED_BY_MAP=TestUser001:A001,…`。规则：没有映射的账号调 ingest → 403 `reading_identity_unmapped`（准入前判，不扣额度）；代填他人仍要求 admin。哪种方案由用户拍板（见 D2）。
- **状态更新（2026-11-18 用户拍板，本条关闭）**：added_by 按提交通道记真实提交者，不做跨体系映射——agent 通道记 Muse 编号（A001/A002/A003），网页通道记登录的 Agnes 账号名，字段里能区分来源即可。按此口径，「会被白名单拒/记错名」的问题不存在：白名单只约束 agent 通道，网页通道的账号名是正当取值。原改法（映射列/.env 映射表）与 D2 一并作废；剩下的只是契约细节（来源区分方式、TtsWeb 侧对 agnes-service 令牌放行 Agnes 账号名形态），由降级设计稿一并给出。

### B3. pos 映射表与 reading 实值不一致
- **证据**：A-10。
- **改法**：§4 映射表改为：代名詞→`pronoun`（不是 pron）；形容動詞/形状詞→`adj`（不是 adjn）；接頭辞/接尾辞→`affix`；数詞→`num`（若提示词让模型单列数词）；連体詞/感動詞的目标值**以 P2a 分词代码的枚举常量为准**，未定前映射为 `adn`/`intj` 会产出 reading 不认的值。实现前从 TtsWeb 代码取枚举全集，把映射表写成常量并在 `/health` 或回报中列出。
- 补充：raw 模式按学校文法「形容動詞整体」切分（`静かな` 一个 token），而 reading 骨架是 UniDic 切分（`静か`+`な`，见 ce605b3d6e seq 0）。raw 的「reading 形状」只是**形状一样，切分并不等价**，§4 应写明它不能当骨架用（与「不做纯 LLM 替代 fugashi」一致）。

### B4. 去重语义会让存量 partial 文章永远拿不到译文
- **证据**：A-16（6/6 篇 partial、82/82 句 tr 为空）、A-14。
- **改法**：§8 第 1 步改为按已有文章的 status 分支——`ready` 且未带 `replace` → 返回已有短链（`deduplicated:true`，不调 LLM）；`raw`/`partial` → **照常 enrich + commit**（以当前版本为 base，按契约产生新版本或补译），响应 `deduplicated:false, upgraded_from:"partial"`。§11 第二期验收第 2 条相应补一条「对 partial 文章入库 → 变 ready」。软删文章（`deleted_at` 非空）不参与去重（唯一索引是部分索引）。

### B5. gloss 与出处字段在 DDL 里没有或不全
- **证据**：A-13、A-17。
- **改法**：
  - gloss：现行 DDL 无落点。第二期提交时**默认不提交 gloss**；enrich 默认 `gloss:false`（省 token），等契约给出字段（比如 `toks[i].g`）后再开。`/api/analyze` 可以保留 gloss 作为可选输出。
  - 模型：除 `versions.tool.model` 外，逐句写 **`tr_model`**（形如 `agnes-3.0-flash`；provider 由 tr_via 表达）。
  - 执行者：`versions.created_by` 是现成列，`executor` 是放 `tool` 里还是写 `created_by`，按契约定；`tool.kind`（commit/replace）由 TtsWeb 决定，agnes-service 不要自己写。
  - tr_via：现有 `agy-gemini` 表示「经 agy 会话调 Gemini」，新增 `gemini-api` 语义上可以区分，但须用户确认（见 D3）。

### B6. 长文 enrich 的 token 量与超时、并发预算算不过来
- **证据**：ce605b3d6e 第 0 句原文 27 字，其 toks JSON 约 1.4 KB；按此推算，5000 字文章的完整骨架约 250 KB JSON（约 8–12 万 input token），输出要覆盖 100+ 句的译文。§3 是单次 180 s、最多 4 次上游调用，§8 的 commit 修复重试还会**额外**跑一轮 enrich（是否计入 4 次未定义）。整个过程中准入票据一直被占用（`Program.cs:206` `await using var _ticket`），而默认每账号并发只有 2、全局 6（`/health` 实测）。
- **后果**：单次调用容易超时或因 `finish_reason=length` 被截断；「seq 集合必须完全一致」会因为一句出错整篇作废，再整篇兜底重来，最坏耗时 > 12 分钟；同时占住 1/6 的全局并发槽。
- **改法**：
  1. **紧凑投影**：发给模型的不是原样骨架，而是 `[{i, s, r}]`（加上 `unk` 标记），不带 `l/pd/conj/ruby/sub`，体积约降到 1/5。
  2. **分块**：按约 20 句或约 1500 字一块，逐块 enrich；校验、修复、兜底都**按块**进行，失败只重做失败块（这时「4 次上限」改为「每块 ≤3 次、整单 ≤ N 次」）。
  3. **总时限**：新增 `INGEST_DEADLINE_S`（建议 300），用剩余时间裁剪单次超时；到时限还没完成的块按契约走 partial，没有 partial 就走 raw。
  4. 识别 `finish_reason=length`，按「块太大」处理（对半拆分），不要当校验失败去发修复提示。
  5. 若公网入口对长请求有超时（instabox 代理的上限未核），考虑把 ingest 做成「提交即返回 job_id + 轮询」（与现有 video 的 poll 模式同构，见 D4）。

### B7. 新密钥不会被脱敏；Agnes.Call 写死了 Agnes 的 key 和 base
- **证据**：`Agnes.Redact` 只替换 `AgnesApiKey`、`JwtSecret`（`Agnes.cs:44-49`）；`Agnes.Call` 固定用 `Settings.AgnesApiKey` 和 `AgnesBase`（`:56-58`），错误文案写的是 "Agnes API"。
- **后果**：GEMINI_API_KEY、TTSWEB_TOKEN 如果出现在异常信息或上游回显里，会原样进入日志和 `call_log.detail`，违反 §2 的硬规矩。
- **改法**：把 `Call` 改成 `Upstream.Call(name, baseUrl, key, …)`，Agnes 的原有调用改为传参（行为不变）；`Redact` 改为遍历一个「敏感键名集合」（AGNES_API_KEY、JWT_SECRET、GEMINI_API_KEY、TTSWEB_TOKEN）。不要另写一套 HttpClient（复用 `Agnes.Http` 的连接池与超时模式）。

### B8. §10 的 .env 片段带行内注释，会被当成值
- **证据**：`Settings.Parse` 不剥离行内 `#`（`Settings.cs:62-64`，取 `=` 之后整段 Trim）；systemd `EnvironmentFile` 也不把行内 `#` 当注释。
- **后果**：照抄 §10 后，`GEMINI_API_KEY` 的值会变成 `# 用户本人填写；留空 = …`（非空），gemini 被判为 ready 并带垃圾 key 出站；`GEMINI_API_URL`、`TTSWEB_TOKEN` 同理。
- **改法**：§10 和 `.env.example` 的注释一律单独成行（现有 `.env.example` 就是这种写法）。另外在就绪判定里加格式校验（URL 能被 `Uri.TryCreate` 解析、key 不以 `#` 开头）。

### B9. 管线记账与第二期测试数据都会写错地方
- **证据**：A-1 ②；`CallLog.Write` 把 detail 截到 **300 字**（`CallLog.cs:24`）；开发实例与生产共用 `.env` 和同一个库（`tests/run_test_8794.sh` 注释），reading 也在同一个 db77database，且是真源（A-18），会被 pull 到 ddPC 镜像（§11 第二期第 6 条正是依赖这一点）。
- **改法**：
  - 管线：给 `Generate` 加两个参数——`Func<JsonObject,string?> preview`（analyze 取 text 或第一句），以及由 run 返回的 outcome 记录（实际 provider/model、尝试轨迹、是否退额）。TtsWeb 侧错误**不要**设 `UpstreamStatus`（否则会被当作「Agnes 拒绝」而退额）。兜底后成功不退额；全部上游都返回 4xx 才退额。
  - detail：改用紧凑编码（如 `fb=agnes:403>gemini:ok;v=ok;ingest=ab12cd34ef`），或把截断上限提到约 1000 字（列是 text，无 DDL 变更）。
  - 测试数据：第二期功能测试用 **Mock TtsWeb**（preview/commit/share 的最小桩）完成；对真源只跑验收要求的少量用例，标题统一加前缀（如 `[agnes-ingest-test]`）。清理方式（软删 `deleted_at` 还是交给朗读项目处理）必须先与朗读项目约定，因为数据会同步到 ddPC 镜像。

---

## 四、建议优化

| # | 建议 | 收益 | 代价 |
|---|---|---|---|
| O1 | **先做同 provider 内的模型级兜底**：agnes-3.0-flash → agnes-2.5-flash，再轮到 gemini。Agnes 的 403 insufficient_user_quota 是按模型生效的（pro 403、flash 正常） | 今天就有可用的兜底路径（gemini 没有 key，§11 第一期第 3 条只能靠 mock 测） | 注册表多一层 model 顺序配置 |
| O2 | **Gemini 先只做 json_object + reasoning_effort**，`json_schema strict` 推迟到拿到真 key 并实测后再做 | 少写两套 schema、少一个无法真测的分支（Gemini 的 OpenAI 兼容层对 strict schema 有限制，现无 key 无法验证） | 拿到 key 后补一轮 |
| O3 | **raw 模式只出一种形状**（建议只出 analyzer 形状 + 映射后的 pos），去掉 reading 形状和 ruby 推导；或者整体推到第二期之后 | ruby 推导只服务于 raw 的 reading 视图——enrich 不返回 toks，fixes 套用后的 ruby 重算在 commit 侧（TtsWeb）。可省掉一个算法 + 单测 + 双形状维护 | 若日后要在前端直接展示 raw 结果再补 |
| O4 | 复用而不是新建：`Upstream.Call`（B7）、`Generate`（B9）、`Settings.Get`、`Json.ReadBody`、`ApiError`、管理页现有筛选组件；provider 注册表用一个静态字典 + record 即可，不要做 DI/接口层 | 改动面小，符合现有 minimal API 风格 | 无 |
| O5 | **失败分类细化**：上游 4xx（参数类，不兜底）/ 403、429（配额，换模型或 provider）/ 5xx、超时（兜底）/ JSON 解析失败（修复）/ 校验失败（修复）/ 截断（拆块）/ TtsWeb 4xx（输入问题→直接 400，不降级）/ TtsWeb 5xx 或不可达（503；**此时降级到 /share 也没有意义**，因为是同一服务）。每类有固定的 outcome 码写进 call_log | 降级只在有意义时发生，管理页能看出原因 | 一张错误映射表 |
| O6 | **进程内按文本哈希加锁**（单实例、`ConcurrentDictionary<hash, SemaphoreSlim>`）：同一文本的并发 ingest 串行，后到的直接复用结果 | 避免并发重复调用时 LLM 白跑一遍，再在 commit 的唯一索引上撞车 | 约 20 行；哈希只做锁键，去重仍以 TtsWeb 的规范化为准 |
| O7 | 准入前做全部廉价校验：`lang` ∈ ja/zh/en 且 enrich 仅限 ja（其他语言直接走 /share 或 400，由用户定）、`rate` 匹配 `^[+-][0-9]+%$`、`title` ≤120、`added_by` ≤40、text 1–5000 | 对照 `articles_*_check`，400 不扣额度 | 无 |
| O8 | 提示词与骨架之间用下标对齐：投影里给 `i`，要求模型的 fixes 回传 `i` 与原 `s`，服务端核对 `s` 一致 | 防止模型数错下标（越界校验拦不住「错位但不越界」） | 输出略增 |
| O9 | MockAgnes 扩展采用「场景前缀」：在 user 内容里带 `scenario:json_ok|json_bad_concat|json_invalid|403|sleep:N`；gemini 测试用同一个 mock 的另一路由（如 `/gemini/chat/completions`），校验另一个 dummy key | 能在 call_log 里区分出 provider，验收第 3 条才有说服力 | 改 mock 约 60 行 |

---

## 五、遗漏项（实现时一定会撞到）

1. **管理端 kind 白名单**：`AdminApi.cs:499-500` 只接受 text/image/video，不加 analyze 的话筛选请求会 400。TASK6 §7 只提了前端（`index.html:230` 的 option、`:802` 的 kindZh）。
2. **`LimitService.View` 的用量与 `counted_endpoints`**（`LimitService.cs:317`, `:330`）要补 analyze 与两个新端点；`Kinds`（`:27`）也要补，否则日后代码访问 `used["analyze"]` 会抛 KeyNotFound。
3. **测试脚本端口**：`tests/port8794.env`、`tests/run_test_8794.sh` 硬编码 8794，必须改成 8795（或新建 `port8795.env`），否则与 P2a 撞端口。
4. **重启会打断长请求**：`TimeoutStopSec=20`（`agnes-service.service:24`）。publish 后重启时，正在跑的 ingest 会被杀，降级逻辑也来不及执行。生产切换前要确认没有在途 ingest（可在 `/health` 暴露 in-flight 数）。
5. **客户端断开**：`Generate` 把 `ctx.RequestAborted` 一路传下去，调用方断线时可能出现「LLM 已花费、commit 没做」。需要决定 commit 阶段是否改用独立的 CancellationToken 继续完成（建议继续，保证原文不丢的承诺）。
6. **unk / 无读音 token**：骨架里外文词是 `r:null, unk:true`（ce605b3d6e 的 `quiet`），fixes 的「纯平假名」校验与提示词都要显式排除这类 token，否则模型会给英文词编假名。
7. **fixes 的存储形状**：`sentences.fixes jsonb NOT NULL DEFAULT '[]'` 已存在，说明 commit 侧会把 fixes 原样落库；`{tok_i, reading}` 这个键名必须与契约一致（未核），否则会落进库里却没被套用。
8. **DDL 迁移幂等写法**：auth_schema.sql 现有段落都是 `IF NOT EXISTS`，替换 CHECK 需要 `DO $$ … IF EXISTS(pg_constraint …) THEN ALTER TABLE … DROP CONSTRAINT …; ADD CONSTRAINT …` 的写法，并保持约束名 `usage_daily_kind_check`/`call_log_kind_check` 不变，这样管理工具的引用不会失效。要先部署 DDL、后部署代码（只扩取值，向后兼容）。
9. **/health 的 analyze 段别泄露配置细节**：`/health` 是公开端点，只给 ready 和模型名，不要给 URL（`upstream` 字段已暴露 Agnes base，GEMINI_API_URL 就不必再暴露）。
10. **§9.1「本机镜像库」**：执行者在 77B，镜像在 ddPC；77B 的实现者既碰不到 ddPC 的库，也不该碰。应改为「由朗读项目在 ddPC 执行，或由镜像 pull 自动带过去」，并写明谁负责。
11. **TtsWeb 令牌登记属于改 TtsWeb 配置**：P2 施工期间由 agnes-service 的实现者去改 TtsWeb 的配置文件，和「不抢文件」的原则冲突。建议实现者只生成令牌并写入自己的 .env，把「令牌哈希 + 白名单」作为交付物交给朗读项目登记（令牌值通过双方都能读的 600 权限文件传递，不经聊天）。

---

## 六、需用户拍板

- **D1 第二期的触发点**：等 P2 在 8791 切换上线后再开工（推荐），还是允许先对 P2a 开发实例（写真源库）开发？
- **D2 身份映射方案**：~~`agnes_auth.users.reading_added_by` 列（推荐，管理页可维护），还是 .env 映射表；未映射账号是禁止 ingest，还是统一记为某个默认编号？~~ **已拍板（2026-11-18）：不做映射，按提交通道记真实提交者（agent 通道记 A00x、网页通道记 Agnes 账号名），本条关闭。**
- **D3 tr_via 取值**：新增 `agnes-api`、`gemini-api`（TASK6 原案），还是 Gemini API 复用 `agy-gemini`？是否同时规定 `tr_by='agnes-service'`、`tr_model=<model>`？
- **D4 ingest 同步还是异步**：同步阻塞（最长可达分钟级），还是返回 job_id 轮询？
- **D5 计费口径**：一次 ingest 只算 1 次准入（原案），还是按块数计？长文一次调用可能消耗原来 10 倍以上的上游 token。
- **D6 gloss 是否进第一版**：契约无落点时，是否先不做？
- **D7 raw 模式范围**：是否按 O3 精简为单一形状、去掉 ruby 推导？
- **D8 非 ja 文本**：ingest 收到 zh/en 时是直接存原文，还是 400？

---

## 七、修订后的实施顺序建议

1. **TASK6 改稿**（实施前完成）：落实 B2–B8 的口径；第二期改为「契约映射表待填」；拍板 D1–D8。
2. **第一期·地基**（不依赖 P2）：
   1. 先写 `auth_schema.sql` 的幂等 DO 块（kind 加 analyze）→ 对现库执行（向后兼容）→ 查 pg_constraint 留证。
   2. `Upstream.Call` 抽取 + `Redact` 扩展（B7）；Agnes 原有三条路径回归测试（8795 + MockAgnes）。
   3. provider 注册表（模型级兜底优先，O1/O2）+ `.env.example` 加键（注释单独成行，B8）。
   4. `Generate` 扩展（preview 选择器、outcome 记录、退额规则，B9）；`LimitService.Kinds/View`、`AdminApi` 白名单、`index.html` 筛选。
   5. `/api/analyze`：先做 enrich（紧凑投影 + 分块 + 按块校验/修复，B6），再做 raw（按 D7 的结论）；pos 映射用从 TtsWeb 代码取到的枚举（B3）。
   6. MockAgnes 场景扩展（O9）→ 跑 §11 第一期验收 → 真实冒烟各 1 次 → publish + 重启（确认没有在途请求）。
3. **闸门检查**：P2 在 8791 上线；GET `/ingest.md` 能取到；按契约填写映射表（规范化、去重返回、status、版本、fixes/tr/tr_model/created_by/gloss 字段、错误码、partial 提交），与本报告 B4/B5 对照，回报差异。
4. **第二期·编排**：
   1. 先做 Mock TtsWeb，在 8795 上完成全部分支（ready 去重、partial 升级、replace、降级、身份 403、并发同文）。
   2. 协调项：令牌（按遗漏项第 11 条的交付方式）、tr_via DDL（时机与文件锁由朗读项目确认；77B 库由本任务执行，ddPC 镜像由朗读项目执行）、ingest.md 补充文本作为交付物。
   3. 对真源跑少量带前缀的验收用例 → 按约定清理 → publish + 重启 → 冒烟。