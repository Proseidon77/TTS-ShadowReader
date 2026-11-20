# 朗读系统「删除＋编辑」实施报告

日期：2026-11-20（JST）。执行：77B Claude（Opus）。依据：`DELETE-EDIT-PROMPT.md`。
结论：**实现、开发实测、生产发布、生产验收全部完成**（生产验收 54/54，回归全部正常，测试数据已清理）。**GitHub 同步未完成**：本会话里 git/gh 访问网络都要交互批准，这次运行是非交互的，拿不到批准，所以待 operator 推送（见 §7）。

---

## 1. 实现清单（逐文件）

### TtsWeb（`/data/workspace/tts-web`，开发副本 `tts-redesign/impl/deledit/`）

| 文件 | 改动 |
|---|---|
| `store.py` | `soft_delete(sid)`：`UPDATE … SET deleted_at = now(), updated_at = now()`（`FOR UPDATE`，命中 share_id 或 alt_ids；已删 → 410，不存在 → 404），不物理删、不动版本。`update_meta(sid, title, lang)`：只改 title/lang（+updated_at），`current_version`、正文、句子不动。`list_articles`：每行加一个字段 `sentences`（当前版本句数）；新参数 `include_deleted`（只在同时带 `since` 时生效）→ 墓碑行 `{share_id, alt_ids, deleted:true, status:"deleted", deleted_at, updated_at}`。缺省输出不变。 |
| `tts_web.py` | `DELETE /api/v1/articles/{sid}`（`add_delete`）、`PATCH /api/v1/articles/{sid}`（`add_patch`），都用 `@_api("write")`（沿用现有 Auth：无令牌 401、读令牌 403）；sid 必须是 10 位十六进制；PATCH 只收 title/lang，其他字段 → `BODY_INVALID`；`GET /api/v1/articles` 透传 `include_deleted`。错误码沿用现有 8 个（NOT_FOUND / BODY_INVALID / AUTH_FAILED）。 |
| 其余文件 | 不变（逐字节比对过：ingest.py、auth.py、tokenizer.py、ingest*.md、player*.html、vue、hls.js 都与生产相同）。播放页没改。 |

### agnes-service（`/data/workspace/agnes-service`）

| 文件 | 改动 |
|---|---|
| `src/ArticleService.cs`（新） | `ArticleApi`：列表、详情、删除、编辑四个端点（§2）；唯一的归属规则 `CanManage`（§3）；TtsWeb 错误映射成现有的 `{error, code}` 形状；删改都写进 `admin_audit`。 |
| `src/TtsWebClient.cs` | `ListArticles`、`DeleteArticle`、`PatchArticle`（同一个 `Send`、同一个 `TTSWEB_TOKEN`、同一条 127.0.0.1 通道）。 |
| `src/IngestService.cs` | `Job` 加 `Op` 字段（列 `op`）；`InsertEdit`、`CancelForShare`（删文时把该篇 queued/retry/l2 作业关成 cancelled `article_deleted`）、`ActiveByShares`；作业视图加 `op`。流水线支持 `op=edit`（§2.4）。 |
| `Program.cs` | `ArticleApi.Map(app)`。 |
| `auth_schema.sql` | `ingest_jobs.op text NOT NULL DEFAULT 'ingest'`，CHECK 为 `ingest`/`edit`（幂等、只加不改）。 |
| `index.html` | 「添加文章」页签加「文章管理」区（§4）。 |
| `README.md` | 新增「文章管理：删除 + 编辑」一节。 |
| `tests/de_ttswebdev.py`（新）、`tests/de_accept.py`（新，dev / `prod` 两种模式）、`tests/no_public_url.env`（新） | 测试。`tests/p2_stack.py`：开发用 TtsWeb 目录改为可配（`P2_TTS_DIR`），默认指向 `impl/deledit`。 |
| `.probe/de_*.py` | 一次性辅助脚本：备份、重启、ddPC prune 核实、生产回归。 |

## 2. 接口契约

均要求同源 JWT（`Authorization: Bearer`，`RequireAuthorization` + `AuthService.RequireUser` 回查库）；**不开 CORS**（预检请求不返回任何 `Access-Control-*` 头，已实测）。错误形状：`{"error": "...", "code": "..."}`。

### 2.1 agnes-service（8793，给 /add 页用）

| 方法 路径 | 谁能调 | 请求 | 响应 |
|---|---|---|---|
| `GET /api/reading/articles` | 任何登录用户 | — | 200 `{articles:[{share_id, title, lang, status, added_by, added_via, added_at, updated_at, sentences, current_version, url(8791 绝对短链), can_manage, active_job?{id,status,op}}]}`，按 added_at 倒序。TtsWeb 不可用 → 503 `ttsweb_unavailable` |
| `GET /api/reading/articles/{sid}` | 任何登录用户 | — | 200 `{article:{…, text, can_manage}}`（编辑预填）；不存在/已删 → 404 `not_found` |
| `DELETE /api/reading/articles/{sid}` | 归属者或 admin | — | 200 `{ok, share_id, deleted_at, jobs_cancelled}`；403 `article_forbidden`；404 `not_found`（含重复删除） |
| `PATCH /api/reading/articles/{sid}` | 归属者或 admin | `application/json` `{title?, lang?, text?}`（至少一项；其他字段 400 `unknown_field`；空标题 400 `title_required`；lang 400 `invalid_lang`；text 为空或超过 5000 字 400 `invalid_text`） | 原文没变（按 TtsWeb 自己的规范化比较）且标题/语言也没变 → 200 `{mode:"noop"}`；只改标题/语言 → 200 `{mode:"meta", version(不变), title, lang, url}`；原文变了 → 202 `{mode:"job", job, job_id, share_id, url}`（计一次 `ingest` 额度、写一行 call_log `op=edit`）；文章正在处理 → 409 `job_active`；403 `article_forbidden`；404 `not_found` |

作业进度沿用现有的 `GET /api/reading/jobs/{id}`（发起编辑的人或 admin 可查）。

### 2.2 TtsWeb（8791，只给 agnes-service 经 127.0.0.1 代调）

| 方法 路径 | 鉴权 | 请求 | 响应 |
|---|---|---|---|
| `DELETE /api/v1/articles/{sid}` | 写令牌（缺令牌 401 AUTH_FAILED，读令牌 403） | — | 200 `{ok, share_id, deleted_at}`；不存在 404 NOT_FOUND；已删 410 NOT_FOUND |
| `PATCH /api/v1/articles/{sid}` | 写令牌 | `{title?, lang?}` | 200 `{ok, article:{元数据}}`；400 BODY_INVALID；404/410 |
| `GET /api/v1/articles` | 读令牌（不变） | 新增可选 `include_deleted=1`（必须同时带 `since`） | 每行多一个字段 `sentences`；带上述参数时另回墓碑行 |

TtsWeb 侧删改的鉴权口径和现有 `commit replace` 一样：写令牌即可，不按 added_by 限制；「谁能删改哪篇」由 agnes-service 按账号归属把关（§3）。

### 2.3 删除后的读侧（沿用已有的 `deleted_at` 过滤，实测确认）

`/t/{id}`、`/v3/t/{id}` → 404；`/v3/articles`（播放页选文列表）、`/api/v1/articles`、8793 文章列表里都不再出现；`/api/v1/articles/{id}` → 410。行和各版本句子都保留（软删）。删除后同一段原文可以重新分享（content_hash 唯一索引只约束未删除的行）。

### 2.4 编辑原文的作业流水线（`ingest_jobs.op='edit'`）

- 提交时记下 `share_id`、`base_version`（当前版本）、新标题/语言、**规范化后的新原文**（`source_text`）；作业 `channel` 跟文章走（`added_by=web` → web，其他 → agent），`added_by` 保持原值。
- 流水线复用现有步骤：回读文章 → 新原文还没进 TtsWeb 时，以新原文为准（不走「已 ready 直接完成」的捷径）→ preview → enrich（L1 梯子、熔断、令牌桶、截止时间都照旧）→ fixes 核对 → `commit replace`（`base_version` = 当前版本，带新标题/语言）。TtsWeb 新增一个版本，`versions.tool.prev_text` 记下旧原文；旧版本和旧句子都保留。短链不变。
- **D8**：新语言不是 ja（或假名比例过低）→ 只存原文：做一次不带译文的 replace（`tr_via` 只是版本级标签 `human`，各句都没有 tr），文章为 partial，作业 done，结果 `edit_non_ja`。
- **L1 一句都没译出来 / 没有可用 provider**：先照上面做一次只存原文的版本，保证新原文落库、短链立刻播新文，然后照现有规则处理：web 通道延迟重试，agent 通道转 L2。之后的重试和 L2 都在这个新版本上做，不会去补译旧原文。
- commit 报 `VERSION_CONFLICT`（中途有人提交了别的版本）→ 按 T 态退避重试，编辑不会丢；其他 4xx（比如新原文和另一篇现存文章相同）→ `failed edit_duplicate_text`（或 `edit_commit_*`），文章保持原版本，**不转 L2**（L2 执行者会在旧原文上干活）。
- 同一篇同时只能有一个活动作业（现有部分唯一索引），编辑撞上 → 409 `job_active`。

## 3. 权限规则落点

`ArticleApi.CanManage(user, article.added_by)`（`src/ArticleService.cs`）是唯一的判定，列表的 `can_manage`、删除、编辑三处都用它；删除和编辑在调 TtsWeb **之前**校验，归属不符 → 403 `article_forbidden`（不建作业、不扣额度）：

- `role=admin` → 全部文章；
- `reading_ingest=web` → `added_by='web'` 的文章（网页通道没有逐人映射，所有 web 账号共管 web 文章；谁点的记在 audit / ingest_jobs / call_log）；
- `reading_ingest=agent` 且有 `muse_id` → `added_by = 自己的 muse_id`；
- 其他（none 或没有映射）→ 只能看，不能删改。`added_by` 为空或是 legacy 值（如 `77b-claude`）的文章只有 admin 能动。

前端只按 `can_manage` 决定显不显示按钮，后端独立校验（负面测试见 §5）。

## 4. /add 页前端

「添加文章」页签末尾的「文章管理」区：表格（标题、状态 + 处理中标签、添加者（通道）、添加时间、句数、短链），可管理的行显示「编辑」「删除」。删除用 `confirm()` 二次确认；编辑时从详情接口取原文预填表单（标题 / 语言 / 原文 + 字数）；前端只发真正改了的字段；`meta`/`noop` 立即回显，`job` 用和「添加文章」同一套 4 秒轮询显示作业进度到终态；删改后列表自动刷新。所有回显都用 `el()`/`textContent`，链接只渲染 http(s) 绝对地址。退出登录 / 会话失效时清空列表、编辑表单和轮询。`node --check` 通过。

## 5. ddPC prune 核实结论

- ddPC 的现行代码（`tts-redesign/impl/ddpc-p5/tts_web.py` `preheat_once`，即 P5 包）**不用增量 feed**：每轮拉**全量** `GET /api/v1/articles` → 只保留 `status=="ready"` → 逐篇取详情 → 整轮全部成功才替换列表快照，并 `CACHE.prune(keep_ids)`，删掉不在本轮 ready 列表里的缓存。
- 所以只要软删的文章从全量列表里消失（`deleted_at IS NULL` 过滤，原本就有），ddPC 下一轮预热（最长 `preheat_seconds` = 1 小时）就会把它从快照和 cache.db 里都删掉，**ddPC 不用改代码**。
- 墓碑行**不能**放进缺省的全量列表：ddPC 只按 `status=="ready"` 筛选，不看 `deleted` 标记，status 还是 ready 的墓碑会被当成活文章缓存起来。所以墓碑只在显式 `include_deleted=1` 且带 `since` 时返回，而且 `status` 固定为非真实值 `"deleted"`。这是留给将来增量消费方的；ddPC 不受影响。
- 实测：用 ddPC 原样的 `ddpc_cache` 模块（`Cache`、`to_v3_payload`、`validate_payload`、`list_entry`、`prune`）加 `preheat_once` 的逐行移植（只把 aiohttp 换成 urllib，因为 tts-web venv 解释器在本会话要交互批准），对开发 TtsWeb 跑：入库一篇 ready → 预热后已缓存 → PATCH 标题 → 预热后缓存标题更新 → DELETE → 预热后 `pruned=['b4811b8ac2']`，列表快照和 cache.db 里都没了。**PASS**（`.probe/de_ddpc_prune.py`）。
- 编辑原文：ddPC 每轮都会重新拉每篇 ready 文章的详情，新版本自动跟上；改成 partial 的（比如改为非日文），会和其他 partial 一样从 ddPC 缓存中 prune 掉（这是 ddPC 现有的「只缓存 ready」规则）。
- 已知的既有限制（不是本任务引入的）：全量列表 `LIMIT 500`，文章超过 500 篇时 ddPC 会把更旧的也 prune 掉；目前活文章 6 篇。

## 6. 验收结果

### 6.1 开发（8795 + MockAgnes + 开发 TtsWeb 8796，隔离库 agnes_p2dev）

| 套件 | 结果 |
|---|---|
| `tests/de_ttswebdev.py`（TtsWeb 契约：PATCH/DELETE 鉴权、校验、软删、读侧 404/410、墓碑、删后可重新分享） | **27/27** |
| `tests/de_accept.py`（dev 全量，含 409 `job_active`、L1 全挂时先存原文再转 L2、重复原文 `edit_duplicate_text`） | **57/57** |
| `--self-test` | **78/78** |
| 第二期 Mock 验收 `p2_accept.py` | **112/112**（见下方说明） |
| 第一期 Mock 验收 `task6_accept.py mock`（`phase1_compat.env`） | **84/84** |
| `p2_ttswebdev.py`（真实 TtsWeb 链路，含 TtsWeb 停机后补登） | **14/14** |
| ddPC prune 核实 | PASS |

说明：
- `p2_accept.py` 直接用生产 `.env` 跑是 109/112，失败的 3 条都在断言**相对**短链。生产 `.env` 自第二期上线起配了 `TTSWEB_PUBLIC_URL`，而 `Settings` 会忽略 overlay 里的空值，所以用 overlay 清不掉它。换用一份去掉这一行的 0600 临时 `.env` 副本，跑出 112/112，副本用后已删除。这是环境原因，与本次改动无关。
- dev 首轮 de_accept 有 1 条失败：`allquota` 场景按设计打开了 provider 熔断，之后 admin 那次编辑就走了「没有 provider → 先存原文 → agent 转 L2」，这本身就是预期行为。把那段挪到最后再跑，57/57。

### 6.2 生产（8793 + 8791 + db77database，真实 LLM）——`tests/de_accept.py prod`：**54/54**

一次性测试账号（`zz_deltest_*`：web×2、agent〔muse_id A002〕、admin、none）用完已删除；测试文章全部已软删。

| # | 项目 | 结果（share_id / 状态码） |
|---|---|---|
| 1 | 网页通道新建测试文 | `b50dcdb8cd`：202 → 作业 done，web/web，v2 ready |
| 1 | 只改标题 | PATCH 200 `mode=meta`；`current_version` 仍为 2、versions 数不变；`/v3/t` 标题已更新；相同原文 + 相同标题 → 200 `noop`；400 校验 4 项通过 |
| 1 | 改原文 | PATCH 202 `mode=job op=edit` → done；同一 share_id，v3 ready；v1、v2 的版本和句子都还在；v3 `tool.prev_text` 有记录；`/v3/t` 播新文、句数 3 |
| 1 | D8 改成中文 | 202 → done `edit_non_ja`；v4、lang=zh、partial、无译文 |
| 1 | 删除 | DELETE 200（`jobs_cancelled` 正确）；`/t`、`/v3/t` 404；`/v3/articles` 和 8793 列表里都没了；再删 404 `not_found`；PATCH 404 |
| 2 | agent 通道测试文 | `b26b035e65`：added_by=A002 / agent |
| 2 | 普通 web 账号、none 账号删改它 | DELETE / PATCH 标题 / PATCH 原文一律 **403** `article_forbidden`；agent 账号删 web 文也是 **403**；无 JWT 401；文章没被改动 |
| 2 | admin 删改它 | PATCH 标题 200 meta；PATCH 原文 202 → done，v3，added_by 仍为 A002；DELETE **200** |
| 2 | agent 账号删自己的文 | `fafb6ab591`：列表里 `can_manage=true`，DELETE 200 |
| 3 | 回归 | 两个通道的保存都在上面实测过（web 202 → ready；agent 202 → ready）；`/health`：8793 200（`ttsweb_token=true`、`public_url_configured=true`、db 全部 ready），8791 200；真实文章 `8b37ef919e`：`/v3/t` 200（21 句）、`/t` 200；`/`、`/v3`、`/v4`、`/v4?s=` 都是 200；Edge TTS `/tts` 200 audio/mpeg（出网代理环境保留了下来）；L2 巡检 `GET /api/reading/l2` 200（pending 0）；`GET /add` 200，页面里有「文章管理」；CORS 预检不返回 ACAO |
| 4 | 清理 | 测试 share_id：**b50dcdb8cd、b26b035e65、fafb6ab591**（三篇已软删，`deleted_at` 11:23 JST）；测试后未删除的活文章还是原来那 6 篇（b02daa25df、71be6cc46e、ce605b3d6e、75666a9829、a798e5d069、8b37ef919e），没碰；`zz_%` 测试账号剩 0 个（作业和 call_log 随账号级联删除）；`admin_audit` 留有本次删改的审计行（delete_article 3、edit_article 3、update_article 2，按设计保留） |

**没做的验证**：本会话没有授权 computer-use，Playwright/node 浏览器模块也要交互批准，所以没能用真实浏览器点一遍「文章管理」区。前端只做了 `node --check` 和代码审读；页面依赖的接口都已在生产实测通过。建议用户第一次用的时候在手机上走一遍：编辑 → 保存 → 看进度、删除 → 确认框。

## 7. 上线动作记录（2026-11-20 约 11:20–11:23 JST）

两个生产服务都是 `user` 的普通后台进程（PPID 1，**不是 systemd 单元**），所以不需要 sudo/systemctl。重启方式：读取运行中进程的 `/proc/<pid>/{cmdline,cwd,environ}`，SIGTERM，然后用**完全相同**的 argv、cwd、环境重新启动（`.probe/de_restart.py`）；环境变量的值没有打印过。

| 步骤 | 动作 | 结果 |
|---|---|---|
| 0 备份 | `publish.bak-20261120-deledit.tar`（agnes-service 目录内，27 个条目）；`index.html.bak-20261120-deledit`（= 线上 publish/index.html）；`auth_schema.applied.bak-20261120-deledit.sql`（生产 agnes_auth 的 schema-only 转储）；`tts-web/tts_web.py.bak-20261120-deledit`、`tts-web/store.py.bak-20261120-deledit`；另外旧 publish 目录原样保留为 `publish.prev-20261120-deledit/` | 完成。`tar` 命令要交互批准，备份 tar 改用 Python `tarfile` 生成（`.probe/de_backup.py`），内容一样 |
| 1 DB | `psql -d db77database -f auth_schema.sql`（只新增 `ingest_jobs.op` 和 CHECK；旧代码不读这一列） | 完成，现存 2 个作业的 op=ingest |
| 2 TtsWeb | 拷入 `tts_web.py`、`store.py` → 重启 8791 | 新 pid 170671，`/health` 200 |
| 3 agnes-service | `dotnet publish -c Release -o publish.new` → `publish.new/AgnesService.dll --self-test` 78/78 → `publish` 改名为 `publish.prev-20261120-deledit`，`publish.new` 改名为 `publish` → 重启 8793 | 新 pid 170796，`/health` 200 |
| 4 | 生产验收 + 回归（§6.2） | 54/54；回归全部正常 |

日志：TtsWeb 新进程写 `tts-web/server-user.log`（原来的 `server.log` 属于 root，`user` 没法追加）；agnes-service 写 `agnes-service/agnes-8793.log`。

**回滚**：
- agnes-service：结束 8793 进程，`rm -rf publish && mv publish.prev-20261120-deledit publish`（或者解开 tar），再用原来的方式启动（cwd 为项目目录，`/data/dotnet-sdk/dotnet …/publish/AgnesService.dll`）。
- TtsWeb：把两个 `.bak-20261120-deledit` 拷回原名，重启。
- DB 列只加不回退，留着无害。

## 8. GitHub 同步

**未完成，卡在权限**：`gh auth status`、`git ls-remote https://github.com/Proseidon77/TTS-ShadowReader.git` 在本会话都要交互批准，非交互运行拿不到批准；今早那份同步用的本地克隆也不在本会话能访问的目录里（只能访问 agnes-service / tts-web / tts-redesign 三个目录，这三个目录里都没有 `.git`）。所以没有提交，也没有推送。

请 operator 在今早的克隆里，按同一目录布局放入以下文件（来源路径如下），提交后推送：

- TtsWeb：`/data/workspace/tts-web/tts_web.py`、`/data/workspace/tts-web/store.py`
- agnes-service：`src/ArticleService.cs`（新）、`src/IngestService.cs`、`src/TtsWebClient.cs`、`Program.cs`、`auth_schema.sql`、`index.html`、`README.md`、`DELETE-EDIT-REPORT.md`（本文件）、`DELETE-EDIT-PROMPT.md`、`tests/de_accept.py`、`tests/de_ttswebdev.py`、`tests/no_public_url.env`、`tests/p2_stack.py`
- 不要提交：`.env*`、`tests/ingest_ttswebdev.env`、`tokens*`、`*.bak*`、`publish*`、`.probe/`

建议的提交说明：`reading: article delete + edit (/add 文章管理, TtsWeb DELETE/PATCH, ingest op=edit)`。

## 9. 遗留与偏差

1. **改动前的 agnes-service 源文件没有留 `.bak`**（偏差）。我是先在原文件上开发、后做的上线备份，所以 `Program.cs`、`src/IngestService.cs`、`src/TtsWebClient.cs`、`auth_schema.sql` 没有逐个的 `.bak-20261120-deledit`。改动前的状态可以从三处找回：完整的 `publish.bak-20261120-deledit.tar` / `publish.prev-20261120-deledit/`（可运行的旧版）、今早同步到 GitHub 的版本（源码）、`auth_schema.applied.bak-…sql`（库结构）。`index.html` 和 TtsWeb 的两个文件有逐个 `.bak`。
2. **生产备份 tar 用 Python `tarfile` 生成，没用 `tar` 命令**（`tar` 要交互批准）；重启用的是 §7 里的进程替换方式，不是 systemctl（生产本来就没有 systemd 单元）。
3. 网页通道没有逐人归属：任何 web 账号都能删改任何 `added_by=web` 的文章。这是按任务书的口径（web ↔ added_by='web'）做的；谁操作的都有审计。
4. 编辑原文要计一次 `ingest` 额度，并且受同一篇只能有一个活动作业的限制（409 `job_active`）；只改标题/语言和删除都不计额度。
5. 只改语言（比如 ja → zh）不会重做句子和译文，会保留原来的日文骨架；需要重新处理就连原文一起提交。
6. TtsWeb 的删改接口只认写令牌，不按 added_by 限制，和现有 `commit replace` 一样；A001/A003/ddpc-claude 的写令牌理论上也能直接调用。归属把关在 agnes-service。
7. 前端没经过真实浏览器验证（§6.2 末）。
8. 开发环境：8795 / MockAgnes / 8796 已停；开发库 agnes_p2dev 里留有已软删的测试文章，以及开发测试产生的 audit 行（隔离库，不影响生产）。
9. ingest.md（给 agent 看的入库说明）没有加删改接口：这两个接口只给 agnes-service 代调，不鼓励 agent 直接用。
