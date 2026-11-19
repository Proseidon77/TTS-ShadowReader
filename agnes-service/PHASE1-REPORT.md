# TASK6 第一期回报（analyze 底座）

日期：2026-11-19 ｜ 机器：77B ｜ 实现依据：TASK6.md + TASK6-REVIEW.md（冲突处以评审为准）+ TASK6-FALLBACK-DESIGN.md 的 L1 顺序

## 0. 结论先行

- **开发实例 8795 上全部通过**：`dotnet build -c Release` 0 警告 0 错误；`--self-test` 52/52；Mock 验收（含既有 text/image/video 回归 + §11 第一期 1–6）84/84；超时兜底 4/4；真实 Agnes 冒烟 3 轮全部 200。
- **DDL 已对现库执行**（只扩 kind 取值，向后兼容），pg_constraint 留证见 §2。
- **生产没有发布、没有重启——停在发布闸门。** 原因：本 session 的权限规则拒绝 `sudo` / `systemctl`（`sudo -n true`、`systemctl status agnes-service` 都返回 "requires approval"，非交互会话无法批准）。生产跑在 `publish/` 上，不能先覆盖它、过后再找人重启（覆盖运行中进程的程序集有风险），所以 `publish/` 一字未动。已经把发布产物放到 `.probe/publish-task6p1/` 并核过（含新 index.html，`--self-test` 52/52），交接命令见 §7，大约 1 分钟能做完。
- 两项**没能执行**的检查（同样是权限拦截，没有绕过）：`node --check`（node 被拒）和 `smoke.sh`（带环境变量调用被拒）。替代证据：前端 diff 只有两处字面量改动（§4.7）；smoke.sh 的公开端点与 401 检查已在验收脚本 `public_checks()` 里逐条复刻，并且通过。
- 生产现在的状态：旧二进制照常运行；DB 约束已扩（旧代码不受影响）；`.env` 多了 5 个新键（旧代码不读）。测试账号、测试数据已清零（§5）。

## 1. 文件清单

新增：

| 文件 | 内容 |
|---|---|
| `src/Upstream.cs` | 通用 `Upstream.Call(name, baseUrl, key, keyName, …)`（B7），复用 `Agnes.Http` 连接池；`Redact` 遍历敏感键集合 `AGNES_API_KEY / JWT_SECRET / GEMINI_API_KEY / TTSWEB_TOKEN` |
| `src/Analyze.cs` | provider 注册表（静态字典 + record，O4）、`/api/analyze` 引擎（raw/enrich）、提示词常量 analyze-v1 / enrich-v1、校验、修复重试、兜底、分块/拆块、pos 映射、ruby 推导、隐藏自检 `--self-test` |
| `tests/port8795.env`、`tests/mock8795.env`、`tests/run_test_8795.sh` | 8795 开发实例 overlay（8794 留给朗读项目）；mock overlay 把 agnes 与 gemini 都指向 MockAgnes，dummy key 只发给本地 mock |
| `tests/task6_accept.py` | 验收脚本：`mock`（回归 + §11 1–6）、`timeout`、`real`、`real1`、`prod`；每次运行自建一次性账号（随机密码经 stdin 交给 CLI，不打印），结束时删除 |
| `PHASE1-REPORT.md` | 本文件 |
| `.probe/task6_kind_migration.sql` | 对现库执行的 DO 块（从 auth_schema.sql 原样摘出） |
| `.probe/publish-task6p1/` | 待发布产物（staging，未上线） |
| `.probe/task6_*.txt` | 各轮运行日志（下文全文引用） |

修改（全部先做了备份，后缀 `.bak-20261119-task6p1`）：

| 文件 | 改动 |
|---|---|
| `auth_schema.sql` | 两处建表 CHECK 加 `'analyze'`；新增幂等 DO 块：约束定义里没有 `'analyze'` 才 DROP + ADD，约束名不变 |
| `Program.cs` | `--self-test` 入口；`/health` 加 `analyze` 段；`Generate` 加 `preview` 选择器与 `CallOutcome`（B9）；新路由 `POST /api/analyze` |
| `src/Agnes.cs` | `Call` 改为调用 `Upstream.Call("Agnes API", AgnesBase, AgnesApiKey, "AGNES_API_KEY", …)`，错误文案与行为不变；`Http` 改为 internal 以便共用；`Redact` 委托给 Upstream |
| `src/CallLog.cs` | detail 截断 300 → 1000；写库前再做一次 Redact |
| `src/LimitService.cs` | `Kinds` 加 analyze；`View.used` 加 analyze；`counted_endpoints` 加 `POST /api/analyze`（评审遗漏项 2） |
| `src/AdminApi.cs` | 调用记录 kind 白名单加 analyze（评审遗漏项 1） |
| `index.html` | 调用记录筛选加 `<option value="analyze">分析</option>`；`kindZh` 加 `analyze:"分析"` |
| `.env`（600，备份也是 600） | 追加 GEMINI_API_KEY（空）、GEMINI_API_URL（空=默认端点）、ANALYZE_DEFAULT_PROVIDER=agnes、ANALYZE_PROVIDER_ORDER=agnes,gemini、ANALYZE_TIMEOUT_S=180，注释全部单独成行（B8） |
| `.env.example` | 同上，另把 counted endpoints 注释补上 /api/analyze |
| `tests/MockAgnes/Program.cs` | 场景前缀（O9）与 `/gemini/chat/completions` 路由（另一把 dummy key，并要求 reasoning_effort ∈ low/minimal） |
| `README.md` | 端点表加一行，末尾加「日语解析 /api/analyze」一节 |

## 2. DDL 迁移（第 1 项）

只执行了 DO 块本身（`psql -1 -f .probe/task6_kind_migration.sql`），**没有**重跑整份 auth_schema.sql。连续执行两次都输出 `DO`（第二次因为定义里已有 `'analyze'`，什么也不做）。迁移后查 pg_constraint：

```
agnes_auth.usage_daily|usage_daily_kind_check|CHECK ((kind = ANY (ARRAY['text'::text, 'image'::text, 'video'::text, 'analyze'::text])))
agnes_auth.call_log|call_log_kind_check|CHECK ((kind = ANY (ARRAY['text'::text, 'image'::text, 'video'::text, 'analyze'::text])))
```

## 3. 构建与单测

- `dotnet build -c Release --no-incremental`：**0 Warning(s)，0 Error(s)**（最终版本）。MockAgnes 构建同样 0/0。
- 没有测试工程（csproj 把 tests/** 排除在编译之外），按 TASK6 §11 的备选做法，用隐藏自检入口 `AgnesService.dll --self-test` 代替，最终 **52/52**。覆盖范围：ruby（送假名 買っ/食べる/静かな/読ん/来た、纯假名、片假名、符号、拉丁字母、r 为空、无后缀、数字词 18/一本、数字+假名、整词注音 お茶、后缀吃掉整段读音时退回整词）、pos 映射、内容解析（代码块标记、前后夹杂说明文字、非法 JSON）、raw 校验（拼接位置、pos 集合、片假名 furigana 归一、非假名拒绝、整词读音覆盖）、enrich 校验（越界、s 不一致、seq 集合、tr 为空、拉丁 unk 剔除、缺读音告警、送假名覆盖）、投影、分块、候选顺序（O1）、就绪格式校验（B8）、脱敏键集合。全文见附录 A。

## 4. 实现要点与口径

### 4.1 Provider 注册表（第 3 项）

| | agnes | gemini |
|---|---|---|
| 端点 | `AGNES_BASE_URL + /v1/chat/completions` | `GEMINI_API_URL`，为空时用 `https://generativelanguage.googleapis.com/v1beta/openai/chat/completions` |
| 模型顺序 | agnes-3.0-flash（默认）→ agnes-2.5-flash | gemini-flash-latest（默认）→ gemini-flash-lite-latest |
| 策略 | `response_format:{type:"json_object"}` | json_object + `reasoning_effort`（latest=low，lite=minimal）；**不做** strict json_schema（O2） |
| ready | Key 非空、不以 `#` 开头、不含空白，且 URL 能用 `Uri.TryCreate` 解析成绝对 http(s) 地址（B8）；不发探测 | 同左 |

选择顺序：请求的 provider（或请求 model 所属的 provider）> `ANALYZE_DEFAULT_PROVIDER` > `ANALYZE_PROVIDER_ORDER`；同一 provider 内先换模型，再换 provider（O1）。请求指定的 model 排在它所属 provider 的最前面。未就绪的 provider 直接跳过；一个候选都没有时返回 503 `no_provider_available`。

### 4.2 失败分类（O5，按 L1 顺序）

| 上游结果 | 处理 | trail 记号 |
|---|---|---|
| 参数类 4xx（400/404/422…） | 不兜底，整单失败，502 `upstream_rejected`（带 upstream_status，不把 4xx 透传给调用方，见降级设计 F9） | `:400` |
| 401 | 跳过该 provider 剩下的所有模型（鉴权类，降级设计 E5） | `:401` |
| 403 / 429 | 换下一个模型 → 换 provider | `:403` `:429` |
| 5xx / 超时 / 网络错误 / 200 但非 JSON 或缺 choices | 同上 | `:500` `:timeout` `:net` `:shape` |
| content 不是合法 JSON / 校验不过 | 同一模型带失败清单修复重试 1 次（L1a）；修完还不过 → 下一个模型 | `:json` `:bad`，修复那次记 `r:` |
| `finish_reason=length` | enrich：判定为块太大，对半拆块（L1d），不算校验失败；raw：换下一个模型 | `:len` |

**上限（回报要求写明数值与理由）**：

| 项 | 值 | 理由 |
|---|---|---|
| 单次上游调用超时 | `ANALYZE_TIMEOUT_S`，默认 180 s（可取 5–900） | 沿用 TASK6 §3；同时会被整单剩余时间截短 |
| raw 整单上游调用 | ≤ 4 | TASK6 §3 原口径：首次 + 修复 + 换模型 + 修复，正好走完 agnes 的两个模型 |
| enrich 每块上游调用 | ≤ 3 | 评审 B6 / 降级设计 §3.1：首次 + 修复 + 换一次模型 |
| enrich 整单上游调用 | ≤ min(30, 2×初始块数 + 4) | 每块平均留 1 次修复余量，另留 4 次给兜底和拆块；30 是硬顶，约等于 10 块 × 3 次（200 句上限正好 10 块），防止长文把配额耗光 |
| 块并行度 | 2 | 降级设计 §3.1；只占 1 个准入票据 |
| 整单墙钟 | 600 s | 防止 30 次 × 180 s 的极端情况；不足 5 s 时停止发起新调用 |
| 分块 | 约 20 句或约 1500 字 | 评审 B6 |

### 4.3 /api/analyze 契约（第 4 项）

- **raw**：`{mode:"raw", text(1–2000 字，先 trim), provider?, model?}` → `{mode, provider, model, prompt_version:"analyze-v1", attempts, trail, toks:[{s,pos,r,ruby}], tokens:[{word,pos,furigana}]}`。模型只输出 analyzer 形状，服务端映射成 reading 形状：`r` = furigana；furigana 为空时，假名词取其平假名，记号取 `""`，其余为 null。`ruby` 按 §6 算法推导。说明：raw 用的是学校文法切分（`静かな` 是一个词），**和 fugashi 骨架的切分不等价，不能拿来当骨架用**（B3 补充）。
- **enrich**：`{mode:"enrich", sentences:[{seq,text,toks}] (1–200 句，合计 ≤5000 字), gloss?:bool}` → `{mode, provider, model, prompt_version:"enrich-v1", attempts, chunks:[{chunk,seq,provider,model,attempts,result,trail}], sentences:[{seq,tr,fixes:[{tok_i,s,reading}],gloss?}], warnings?}`。
  - 发给模型的是紧凑投影 `{seq,text,toks:[{i,s,r,unk?}]}`，去掉 sym/空白 token（下标 i 保持原值），不带 l/pd/conj/ruby/sub（B6）。
  - 模型返回 fixes 时带 `i` 和原 `s`，服务端核对 `s` 与该下标的词一致（O8），不一致按校验失败处理、触发修复。
  - 准入前检查：seq 为非负整数且不重复；toks 拼接必须等于 text（不等则 400，提示原样传 preview 骨架）；toks 可以为 null。
  - 某块最终失败 → 502 `analyze_validation_failed`（或 upstream_*），响应里附 `failed_seqs`、每块失败清单，以及 `partial`（已通过的块的结果，供第二期 L1e 部分提交使用）。
  - **gloss**：默认关闭，提示词不要求（B5）；请求带 `gloss:true` 才在提示词里加 gloss 规则；模型主动返回的 gloss 会透传，越界或非法的键丢弃，不判失败。
- **pos 映射**：实现前读了 `/data/workspace/tts-redesign/impl/service/tokenizer.py` 的 `POS_MAP` 及其后的 `num`/`other` 逻辑。TtsWeb 的枚举全集是 `noun pronoun verb adj adv conj particle aux affix sym num other`。实际采用的映射：

  | 学校文法 | 映射 | 与 TASK6 §4 原表的差异 |
  |---|---|---|
  | 名詞 / 動詞 / 形容詞 / 副詞 / 接続詞 / 助詞 / 助動詞 / 記号 | noun / verb / adj / adv / conj / particle / aux / sym | 无 |
  | 代名詞 | **pronoun** | 原表是 pron（B3） |
  | 形容動詞 | **adj** | 原表是 adjn；TtsWeb 把形状詞也记成 adj（B3） |
  | 連体詞 | **adv** | 原表是 adn；TtsWeb 的 POS_MAP 写的就是 連体詞→adv |
  | 感動詞 | **conj** | 原表是 intj；TtsWeb 的 POS_MAP 写的就是 感動詞→conj |
  | 接頭辞 / 接尾辞 | **affix** | 原表没有这两类（B3） |
  | 数詞 | **num** | 原表没有；TtsWeb 用 pos2=数詞 判 num；提示词要求模型单独标出数詞 |
  | 改行 | sym | 无 |

  另外 TtsWeb 对映射不到的词性给 `other`；本端用封闭集合校验，模型给出集合外的词性时触发修复，所以不会产生 other。`/health` 里没有列出映射表（端点是公开的），映射表就是 `Analyze.PosMap` 这个常量。
- **unk / 无读音 token**（评审遗漏项 6）：凡是不需要读音的词（不含汉字或数字，即拉丁字母 unk、假名、符号），即使模型给了 fixes 也剔除，并在 warnings 里记 `fixes_dropped`。**但**含数字、r 为 null 的 unk token（TtsWeb 合并出来的数字串）不剔除，反而要求模型补读音（偏差说明见 §6-2）。仍然缺读音的词记 warning `readings_missing`，不判失败。

### 4.4 管线接入（第 5 项，B9）

- `Generate(ctx, kind, prepare, preview?, outcome?)`：text/image/video 的调用方式不变（不传后两个参数，仍走原来「上游 4xx 即退额」的规则）。analyze 传入 `Analyze.Preview`（raw 取 text，enrich 取第一句；统一截 120 字）和 `CallOutcome`（实际的 `provider:model`、紧凑轨迹、是否退额）。
- **退额**：至少有过 1 次上游调用、且**全部**上游调用都是 4xx，才退额；兜底后成功不退；超时、5xx、校验失败都不退；0 次调用（no_provider_available）也不退，和原来「Key 缺失照样计数」的口径一致。
- kind=`analyze`：一次请求只准入 1 次（Mock 验收：18 次准入 + 2 次退额 → used.analyze=18；200 句、10 块的请求也只计 1 次）。计入每日总量、每分钟和并发；不占 video 子配额。`ReadUsage` 本来就按 DB 里出现的 kind 求和（评审 A-2），总量自动包含 analyze。
- call_log：model 记 `provider:model`；detail 用紧凑编码，例如 `m=raw;a=3;fb=agnes/3.0-flash:403>agnes/2.5-flash:403>gemini/flash-latest:ok;v=ok`、`m=enrich;n=45;c=3;a=3;c0[0-19]=agnes/3.0-flash:ok;…;v=ok`；退额的行以 `refund;` 开头；截断上限约 1000 字。

### 4.5 /health（第 6 项）

```json
"analyze": {"providers": {"agnes": {"ready": true, "models": ["agnes-3.0-flash","agnes-2.5-flash"], "strategy": "json_object"},
                          "gemini": {"ready": false, "models": ["gemini-flash-latest","gemini-flash-lite-latest"], "strategy": "json_object+reasoning_effort"}},
            "default_provider": "agnes", "order": ["agnes","gemini"], "prompt_versions": {"raw": "analyze-v1", "enrich": "enrich-v1"}}
```

上面是真实配置下的输出。只给 ready 和模型名，不给任何 URL（评审遗漏项 9）；验收脚本检查了 analyze 段里不含 "http"。

### 4.6 配置与热重读（第 8 项）

- `Settings.Get` 按键名读取，文件 mtime 变化就重读，所以新键自动支持热重读。实测：8795 运行中改 overlay 文件里的 `ANALYZE_DEFAULT_PROVIDER/ORDER`，`/health` 立即变成 `gemini ['gemini','agnes']`；改 `ANALYZE_TIMEOUT_S=5`，下一次请求就按 5 s 超时（§5 超时用例）。
- **生产上要注意**（评审 A-4）：systemd 的 `EnvironmentFile` 会在启动时把 .env 灌进进程环境，而环境变量优先于文件。启动时为空的键（`GEMINI_API_KEY=`、`GEMINI_API_URL=`）之后填入，无需重启即生效；启动时非空的键（`ANALYZE_DEFAULT_PROVIDER/ORDER/TIMEOUT_S`）改了要重启。

### 4.7 前端（第 7 项）

`diff index.html.bak-20261119-task6p1 index.html` 只有两行：

```
230: …<option value="video">视频</option></select>  →  …<option value="video">视频</option><option value="analyze">分析</option></select>
802: const kindZh = { text: "文本", image: "图片", video: "视频" };  →  const kindZh = { text: "文本", image: "图片", video: "视频", analyze: "分析" };
```

`node --check` 被本 session 的权限规则拦下，**没有执行**。这次 JS 的改动只是对象字面量里加了一个键，而且 8795 实际提供的页面已经由验收脚本确认含有这两处。建议发布时补跑一次：`python3` 抽出 `<script>` 后执行 `node --check`（命令见 §7）。没有加解析页签。

## 5. 验收证据（开发实例 127.0.0.1:8795，与生产同库、同 .env，另叠 overlay）

运行方式：`AgnesService.dll --env-file .env --env-file tests/port8795.env [--env-file tests/mock8795.env]`。MockAgnes 监听 127.0.0.1:18793。最终版本的完整运行日志见附录 B（mock，84/84）和附录 C（timeout，4/4）。下表是逐项对应：

| §11 / 任务项 | 用例（场景前缀） | 关键结果 |
|---|---|---|
| 既有回归 | text / image / video / video status / models / text {}→400 / reject→400 并退额 / boom→502 | 全部符合原有行为；used = text 2、image 1、video 1（reject 已退额） |
| smoke.sh 等价项 | /health、/、CSP、/nope 404；8 个端点无 token 一律 401；伪造 token 401 | 全部通过 |
| 1 raw 正常 | `scenario:json_ok 彼女は静かな図書館で本を読んでいる。` | 200，attempts=1；彼女→pronoun/かのじょ/整词 ruby；静かな→adj，ruby `[静/しず][かな]`；読ん→`[読/よ][ん]`；いる、。 的 ruby 为 null；analyzer 视图保留日文标签 |
| 2 修复重试 | json_bad_concat / json_invalid | attempts=2，trail `agnes/3.0-flash:bad>r:ok` / `:json>r:ok` |
| 2' 修复用尽 | bad_always | 502 `analyze_validation_failed`，attempts=4（上限），details 指出「第 25 个字符处」的差异 |
| 3 兜底 | 403m / 403 / 500 | 模型级先兜底：`agnes/3.0-flash:403>agnes/2.5-flash:ok`；agnes 两个模型都 403 → `gemini/flash-latest:ok`（gemini mock 校验另一把 key 和 reasoning_effort）；call_log 行 model=`gemini:gemini-flash-latest`，detail 有完整轨迹 |
| 3' 退额规则 | 403all / 400 / bad_always(gemini) / sleep:8 | 全 4xx → 502 upstream_failed，退额（detail 以 `refund;` 开头）；参数 400 → 不兜底，attempts=1，502 upstream_rejected，退额；校验失败 → 不退；超时 → 不退 |
| 3'' 超时兜底 | sleep:8，ANALYZE_TIMEOUT_S=5（热重读） | `agnes/3.0-flash:timeout>agnes/2.5-flash:timeout>gemini/flash-latest:ok`，耗时 10.0 s |
| 4 enrich 正常 | 3 句骨架（含 r=null 的「18」和拉丁词「quiet」） | 200；fixes `[{tok_i:0, s:"18", reading:"じゅうはち"}]`；quiet 被剔除；没有请求 gloss 时不返回 gloss |
| 4 越界拦截 | oob / bad_always | 先越界后修好：attempts=2；一直越界：502 `analyze_validation_failed`，失败清单写着「fixes 下标 i=999 越界（该句 toks 共 6 个）」，`failed_seqs` 为 [0,1,2] |
| 4 分块/拆块 | 45 句 / 200 句 / length | 45 句 → 3 块（0-19 / 20-39 / 40-44）；200 句 → 10 块，并行，attempts=10；length → c0 拆成 c0a+c0b，c0a 再拆成 c0aa+c0ab，共 5 次调用 |
| 4 enrich 兜底 | 403 | 块级换到 gemini |
| 5 401 | 无 token POST /api/analyze | 401 |
| 5 429 | 测试账号 daily_quota 改成当前用量 | 429 `daily_quota_exceeded`；call_log 有一行 kind=analyze，outcome=daily_quota_exceeded，detail=total |
| 5' 计数 | — | used.analyze = 18 = 准入次数 − 退额次数；total 含 analyze；/api/limits 的 counted_endpoints 含 `POST /api/analyze`；/me 能看到 analyze |
| 5'' 准入前 400 | 8 种参数错误 | 全部 400，用量不变 |
| 6 /health | mock 配置与真实配置各看一次 | ready、模型、default、order 与当时的配置一致；不含 URL |
| 管理端 | `/api/admin/calls?kind=analyze` / `kind=bogus` | 只返回 analyze 的行 / 400 |
| call_log 细节 | — | enrich 的 prompt_preview 是第一句；有 detail 长达 328 字（超过旧的 300 上限） |

**测试数据清理**：每次运行结束都删除测试账号（call_log、usage_daily 级联删除，login_audit 按用户名删除）。最后核对 `t6p1_%`：users 0、call_log 0、login_audit 0；全库 kind=analyze 的 call_log 0 行、usage_daily 0 行。8795 与 18793 端口都已关闭。真实冒烟读 reading 库只用了 SELECT。

## 6. 与任务书的偏差及理由

1. **没有发布和重启生产**：权限拦截（见 §0），停在闸门，交接命令见 §7。
2. **unk token 的排除口径**：任务书写「unk/无读音 token 排除在 fixes 之外」。本实现只排除**不需要读音**的 token（不含汉字/数字：拉丁字母 unk、假名、符号），判定规则与 TtsWeb 的 `NEEDS_READING_RE` 相同。TtsWeb 合并出来的数字串是 `pos=num, r=null, unk=true`，而 commit 要求「含汉字或数字的 token 读音齐全」（降级设计 F7）。如果把所有 unk 一律排除，数字永远补不上读音，第二期每篇都会停在 partial。评审遗漏项 6 的原意针对的是外文词（`quiet`），这一点做到了。
3. **fixes 的形状**：响应用 `{tok_i, s, reading}`，即 TASK6 的键名再加上 O8 要求的 `s`；模型侧用 `{i,s,r}`。第二期提交时要映射成 TtsWeb commit 的 `readings:[{tok,s,r}]`（键名已对照 ingest.py 核过）。
4. **新增「读音覆盖整词」校验，并微调两份提示词**（真实冒烟中发现）：第 1 轮 agnes-3.0-flash 把 `静かな` 的 furigana 给成 `しずか`，旧校验放行了，ruby 就退化成整词注音。现在规定：含汉字/数字的词，读音必须以它的词尾假名结尾，且比词尾长；raw 的 furigana 和 enrich 的 fixes 都做这项检查，不过就修复重试。第 2 轮真实调用中，这项检查实际拦下了一次，修复后正确（`agnes/3.0-flash:bad>r:ok`）。提示词另外补了两条：补助动词（いる等）的词性是動詞；数字和量词分成两个词时，音变分别写进各自的读音（一→いっ、本→ぽん）。第 3 轮验证已经生效。因为还没上线，提示词版本仍是 analyze-v1 / enrich-v1。
5. **请求里指定 provider/model 不是硬绑定**：指定的那个排在最前面，失败后仍按 O1 顺序兜底，响应如实返回实际用的 provider/model。
6. **analyze 不透传上游 4xx**：统一包成 502 `upstream_rejected`（带 upstream_status）。理由是避免 Agnes 的 401/403 被调用方误认为自己的 JWT 失效（降级设计 F9）。text/image/video 的透传行为没有改。
7. **401 单独成类**：跳过该 provider 的其余模型（任务书的分类里没写 401；依据是降级设计 E5 的鉴权类）。
8. **片假名 furigana 自动转成平假名**，不当作失败（宽松处理，少一次修复调用）；其他非假名一律判失败。
9. 熔断器（降级设计 A4/§2.2）、INGEST_DEADLINE_S、作业表都属于第二期，本期没有做；本期只有整单 600 s 墙钟这一个常量。

## 7. 发布交接（需要有 sudo 的人执行，约 1 分钟）

```bash
cd /data/workspace/agnes-service
# 0) JS 语法补检（本 session 被拦下的那一项）
python3 -c "import re;s=open('index.html',encoding='utf-8').read();open('.probe/index_inline.js','w',encoding='utf-8').write('\n;\n'.join(re.findall(r'<script[^>]*>(.*?)</script>',s,re.S)))" && node --check .probe/index_inline.js
# 1) 确认没有在途的长请求（评审遗漏项 4：TimeoutStopSec=20）
ss -tn state established '( sport = :8793 )'
psql -d db77database -Atc "select kind, outcome, created_at from agnes_auth.call_log where created_at > now() - interval '10 minutes' order by id desc"
# 2) 回滚备份 → 先停服务再覆盖 publish/（不要在服务运行时覆盖程序集）→ 启动
tar -cf publish.bak-20261119-task6p1.tar publish
sudo systemctl stop agnes-service
/data/dotnet-sdk/dotnet publish -c Release -o publish
sudo systemctl start agnes-service
# 3) 生产冒烟：/health 含 analyze 段、全部 401、前端「分析」筛选、真实 raw 1 次、管理页 kind=analyze 筛选；用一次性账号，结束自动删除
python3 tests/task6_accept.py prod --base http://127.0.0.1:8793
curl -s https://8793-agq6dov7ieyr5kl67vfd2cqvce.instabox.tech/health | python3 -m json.tool | grep -A3 '"analyze"'
# 回滚：sudo systemctl stop agnes-service && rm -rf publish && tar -xf publish.bak-20261119-task6p1.tar && sudo systemctl start agnes-service
#      （DB 约束只扩了取值，回滚代码不需要回滚 DDL）
```

## 8. 未拍板项采用的推荐口径

| 项 | 本期采用 |
|---|---|
| D6 gloss | 默认关；`gloss:true` 才请求；模型主动给的透传，非法键丢弃 |
| D7 / O3 raw 范围 | 不精简：按任务书保留双形状 + ruby 推导 |
| D5 计费 | 一次请求 1 次准入，不按块计 |
| O2 Gemini | 只做 json_object + reasoning_effort；strict json_schema 等拿到真 Key 实测后再做 |
| O6 同文串行锁、熔断器 | 第二期 |
| 失败请求 call_log 的 model 列 | 有成功块时取句数最多的 `provider:model`，否则取第一个候选 |
| 退额边界 | 0 次上游调用（无可用 provider）不退额；超时/5xx/校验失败不退；全部 4xx 才退 |
| 超时参数 | `ANALYZE_TIMEOUT_S` 可取 5–900，默认 180 |

## 9. 真实 Agnes 冒烟（8795，真实 .env，gemini 未就绪）

共 3 轮，7 次 analyze 准入，每轮都用新建后删除的一次性账号：

- 第 1 轮（读音覆盖校验加入前）：raw×2 + enrich×1（附录 D 全文）。
- 第 2 轮（加入校验后，按闸门要求重跑）：raw×2 + enrich×1（附录 E 全文）。
- 第 3 轮（量词音变提示词修改后，只验证这一句）：raw×1（附录 F 全文）。

**enrich 骨架来源**：`reading.sentences` 中 `ce605b3d6e`（「静かな退職（quiet quitting）」の記事，status=ready）当前版本的 seq 0–2，toks 原样取出（只做了 SELECT）。选它的原因：P2a 冒烟文章已被软删；这一篇同时含拉丁 unk 词（quiet）、形状詞（静か）和数字（2022），覆盖面最好。

时延：raw 1.0–5.1 s（含修复的那次 2.3 s），enrich（3 句一块）约 1.3 s。

结果摘要：

| 轮次 | 句子 | 结果 |
|---|---|---|
| 1 | 彼女は静かな図書館で本を読んでいる。 | 静かな 的 furigana 只给了 しずか（旧校验放过，ruby 退化为整词）；いる 标成 助動詞 |
| 1 | 一本のペンと十八年の手帳。 | 一/いち、本/ほん：量词音变没有体现 |
| 1 | enrich | fixes 给出 2022→にせんじゅうに；quiet、quitting 的 2 条 fixes 被剔除（fixes_dropped=2）；译文自然 |
| 2 | 彼女は静かな図書館で本を読んでいる。 | 新校验拦下一次后修复：静かな/しずかな，ruby `[静/しず][かな]`；いる 标为 動詞 |
| 2 | 一本のペンと十八年の手帳。 | 手帳 给成 ちょう，**读错了**，而且校验抓不到（没有送假名可比） |
| 2 | enrich | 3 句译文都合格；fixes 为空 |
| 3 | 一本のペンと十八年の手帳。 | 一/いっ、本/ぽん、十八/じゅうはち、手帳/てちょう，全部正确 |

**默认模型是否合适（一句话结论）**：agnes-3.0-flash 速度快（1–5 s）、JSON 格式稳定，作为默认模型**可以用**；但读音准确度不够稳定（同一句两次运行，手帳 一次对一次错），raw 的读音应视为「需要校对」的草稿。建议第二期上线前用固定样本集把它和 agnes-2.5-flash 各跑一轮，再决定默认模型。


## 附录 A：--self-test（最终版本）

```text
ok   ruby 送假名 買っ/かっ  -> [{"t":"買","r":"か"},{"t":"っ"}]
ok   ruby 送假名 食べる/たべる  -> [{"t":"食","r":"た"},{"t":"べる"}]
ok   ruby 送假名 静かな/しずかな  -> [{"t":"静","r":"しず"},{"t":"かな"}]
ok   ruby 送假名 読ん/よん  -> [{"t":"読","r":"よ"},{"t":"ん"}]
ok   ruby 纯平假名 これ → null
ok   ruby 纯片假名 テスト → null
ok   ruby 符号 。 → null
ok   ruby 拉丁 quiet → null
ok   ruby r 空 → null
ok   ruby 无后缀 図書館
ok   ruby 数字词 18/じゅうはち
ok   ruby 数字词 一本/いっぽん
ok   ruby 数字+假名尾 3つ/みっつ  -> [{"t":"3","r":"みっ"},{"t":"つ"}]
ok   ruby 整词注音 お茶/おちゃ
ok   ruby 送假名 来た/きた  -> [{"t":"来","r":"き"},{"t":"た"}]
ok   ruby 词中假名 値上げ/ねあげ（只剥尾部送假名）  -> [{"t":"値上","r":"ねあ"},{"t":"げ"}]
ok   ruby 后缀吃尽读音 → 整词  -> [{"t":"々か","r":"か"}]
ok   pos 代名詞→pronoun
ok   pos 形容動詞→adj
ok   pos 接頭辞/接尾辞→affix
ok   pos 数詞→num
ok   pos 映射值全部在 TtsWeb 枚举内
ok   parse ```json fence
ok   parse chatter around JSON
ok   parse invalid → null
ok   raw valid
ok   raw concat mismatch reported with position  -> word 拼接与原文不一致：第 4 个字符处原文为「を読む。」，拼接结果为「読む。」
ok   raw pos outside closed set
ok   raw katakana furigana normalised
ok   raw furigana must cover okurigana (静かな/しずか rejected)
ok   raw 静かな/しずかな accepted
ok   coverage: 三ヶ月/さんかげつ ok (ヶ not okurigana)
ok   coverage: 行った/いった ok, 行った/い rejected
ok   raw non-kana furigana rejected
ok   enrich valid; Latin unk fix dropped (gap 6)  -> 
ok   enrich out-of-range i rejected
ok   enrich s mismatch rejected (O8)
ok   enrich fix must cover okurigana (読ん/よ rejected)
ok   enrich seq set mismatch rejected
ok   enrich empty tr rejected
ok   enrich missing reading reported as warning
ok   projection drops sym, keeps i/unk  -> {"sentences":[{"seq":7,"text":"1本のquiet。","toks":[{"i":0,"s":"1","r":null,"unk":true},{"i":1,"s":"本","r":"ほん"},{"i":2,"s":"の","r":"の"},{"i":3,"s":"quiet","r":null,"unk":true}]}]}
ok   chunks by 20 sentences
ok   chunks by ~1500 chars
ok   cands default
ok   cands requested provider first
ok   cands requested model first
ok   cands skip not-ready
ok   order parse ignores junk
ok   ready: key '#…' rejected
ok   ready: url must be absolute http(s)
ok   redact key set
self-test: 52/52 passed
```

## 附录 B：Mock 验收全文（最终版本，84/84）

```text
  ok    GET /health 200 status=ok  -> 200
  ok    GET / html + CSP
  ok    frontend has analyze filter + kindZh
  ok    GET /nope 404
  ok    POST /api/text without token -> 401  -> 401
  ok    POST /api/image without token -> 401  -> 401
  ok    POST /api/video without token -> 401  -> 401
  ok    POST /api/analyze without token -> 401  -> 401
  ok    GET /api/models without token -> 401  -> 401
  ok    GET /api/limits without token -> 401  -> 401
  ok    GET /api/auth/me without token -> 401  -> 401
  ok    GET /api/admin/calls?kind=analyze without token -> 401  -> 401
  ok    garbage token -> 401
  (created test account created user t6p1_mock_f24183 (id 27, role admin; limits: per_minute=500 daily=500 video_daily=default concurrent=default))
== /health analyze section (mock config: both providers ready)
  ok    health 200 + analyze section  -> {"providers": {"agnes": {"ready": true, "models": ["agnes-3.0-flash", "agnes-2.5-flash"], "strategy": "json_object"}, "gemini": {"ready": true, "models": ["gemini-flash-latest", "gemini-flash-lite-latest"], "strategy": "json_object+reasoning_effort"}}, "default_provider": "agnes", "order": ["agnes", "gemini"], "prompt_versions": {"raw": "analyze-v1", "enrich": "enrich-v1"}}
  ok    agnes ready + models
  ok    gemini ready + models
  ok    default/order from config
  ok    no URL in analyze section
== regression: text / image / video (mock upstream)
  ok    text 200 mock reply  -> 200 {'text': 'mock reply', 'model': 'agnes-2.5-flash', 'reasoning': 'mock reasoning', 'usage': {'prompt_tokens': 3, 'completion_tokens': 2, 'total_tokens': 5}, 'finish_reason': 'stop'}
  ok    text {} -> 400  -> 400 {'error': "'prompt' is required"}
  ok    text upstream 400 passthrough  -> 400 {'error': 'Agnes API returned HTTP 400', 'upstream_status': 400, 'upstream_body': '{"error":{"message":"mock: rejected prompt"}}'}
  ok    text upstream 500 -> 502  -> 502 {'error': 'Agnes API returned HTTP 500', 'upstream_status': 500, 'upstream_body': '{"error":{"message":"mock: upstream failure"}}'}
  ok    image 200 url  -> 200 {'model': 'agnes-image-2.5-flash', 'size': '1K', 'url': 'https://example.invalid/mock.png'}
  ok    video 200 id+poll  -> 200 {'id': 'video_mock1', 'task_id': 'task_mock1', 'model': 'agnes-video-2.5-flash', 'status': 'queued', 'progress': 0, 'url': None, 'error': None, 'seconds': '5', 'size': '720P', 'poll': '/api/video/status/video_mock1?model=agnes-video-2.5-flash'}
  ok    video status completed  -> 200 {'id': 'video_mock1', 'task_id': None, 'model': 'agnes-video-2.5-flash', 'status': 'completed', 'progress': 100, 'url': 'https://example.invalid/mock.mp4', 'error': None, 'seconds': '5', 'size': '720P'}
  ok    models 200  -> 200
  ok    usage: text 2 (reject refunded), image 1, video 1  -> {"text": 2, "image": 1, "video": 1, "analyze": 0, "total": 4}
== /api/analyze: auth + pre-admission validation (400 costs nothing)
  ok    no token -> 401  -> 401
  ok    400 invalid_mode  -> 400 {'error': "'mode' is required: raw or enrich", 'code': 'invalid_mode'}
  ok    400 invalid_text  -> 400 {'error': "'text' is required for mode=raw", 'code': 'invalid_text'}
  ok    400 invalid_model  -> 400 {'error': "'model' must be one of agnes-3.0-flash, agnes-2.5-flash, gemini-flash-latest, gemini-flash-lite-latest", 'code': 'invalid_model'}
  ok    400 invalid_provider  -> 400 {'error': "'provider' must be one of agnes, gemini", 'code': 'invalid_provider'}
  ok    400 invalid_model  -> 400 {'error': "'model' must be one of gemini-flash-latest, gemini-flash-lite-latest", 'code': 'invalid_model'}
  ok    400 invalid_sentences  -> 400 {'error': "'sentences' (non-empty array) is required for mode=enrich", 'code': 'invalid_sentences'}
  ok    400 invalid_sentences  -> 400 {'error': 'sentences[0]: toks do not concatenate to text (pass the preview skeleton unchanged)', 'code': 'invalid_sentences'}
  ok    400 invalid_text  -> 400 {'error': "'text' is too long (max 2000 chars; split long texts yourself)", 'code': 'invalid_text'}
  ok    400s not counted
== §11-1 raw normal: both shapes, pos map, ruby
        {"mode": "raw", "provider": "agnes", "model": "agnes-3.0-flash", "prompt_version": "analyze-v1", "attempts": 1, "trail": ["agnes/3.0-flash:ok"], "toks": [{"s": "scenario:json_ok ", "pos": "sym", "r": "", "ruby": null}, {"s": "彼女", "pos": "pronoun", "r": "かのじょ", "ruby": [{"t": "彼女", "r": "かのじょ"}]}, {"s": "は", "pos": "particle", "r": "は", "ruby": null}, {"s": "静かな", "pos": "adj", "r": "しずかな", "ruby": [{"t": "静", "r": "しず"}, {"t": "かな"}]}, {"s": "図書館", "pos": "noun", "r": "としょかん", "ruby": [{"t": "図書館", "r": "としょかん"}]}, {"s": "で", "pos": "particle", "r": "で", "ruby": null}, {"s": "本", "pos": "noun", "r": "ほん", "ruby": [{"t": "本", "r": "ほん"}]}, {"s": "を", "pos": "particle", "r": "を", "ruby": null}, {"s": "読ん", "pos": "verb", "r": "よん", "ruby": [{"t": "読", "r": "よ"}, {"t": "ん"}]}, {"s": "で", "pos": "particle", "r": "で", "ruby": null}, {"s": "いる", "pos": "verb", "r": "いる", "ruby": null}, {"s": "。", "pos": "sym", "r": "", "ruby": null}], "tokens": [{"word": "scenario:json_ok ", "pos": "記号", "furigana": ""}, {"word": "彼女", "pos": "代名詞", "furigana": "かのじょ"}, {"word": "は", "pos": "助詞", "furigana": "は"}, {"word": "静かな", "pos": "形容動詞", "furigana": "しずかな"}, {"word": "図書館", "pos": "名詞", "furigana": "としょかん"}, {"word": "で", "pos": "助詞", "furigana": "で"}, {"word": "本", "pos": "名詞", "furigana": "ほん"}, {"word": "を", "pos": "助詞", "furigana": "を"}, {"word": "読ん", "pos": "動詞", "furigana": "よん"}, {"word": "で", "pos": "助詞", "furigana": "で"}, {"word": "いる", "pos": "動詞", "furigana": "いる"}, {"word": "。"
  ok    raw 200 agnes/agnes-3.0-flash attempts=1  -> 200
  ok    toks/tokens same length
  ok    彼女 pos=pronoun r=かのじょ ruby whole  -> {"s": "彼女", "pos": "pronoun", "r": "かのじょ", "ruby": [{"t": "彼女", "r": "かのじょ"}]}
  ok    静かな pos=adj ruby 静/しず + かな  -> {"s": "静かな", "pos": "adj", "r": "しずかな", "ruby": [{"t": "静", "r": "しず"}, {"t": "かな"}]}
  ok    読ん verb ruby 読/よ + ん  -> {"s": "読ん", "pos": "verb", "r": "よん", "ruby": [{"t": "読", "r": "よ"}, {"t": "ん"}]}
  ok    いる kana: ruby null
  ok    。 sym r='' ruby null
  ok    analyzer view keeps JP labels  -> {"word": "彼女", "pos": "代名詞", "furigana": "かのじょ"}
== §11-2 repair retry
  ok    concat error -> repaired, attempts=2  -> 200 ['agnes/3.0-flash:bad', 'r:ok'] 2
  ok    invalid JSON -> repaired, attempts=2  -> 200 ['agnes/3.0-flash:json', 'r:ok']
  ok    never valid -> 502 analyze_validation_failed, 4-call cap  -> 502 {"error": "model output failed validation after repair and fallback", "code": "analyze_validation_failed", "attempts": 4, "trail": ["agnes/3.0-flash:bad", "r:bad", "agnes/2.5-flash:bad", "r:bad", "cap"], "details": ["word 拼接与原文不一致：第 25 个字符处原文为「。」，拼接结果为「」"]}
  ok    502 details name the first difference  -> ["word 拼接与原文不一致：第 25 个字符处原文为「。」，拼接结果为「」"]
== §11-3 fallback: model level first (O1), then provider; call_log trail
  ok    403 on agnes-3.0-flash -> agnes-2.5-flash  -> 200 ['agnes/3.0-flash:403', 'agnes/2.5-flash:ok']
  ok    agnes 403 x2 -> gemini(mock) ok, attempts=3  -> 200 ['agnes/3.0-flash:403', 'agnes/2.5-flash:403', 'gemini/flash-latest:ok']
  ok    agnes 500 x2 -> gemini  -> 200 ['agnes/3.0-flash:500', 'agnes/2.5-flash:500', 'gemini/flash-latest:ok']
  ok    requested model gemini-flash-lite-latest (reasoning_effort=minimal accepted by mock)  -> 200 ['gemini/flash-lite-latest:ok']
  ok    requested provider gemini -> gemini-flash-latest  -> 200
  ok    all providers 403 -> 502 upstream_failed, 4-call cap  -> 502 {"error": "all analyze providers failed", "code": "upstream_failed", "attempts": 4, "trail": ["agnes/3.0-flash:403", "agnes/2.5-flash:403", "gemini/flash-latest:403", "gemini/flash-lite-latest:403"], "details": ["gemini:gemini-flash-lite-latest: Gemini API returned HTTP 403"]}
  ok    all-4xx -> refunded  -> 9 -> 9
  ok    param 400 -> no fallback, 502 upstream_rejected, attempts=1  -> 502 {"error": "analyze upstream rejected the request", "code": "upstream_rejected", "upstream_status": 400, "upstream_body": "{\"error\":{\"message\":\"mock: invalid parameter\"}}", "attempts": 1, "trail": ["agnes/3.0-flash:400"], "details": ["Agnes API returned HTTP 400"], "provider_error": "Agnes API returned HTTP 400"}
  ok    param 400 -> refunded
  ok    validation failure (200s upstream) -> not refunded
== §11-4 enrich normal, out-of-range interception, chunking, split
        {"mode": "enrich", "provider": "agnes", "model": "agnes-3.0-flash", "prompt_version": "enrich-v1", "attempts": 1, "chunks": [{"chunk": "c0", "seq": "0-2", "provider": "agnes", "model": "agnes-3.0-flash", "attempts": 1, "result": "ok", "trail": ["agnes/3.0-flash:ok"]}], "sentences": [{"seq": 0, "tr": "[agnes:agnes-3.0-flash] 译文：scenario:json_ok 一本のペン。", "fixes": []}, {"seq": 1, "tr": "[agnes:agnes-3.0-flash] 译文：18年の手帳。", "fixes": [{"tok_i": 0, "s": "18", "reading": "じゅうはち"}]}, {"seq": 2, "tr": "[agnes:agnes-3.0-flash] 译文：quietな夜。", "fixes": []}]}
  ok    enrich 200, seq set, tr non-empty  -> 200
  ok    fix for r=null digit token carries tok_i + s  -> [{"tok_i": 0, "s": "18", "reading": "じゅうはち"}]
  ok    Latin unk token excluded from fixes  -> {"seq": 2, "tr": "[agnes:agnes-3.0-flash] 译文：quietな夜。", "fixes": []}
  ok    no gloss unless asked
  ok    out-of-range i caught, repaired (attempts=2)  -> 200 [{'chunk': 'c0', 'seq': '0-2', 'provider': 'agnes', 'model': 'agnes-3.0-flash', 'attempts': 2, 'result': 'ok', 'trail': ['agnes/3.0-flash:bad', 'r:ok']}]
  ok    always out-of-range -> 502 analyze_validation_failed (chunk cap 3)  -> 502 [{"chunk": "c0", "seq": "0-2", "failures": ["seq 0 的 fixes 下标 i=999 越界（该句 toks 共 6 个）", "seq 1 的 fixes 下标 i=999 越界（该句 toks 共 5 个）", "seq 2 的 fixes 下标 i=999 越界（该句 toks 共 4 个）"]}]
  ok    failed_seqs lists the chunk  -> [0, 1, 2]
  ok    45 sentences -> 3 chunks (20/20/5), one admission  -> 200 ['0-19', '20-39', '40-44'] 3ms
  ok    200 sentences (max) -> 10 chunks, parallel, one admission  -> 200 8ms
  ok    finish_reason=length -> split in halves until it fits  -> 200 [('c0', '0-3', 'split'), ('c0a', '0-1', 'split'), ('c0aa', '0', 'ok'), ('c0ab', '1', 'ok'), ('c0b', '2-3', 'ok')]
  ok    enrich chunk fallback agnes 403 -> gemini  -> 200 [{'chunk': 'c0', 'seq': '0-2', 'provider': 'gemini', 'model': 'gemini-flash-latest', 'attempts': 3, 'result': 'ok', 'trail': ['agnes/3.0-flash:403', 'agnes/2.5-flash:403', 'gemini/flash-latest:ok']}]
  ok    gloss:true accepted (mock gives none -> passthrough nothing)
== usage accounting
  ok    used.analyze == admitted - refunded (18)  -> {"text": 2, "image": 1, "video": 1, "analyze": 18, "total": 22}
  ok    total includes analyze
  ok    counted_endpoints has POST /api/analyze
  ok    /me usage shows analyze
== §11-5 quota 429 recorded with kind=analyze
  ok    over daily quota -> 429 daily_quota_exceeded  -> 429 {'error': 'daily_quota_exceeded', 'code': 'daily_quota_exceeded', 'message': 'Daily generation quota exhausted (22 per day). Resets at 2026-11-20T00:00:00+09:00 (00:00 Asia/Tokyo).', 'scope': 'total', 'limit': 22, 'used': 22, 'remaining': 0, 'reset_at': '2026-11-20T00:00:00+09:00', 'reset_at_unix': 1795100400, 'retry_after': None, 'reset_in_seconds': 57607, 'timezone': 'Asia/Tokyo'}
== call_log (admin view) kind=analyze
  ok    admin calls?kind=analyze 200, only analyze rows  -> 200 n=21
  ok    admin kind whitelist rejects bogus
        #123 success                    200 us=None agnes:agnes-3.0-flash | 'scenario:json_ok 彼女は静かな図書館で本を読んでいる。' | m=raw;a=1;fb=agnes/3.0-flash:ok;v=ok
        #124 success                    200 us=None agnes:agnes-3.0-flash | 'scenario:json_bad_concat 東京へ行く。' | m=raw;a=2;fb=agnes/3.0-flash:bad>r:ok;v=ok
        #125 success                    200 us=None agnes:agnes-3.0-flash | 'scenario:json_invalid 東京へ行く。' | m=raw;a=2;fb=agnes/3.0-flash:json>r:ok;v=ok
        #126 analyze_validation_failed  502 us=None agnes:agnes-3.0-flash | 'scenario:bad_always 東京へ行く。' | m=raw;a=4;fb=agnes/3.0-flash:bad>r:bad>agnes/2.5-flash:bad>r:bad>cap;v=fail:word 拼接与原文不一致：第 25 个字符处原文为「。」，拼接结果为「」
        #127 success                    200 us=None agnes:agnes-2.5-flash | 'scenario:403m 東京へ行く。' | m=raw;a=2;fb=agnes/3.0-flash:403>agnes/2.5-flash:ok;v=ok
        #128 success                    200 us=None gemini:gemini-flash-latest | 'scenario:403 東京へ行く。' | m=raw;a=3;fb=agnes/3.0-flash:403>agnes/2.5-flash:403>gemini/flash-latest:ok;v=ok
        #129 success                    200 us=None gemini:gemini-flash-latest | 'scenario:500 東京へ行く。' | m=raw;a=3;fb=agnes/3.0-flash:500>agnes/2.5-flash:500>gemini/flash-latest:ok;v=ok
        #130 success                    200 us=None gemini:gemini-flash-lite-latest | 'scenario:json_ok 東京へ行く。' | m=raw;a=1;fb=gemini/flash-lite-latest:ok;v=ok
        #131 success                    200 us=None gemini:gemini-flash-latest | 'scenario:json_ok 東京へ行く。' | m=raw;a=1;fb=gemini/flash-latest:ok;v=ok
        #132 upstream_failed            502 us=None agnes:agnes-3.0-flash | 'scenario:403all 東京へ行く。' | refund;m=raw;a=4;fb=agnes/3.0-flash:403>agnes/2.5-flash:403>gemini/flash-latest:403>gemini/flash-lite-latest:403;v=fail:gemini:gemini-flash-lite-latest: Gemini API returned HTTP 403
        #133 upstream_rejected          502 us=400 agnes:agnes-3.0-flash | 'scenario:400 東京へ行く。' | refund;m=raw;a=1;fb=agnes/3.0-flash:400;v=fail:Agnes API returned HTTP 400
        #134 analyze_validation_failed  502 us=None gemini:gemini-flash-latest | 'scenario:bad_always 東京。' | m=raw;a=4;fb=gemini/flash-latest:bad>r:bad>gemini/flash-lite-latest:bad>r:bad>cap;v=fail:word 拼接与原文不一致：第 22 个字符处原文为「。」，拼接结果为「」
        #135 success                    200 us=None agnes:agnes-3.0-flash | 'scenario:json_ok 一本のペン。' | m=enrich;n=3;c=1;a=1;c0[0-2]=agnes/3.0-flash:ok;v=ok
        #136 success                    200 us=None agnes:agnes-3.0-flash | 'scenario:oob 一本のペン。' | m=enrich;n=3;c=1;a=2;c0[0-2]=agnes/3.0-flash:bad>r:ok;v=ok
        #137 analyze_validation_failed  502 us=None agnes:agnes-3.0-flash | 'scenario:bad_always 一本のペン。' | m=enrich;n=3;c=1;a=3;c0[0-2]=agnes/3.0-flash:bad>r:bad>agnes/2.5-flash:bad>cap;v=fail:seq 0 的 fixes 下标 i=999 越界（该句 toks 共 6 个）
        #138 success                    200 us=None agnes:agnes-3.0-flash | 'scenario:json_ok 今日は晴れ。' | m=enrich;n=45;c=3;a=3;c0[0-19]=agnes/3.0-flash:ok;c1[20-39]=agnes/3.0-flash:ok;c2[40-44]=agnes/3.0-flash:ok;v=ok
        #139 success                    200 us=None agnes:agnes-3.0-flash | 'scenario:json_ok 雪。' | m=enrich;n=200;c=10;a=10;c0[0-19]=agnes/3.0-flash:ok;c1[20-39]=agnes/3.0-flash:ok;c2[40-59]=agnes/3.0-flash:ok;c3[60-79]=agnes/3.0-flash:ok;c4[80-99]=agnes/3.0-flash:ok;c5[100-119]=agnes/3.0-flash:ok;c6[120-139]=agnes/3.0-flash:ok;c7[140-159]=agnes/3.0-flash:ok;c8[160-179]=agnes/3.0-flash:ok;c9[180-199]=agnes/3.0-flash:ok;v=ok
        #140 success                    200 us=None agnes:agnes-3.0-flash | 'scenario:length 雨。' | m=enrich;n=4;c=1;a=5;c0[0-3]=agnes/3.0-flash:len;c0a[0-1]=agnes/3.0-flash:len;c0aa[0]=agnes/3.0-flash:ok;c0ab[1]=agnes/3.0-flash:ok;c0b[2-3]=agnes/3.0-flash:ok;v=ok
        #141 success                    200 us=None gemini:gemini-flash-latest | 'scenario:403 一本のペン。' | m=enrich;n=3;c=1;a=3;c0[0-2]=agnes/3.0-flash:403>agnes/2.5-flash:403>gemini/flash-latest:ok;v=ok
        #142 success                    200 us=None agnes:agnes-3.0-flash | 'scenario:json_ok 一本のペン。' | m=enrich;n=3;c=1;a=1;c0[0-2]=agnes/3.0-flash:ok;v=ok
        #143 daily_quota_exceeded       429 us=None None | 'scenario:json_ok 東京。' | total
  ok    429 row present (kind=analyze, scope total)  -> [{"id": 143, "created_at": "2026-11-18T22:59:53Z", "user_id": 27, "username": "t6p1_mock_f24183", "kind": "analyze", "model": null, "outcome": "daily_quota_exceeded", "http_status": 429, "upstream_status": null, "duration_ms": 4, "prompt_preview": "scenario:json_ok 東京。", "detail": "total"}]
  ok    fallback row: model=gemini:gemini-flash-latest, trail in detail  -> [{"id": 128, "created_at": "2026-11-18T22:59:53Z", "user_id": 27, "username": "t6p1_mock_f24183", "kind": "analyze", "model": "gemini:gemini-flash-latest", "outcome": "success", "http_status": 200, "upstream_status": null, "duration_ms": 1, "prompt_preview": "scenario:403 東京へ行く。", "detail": "m=raw;a=3;fb=agnes/3.0-flash:403>agnes/2.5-flash:403>gemini/flash-latest:ok;v=ok"}]
  ok    all-4xx row marked refund  -> [{"id": 132, "created_at": "2026-11-18T22:59:53Z", "user_id": 27, "username": "t6p1_mock_f24183", "kind": "analyze", "model": "agnes:agnes-3.0-flash", "outcome": "upstream_failed", "http_status": 502, "upstream_status": null, "duration_ms": 1, "prompt_preview": "scenario:403all 東京へ行く。", "detail": "refund;m=raw;a=4;fb=agnes/3.0-flash:403>agnes/2.5-flash:403>gemini/flash-latest:403>gemini/flash-lite-latest:403;v=fail:gemini:gemini-flash-lite-latest: Gemini API returned HTTP 403"}]
  ok    enrich preview = first sentence; detail lists chunks  -> [{"id": 138, "created_at": "2026-11-18T22:59:53Z", "user_id": 27, "username": "t6p1_mock_f24183", "kind": "analyze", "model": "agnes:agnes-3.0-flash", "outcome": "success", "http_status": 200, "upstream_status": null, "duration_ms": 2, "prompt_preview": "scenario:json_ok 今日は晴れ。", "detail": "m=enrich;n=45;c=3;a=3;c0[0-19]=agnes/3.0-flash:ok;c1[20-39]=agnes/3.0-flash:ok;c2[40-44]=agnes/3.0-flash:ok;
  ok    detail can exceed 300 chars (cap 1000)  -> 328
  (cleanup: deleted user id 27, 1 login_audit rows; rows left for t6p1_mock_f24183: 0)
  ok    test account cleaned up
== 84 passed, 0 failed
```

## 附录 C：超时兜底（4/4）

```text
  (created test account created user t6p1_timeout_e2089b (id 24, role user; limits: per_minute=30 daily=20 video_daily=default concurrent=default))
  ok    agnes timeouts -> gemini  -> 200 ['agnes/3.0-flash:timeout', 'agnes/2.5-flash:timeout', 'gemini/flash-latest:ok'] 10013ms
  ok    per-call timeout 5 s honoured (2 x ~5 s)  -> 10013ms
  ok    timeouts are not refunded
  (cleanup: deleted user id 24, 1 login_audit rows; rows left for t6p1_timeout_e2089b: 0)
  ok    test account cleaned up
== 4 passed, 0 failed
```

## 附录 D：真实冒烟第 1 轮全文

```text
  (created test account created user t6p1_real_adb3cb (id 22, role user; limits: per_minute=30 daily=20 video_daily=default concurrent=default))
== /health analyze section (real config)
        {"providers": {"agnes": {"ready": true, "models": ["agnes-3.0-flash", "agnes-2.5-flash"], "strategy": "json_object"}, "gemini": {"ready": false, "models": ["gemini-flash-latest", "gemini-flash-lite-latest"], "strategy": "json_object+reasoning_effort"}}, "default_provider": "agnes", "order": ["agnes", "gemini"], "prompt_versions": {"raw": "analyze-v1", "enrich": "enrich-v1"}}
  ok    agnes ready, gemini not ready (key empty)
== real raw: 彼女は静かな図書館で本を読んでいる。
        HTTP 200 in 5142 ms
{
 "mode": "raw",
 "provider": "agnes",
 "model": "agnes-3.0-flash",
 "prompt_version": "analyze-v1",
 "attempts": 1,
 "trail": [
  "agnes/3.0-flash:ok"
 ],
 "toks": [
  {
   "s": "彼女",
   "pos": "noun",
   "r": "かのじょ",
   "ruby": [
    {
     "t": "彼女",
     "r": "かのじょ"
    }
   ]
  },
  {
   "s": "は",
   "pos": "particle",
   "r": "は",
   "ruby": null
  },
  {
   "s": "静かな",
   "pos": "adj",
   "r": "しずか",
   "ruby": [
    {
     "t": "静かな",
     "r": "しずか"
    }
   ]
  },
  {
   "s": "図書館",
   "pos": "noun",
   "r": "としょかん",
   "ruby": [
    {
     "t": "図書館",
     "r": "としょかん"
    }
   ]
  },
  {
   "s": "で",
   "pos": "particle",
   "r": "で",
   "ruby": null
  },
  {
   "s": "本",
   "pos": "noun",
   "r": "ほん",
   "ruby": [
    {
     "t": "本",
     "r": "ほん"
    }
   ]
  },
  {
   "s": "を",
   "pos": "particle",
   "r": "を",
   "ruby": null
  },
  {
   "s": "読ん",
   "pos": "verb",
   "r": "よん",
   "ruby": [
    {
     "t": "読",
     "r": "よ"
    },
    {
     "t": "ん"
    }
   ]
  },
  {
   "s": "で",
   "pos": "particle",
   "r": "で",
   "ruby": null
  },
  {
   "s": "いる",
   "pos": "aux",
   "r": "いる",
   "ruby": null
  },
  {
   "s": "。",
   "pos": "sym",
   "r": "",
   "ruby": null
  }
 ],
 "tokens": [
  {
   "word": "彼女",
   "pos": "名詞",
   "furigana": "かのじょ"
  },
  {
   "word": "は",
   "pos": "助詞",
   "furigana": "は"
  },
  {
   "word": "静かな",
   "pos": "形容動詞",
   "furigana": "しずか"
  },
  {
   "word": "図書館",
   "pos": "名詞",
   "furigana": "としょかん"
  },
  {
   "word": "で",
   "pos": "助詞",
   "furigana": "で"
  },
  {
   "word": "本",
   "pos": "名詞",
   "furigana": "ほん"
  },
  {
   "word": "を",
   "pos": "助詞",
   "furigana": "を"
  },
  {
   "word": "読ん",
   "pos": "動詞",
   "furigana": "よん"
  },
  {
   "word": "で",
   "pos": "助詞",
   "furigana": "で"
  },
  {
   "word": "いる",
   "pos": "助動詞",
   "furigana": "いる"
  },
  {
   "word": "。",
   "pos": "記号",
   "furigana": ""
  }
 ]
}
  ok    raw 200 (5142 ms)
== real raw: 一本のペンと十八年の手帳。
        HTTP 200 in 1007 ms
{
 "mode": "raw",
 "provider": "agnes",
 "model": "agnes-3.0-flash",
 "prompt_version": "analyze-v1",
 "attempts": 1,
 "trail": [
  "agnes/3.0-flash:ok"
 ],
 "toks": [
  {
   "s": "一",
   "pos": "num",
   "r": "いち",
   "ruby": [
    {
     "t": "一",
     "r": "いち"
    }
   ]
  },
  {
   "s": "本",
   "pos": "affix",
   "r": "ほん",
   "ruby": [
    {
     "t": "本",
     "r": "ほん"
    }
   ]
  },
  {
   "s": "の",
   "pos": "particle",
   "r": "の",
   "ruby": null
  },
  {
   "s": "ペン",
   "pos": "noun",
   "r": "ぺん",
   "ruby": null
  },
  {
   "s": "と",
   "pos": "particle",
   "r": "と",
   "ruby": null
  },
  {
   "s": "十八",
   "pos": "num",
   "r": "じゅうはち",
   "ruby": [
    {
     "t": "十八",
     "r": "じゅうはち"
    }
   ]
  },
  {
   "s": "年",
   "pos": "affix",
   "r": "ねん",
   "ruby": [
    {
     "t": "年",
     "r": "ねん"
    }
   ]
  },
  {
   "s": "の",
   "pos": "particle",
   "r": "の",
   "ruby": null
  },
  {
   "s": "手帳",
   "pos": "noun",
   "r": "てちょう",
   "ruby": [
    {
     "t": "手帳",
     "r": "てちょう"
    }
   ]
  },
  {
   "s": "。",
   "pos": "sym",
   "r": "",
   "ruby": null
  }
 ],
 "tokens": [
  {
   "word": "一",
   "pos": "数詞",
   "furigana": "いち"
  },
  {
   "word": "本",
   "pos": "接尾辞",
   "furigana": "ほん"
  },
  {
   "word": "の",
   "pos": "助詞",
   "furigana": "の"
  },
  {
   "word": "ペン",
   "pos": "名詞",
   "furigana": "ぺん"
  },
  {
   "word": "と",
   "pos": "助詞",
   "furigana": "と"
  },
  {
   "word": "十八",
   "pos": "数詞",
   "furigana": "じゅうはち"
  },
  {
   "word": "年",
   "pos": "接尾辞",
   "furigana": "ねん"
  },
  {
   "word": "の",
   "pos": "助詞",
   "furigana": "の"
  },
  {
   "word": "手帳",
   "pos": "名詞",
   "furigana": "てちょう"
  },
  {
   "word": "。",
   "pos": "記号",
   "furigana": ""
  }
 ]
}
  ok    raw 200 (1007 ms)
== real enrich: 3 sentences from reading.sentences ce605b3d6e current version, seq 0-2 (read-only SELECT)
[{"seq": 0, "text": "静かな退職（quiet quitting）」が浸透しつつある。"}, {"seq": 1, "text": "実際に会社を辞めるわけではなく、必要最小限の仕事をこなす働き方を意味する。"}, {"seq": 2, "text": "米国で2022年ごろから広まった造語だが、その使われ方は日本と欧米では全く異なる。"}]
        HTTP 200 in 1256 ms
{
 "mode": "enrich",
 "provider": "agnes",
 "model": "agnes-3.0-flash",
 "prompt_version": "enrich-v1",
 "attempts": 1,
 "chunks": [
  {
   "chunk": "c0",
   "seq": "0-2",
   "provider": "agnes",
   "model": "agnes-3.0-flash",
   "attempts": 1,
   "result": "ok",
   "trail": [
    "agnes/3.0-flash:ok"
   ]
  }
 ],
 "sentences": [
  {
   "seq": 0,
   "tr": "安静的辞职（静默离职）理念正在逐渐普及。",
   "fixes": []
  },
  {
   "seq": 1,
   "tr": "这并不意味着真正辞职，而是指只完成必要的最少工作量的工作方式。",
   "fixes": []
  },
  {
   "seq": 2,
   "tr": "这是自2022年左右在美国开始流行起来的造词，但其用法在日本和欧美截然不同。",
   "fixes": [
    {
     "tok_i": 2,
     "s": "2022",
     "reading": "にせんじゅうに"
    }
   ]
  }
 ],
 "warnings": [
  {
   "code": "fixes_dropped",
   "seq": 0,
   "count": 2
  }
 ]
}
  ok    enrich 200 (1256 ms)
        usage: {"text": 0, "image": 0, "video": 0, "analyze": 3, "total": 3}
        call_log:
          analyze|agnes:agnes-3.0-flash|success|200|5116|彼女は静かな図書館で本を読んでいる。|m=raw;a=1;fb=agnes/3.0-flash:ok;v=ok
          analyze|agnes:agnes-3.0-flash|success|200|1004|一本のペンと十八年の手帳。|m=raw;a=1;fb=agnes/3.0-flash:ok;v=ok
          analyze|agnes:agnes-3.0-flash|success|200|1252|静かな退職（quiet quitting）」が浸透しつつある。|m=enrich;n=3;c=1;a=1;c0[0-2]=agnes/3.0-flash:ok;v=ok
  (cleanup: deleted user id 22, 1 login_audit rows; rows left for t6p1_real_adb3cb: 0)
  ok    test account cleaned up
== 5 passed, 0 failed
```

## 附录 E：真实冒烟第 2 轮全文（加入读音覆盖校验后）

```text
  (created test account created user t6p1_real_168c8a (id 25, role user; limits: per_minute=30 daily=20 video_daily=default concurrent=default))
== /health analyze section (real config)
        {"providers": {"agnes": {"ready": true, "models": ["agnes-3.0-flash", "agnes-2.5-flash"], "strategy": "json_object"}, "gemini": {"ready": false, "models": ["gemini-flash-latest", "gemini-flash-lite-latest"], "strategy": "json_object+reasoning_effort"}}, "default_provider": "agnes", "order": ["agnes", "gemini"], "prompt_versions": {"raw": "analyze-v1", "enrich": "enrich-v1"}}
  ok    agnes ready, gemini not ready (key empty)
== real raw: 彼女は静かな図書館で本を読んでいる。
        HTTP 200 in 2261 ms
{
 "mode": "raw",
 "provider": "agnes",
 "model": "agnes-3.0-flash",
 "prompt_version": "analyze-v1",
 "attempts": 2,
 "trail": [
  "agnes/3.0-flash:bad",
  "r:ok"
 ],
 "toks": [
  {
   "s": "彼女",
   "pos": "noun",
   "r": "かのじょ",
   "ruby": [
    {
     "t": "彼女",
     "r": "かのじょ"
    }
   ]
  },
  {
   "s": "は",
   "pos": "particle",
   "r": "は",
   "ruby": null
  },
  {
   "s": "静かな",
   "pos": "adj",
   "r": "しずかな",
   "ruby": [
    {
     "t": "静",
     "r": "しず"
    },
    {
     "t": "かな"
    }
   ]
  },
  {
   "s": "図書館",
   "pos": "noun",
   "r": "としょかん",
   "ruby": [
    {
     "t": "図書館",
     "r": "としょかん"
    }
   ]
  },
  {
   "s": "で",
   "pos": "particle",
   "r": "で",
   "ruby": null
  },
  {
   "s": "本",
   "pos": "noun",
   "r": "ほん",
   "ruby": [
    {
     "t": "本",
     "r": "ほん"
    }
   ]
  },
  {
   "s": "を",
   "pos": "particle",
   "r": "を",
   "ruby": null
  },
  {
   "s": "読ん",
   "pos": "verb",
   "r": "よん",
   "ruby": [
    {
     "t": "読",
     "r": "よ"
    },
    {
     "t": "ん"
    }
   ]
  },
  {
   "s": "で",
   "pos": "particle",
   "r": "で",
   "ruby": null
  },
  {
   "s": "いる",
   "pos": "verb",
   "r": "いる",
   "ruby": null
  },
  {
   "s": "。",
   "pos": "sym",
   "r": "",
   "ruby": null
  }
 ],
 "tokens": [
  {
   "word": "彼女",
   "pos": "名詞",
   "furigana": "かのじょ"
  },
  {
   "word": "は",
   "pos": "助詞",
   "furigana": "は"
  },
  {
   "word": "静かな",
   "pos": "形容動詞",
   "furigana": "しずかな"
  },
  {
   "word": "図書館",
   "pos": "名詞",
   "furigana": "としょかん"
  },
  {
   "word": "で",
   "pos": "助詞",
   "furigana": "で"
  },
  {
   "word": "本",
   "pos": "名詞",
   "furigana": "ほん"
  },
  {
   "word": "を",
   "pos": "助詞",
   "furigana": "を"
  },
  {
   "word": "読ん",
   "pos": "動詞",
   "furigana": "よん"
  },
  {
   "word": "で",
   "pos": "助詞",
   "furigana": "で"
  },
  {
   "word": "いる",
   "pos": "動詞",
   "furigana": "いる"
  },
  {
   "word": "。",
   "pos": "記号",
   "furigana": ""
  }
 ]
}
  ok    raw 200 (2261 ms)
== real raw: 一本のペンと十八年の手帳。
        HTTP 200 in 2334 ms
{
 "mode": "raw",
 "provider": "agnes",
 "model": "agnes-3.0-flash",
 "prompt_version": "analyze-v1",
 "attempts": 1,
 "trail": [
  "agnes/3.0-flash:ok"
 ],
 "toks": [
  {
   "s": "一",
   "pos": "num",
   "r": "いち",
   "ruby": [
    {
     "t": "一",
     "r": "いち"
    }
   ]
  },
  {
   "s": "本",
   "pos": "affix",
   "r": "ほん",
   "ruby": [
    {
     "t": "本",
     "r": "ほん"
    }
   ]
  },
  {
   "s": "の",
   "pos": "particle",
   "r": "の",
   "ruby": null
  },
  {
   "s": "ペン",
   "pos": "noun",
   "r": "ぺん",
   "ruby": null
  },
  {
   "s": "と",
   "pos": "particle",
   "r": "と",
   "ruby": null
  },
  {
   "s": "十八",
   "pos": "num",
   "r": "じゅうはち",
   "ruby": [
    {
     "t": "十八",
     "r": "じゅうはち"
    }
   ]
  },
  {
   "s": "年",
   "pos": "affix",
   "r": "ねん",
   "ruby": [
    {
     "t": "年",
     "r": "ねん"
    }
   ]
  },
  {
   "s": "の",
   "pos": "particle",
   "r": "の",
   "ruby": null
  },
  {
   "s": "手帳",
   "pos": "noun",
   "r": "ちょう",
   "ruby": [
    {
     "t": "手帳",
     "r": "ちょう"
    }
   ]
  },
  {
   "s": "。",
   "pos": "sym",
   "r": "",
   "ruby": null
  }
 ],
 "tokens": [
  {
   "word": "一",
   "pos": "数詞",
   "furigana": "いち"
  },
  {
   "word": "本",
   "pos": "接尾辞",
   "furigana": "ほん"
  },
  {
   "word": "の",
   "pos": "助詞",
   "furigana": "の"
  },
  {
   "word": "ペン",
   "pos": "名詞",
   "furigana": "ぺん"
  },
  {
   "word": "と",
   "pos": "助詞",
   "furigana": "と"
  },
  {
   "word": "十八",
   "pos": "数詞",
   "furigana": "じゅうはち"
  },
  {
   "word": "年",
   "pos": "接尾辞",
   "furigana": "ねん"
  },
  {
   "word": "の",
   "pos": "助詞",
   "furigana": "の"
  },
  {
   "word": "手帳",
   "pos": "名詞",
   "furigana": "ちょう"
  },
  {
   "word": "。",
   "pos": "記号",
   "furigana": ""
  }
 ]
}
  ok    raw 200 (2334 ms)
== real enrich: 3 sentences from reading.sentences ce605b3d6e current version, seq 0-2 (read-only SELECT)
[{"seq": 0, "text": "静かな退職（quiet quitting）」が浸透しつつある。"}, {"seq": 1, "text": "実際に会社を辞めるわけではなく、必要最小限の仕事をこなす働き方を意味する。"}, {"seq": 2, "text": "米国で2022年ごろから広まった造語だが、その使われ方は日本と欧米では全く異なる。"}]
        HTTP 200 in 1258 ms
{
 "mode": "enrich",
 "provider": "agnes",
 "model": "agnes-3.0-flash",
 "prompt_version": "enrich-v1",
 "attempts": 1,
 "chunks": [
  {
   "chunk": "c0",
   "seq": "0-2",
   "provider": "agnes",
   "model": "agnes-3.0-flash",
   "attempts": 1,
   "result": "ok",
   "trail": [
    "agnes/3.0-flash:ok"
   ]
  }
 ],
 "sentences": [
  {
   "seq": 0,
   "tr": "“安静退休”（quiet quitting）的概念正在逐渐普及。",
   "fixes": []
  },
  {
   "seq": 1,
   "tr": "其含义并非实际辞去工作，而是指以必要最低限度完成工作的就业方式。",
   "fixes": []
  },
  {
   "seq": 2,
   "tr": "这是自2022年左右在美国流行开来的新造词，但其在日本和欧美的使用方式截然不同。",
   "fixes": []
  }
 ],
 "warnings": [
  {
   "code": "fixes_dropped",
   "seq": 0,
   "count": 2
  }
 ]
}
  ok    enrich 200 (1258 ms)
        usage: {"text": 0, "image": 0, "video": 0, "analyze": 3, "total": 3}
        call_log:
          analyze|agnes:agnes-3.0-flash|success|200|2229|彼女は静かな図書館で本を読んでいる。|m=raw;a=2;fb=agnes/3.0-flash:bad>r:ok;v=ok
          analyze|agnes:agnes-3.0-flash|success|200|2330|一本のペンと十八年の手帳。|m=raw;a=1;fb=agnes/3.0-flash:ok;v=ok
          analyze|agnes:agnes-3.0-flash|success|200|1253|静かな退職（quiet quitting）」が浸透しつつある。|m=enrich;n=3;c=1;a=1;c0[0-2]=agnes/3.0-flash:ok;v=ok
  (cleanup: deleted user id 25, 1 login_audit rows; rows left for t6p1_real_168c8a: 0)
  ok    test account cleaned up
== 5 passed, 0 failed
```

## 附录 F：真实冒烟第 3 轮全文（量词音变提示词修改后）

```text
  (created test account created user t6p1_real1_35b756 (id 26, role user; limits: per_minute=30 daily=5 video_daily=default concurrent=default))
        HTTP 200 in 1058 ms
{"mode": "raw", "provider": "agnes", "model": "agnes-3.0-flash", "prompt_version": "analyze-v1", "attempts": 1, "trail": ["agnes/3.0-flash:ok"], "toks": [{"s": "一", "pos": "num", "r": "いっ", "ruby": [{"t": "一", "r": "いっ"}]}, {"s": "本", "pos": "affix", "r": "ぽん", "ruby": [{"t": "本", "r": "ぽん"}]}, {"s": "の", "pos": "particle", "r": "の", "ruby": null}, {"s": "ペン", "pos": "noun", "r": "ぺん", "ruby": null}, {"s": "と", "pos": "particle", "r": "と", "ruby": null}, {"s": "十八", "pos": "num", "r": "じゅうはち", "ruby": [{"t": "十八", "r": "じゅうはち"}]}, {"s": "年", "pos": "affix", "r": "ねん", "ruby": [{"t": "年", "r": "ねん"}]}, {"s": "の", "pos": "particle", "r": "の", "ruby": null}, {"s": "手帳", "pos": "noun", "r": "てちょう", "ruby": [{"t": "手帳", "r": "てちょう"}]}, {"s": "。", "pos": "sym", "r": "", "ruby": null}], "tokens": [{"word": "一", "pos": "数詞", "furigana": "いっ"}, {"word": "本", "pos": "接尾辞", "furigana": "ぽん"}, {"word": "の", "pos": "助詞", "furigana": "の"}, {"word": "ペン", "pos": "名詞", "furigana": "ぺん"}, {"word": "と", "pos": "助詞", "furigana": "と"}, {"word": "十八", "pos": "数詞", "furigana": "じゅうはち"}, {"word": "年", "pos": "接尾辞", "furigana": "ねん"}, {"word": "の", "pos": "助詞", "furigana": "の"}, {"word": "手帳", "pos": "名詞", "furigana": "てちょう"}, {"word": "。", "pos": "記号", "furigana": ""}]}
  ok    raw 200 (1058 ms)
  (cleanup: deleted user id 26, 1 login_audit rows; rows left for t6p1_real1_35b756: 0)
  ok    test account cleaned up
== 2 passed, 0 failed
```
