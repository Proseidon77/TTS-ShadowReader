# TtsWeb 入库接口说明（ingest_api: v1，2026-11-18；第二期补充 2026-11-19）

入库前先读本文和 `/ingest_prompt.md`（翻译模板）。服务端**不调用任何 LLM**，翻译、读音校对、对齐都由你（执行者）完成。

## 鉴权
- 请求头：`Authorization: Bearer $TTSWEB_TOKEN`。令牌放在本机 Secrets，**不要写进输出或文件**。
- 写入需要写令牌。`added_by` 缺省时等于调用方本身；驻地令牌（ddpc-claude）可以代填 A001/A002/A003；agnes-service 服务令牌可以代填 A001/A002/A003/web（`web` 用于 8793「添加文章」网页通道，由 agnes-service 代调 /share）；其他令牌代填会被拒（AUTH_FAILED）。

## 两步姿势：preview → commit
1. `POST /api/v1/ingest/preview` `{text, lang?}` → 返回规范化后的 `text`、`content_hash`，以及 `sentences[{seq, para, text, char_start, char_end, kana, toks}]`。
   - 每个 toks 项含 `s` 表层形、`r` 平假名读音（null 表示需要补）、`unk:true` 表示词典里没有读音。
   - 连续数字会合并成一个 token（如「3万2,000」）。词间空格是单独的空白 token。
2. **通读全篇后**整篇翻译，再 `POST /api/v1/ingest/commit`：
```json
{"text": "<原文>", "title": "<标题>", "lang": "ja", "rate": "-20%",
 "added_by": "A003", "executor": "ddpc-claude", "tr_via": "claude-code",
 "tr_model": "claude-opus-5-5", "prompt_version": "v1",
 "sentences": [{"text": "<preview 原样>", "tr": "<中文>",
                "readings": [{"tok": 4, "s": "18", "r": "じゅうはち"}]}]}
```
- `sentences[].text` 必须**逐句原样**取自 preview，不得合并、拆分或改字。
- `readings` 只改读音，不改分词边界。`tok` 是 preview toks 的下标（从 0 起，含标点和空白 token）；`s` 用于核对；`r` 只能是平假名（允许 ー）。
- **含汉字或数字的 token 读音必须齐全**，数字是重灾区。
- 现成译文**只对齐不重译**：带上 `translation_text`（译文全文），`tr_via` 填 `provided`，各句 `tr` 从中原样切分。
- `tr_via` 取值：`self` / `claude-code` / `agy-gemini` / `human` / `import` / `provided` / `gemini-api` / `agnes-api`（后两个是 agnes-service 的 API 路专用）。
- 可选字段：`allow_partial:true`（允许缺译文或缺读音，文章状态为 partial）；`replace:true` 加 `share_id` 加 `base_version`（改稿或补译，生成新版本，见下）；
  `added_via`（`agent` 或 `web`，缺省按令牌推断为 agent；replace 不改它）；
  逐句 `sentences[].tr_via` / `sentences[].tr_model`（同一版本里不同句由不同模型产出时逐句记出处，缺省用版本级值）。
- 成功返回 `{ok, share_id, url, version, status, duplicate, warnings}`。
  - 原文和已有文章相同时返回 `duplicate:true`，不做任何改动。
  - 要改已有文章，用 replace。

## 先存原文（share-first）与补译（replace 口径，第二期）
- 任何通道入库，第一步先 `POST /share {text, title, lang?, by?, added_via?}` 存原文拿短链（约 2 秒可朗读，文章 `status=partial`），其后各级只**补译文**。
- **补译一律走 replace**：`GET /api/v1/articles/{id}` 读当前版本 → preview → 整篇翻译＋补读音 →
  `commit {replace:true, share_id, base_version:<刚读到的 current_version>, text:<文章原文>, title:<原标题>, lang:<原值>, rate:<原值>, …}`。
  - title、lang、rate 必须**原样带回**（replace 会用请求里的值覆盖；漏传 rate 会被重置成 -20%）。原标题为空时用正文前 20 字。
  - 普通 commit 对已存在的文章只会返回 `duplicate:true`，**补不了译文**。
- 文章已经是 `ready`：**不要再补**，直接结束。
- 一个版本一个执行者：补译整篇重做，不把别人半成品的译文拼进自己的版本。

## VERSION_CONFLICT 的语义（第二期）
- replace 时 `base_version` 不是当前版本 → 409 `VERSION_CONFLICT`（details 带 `current_version`）。
- 这表示**别人（另一执行者）已经先提交了新版本**，是「对方已完成」的收敛信号，**不算失败**：
  1. `GET /api/v1/articles/{id}`；`status=ready` → 结束，不要覆盖对方的版本；
  2. 仍是 partial → 以新的 `current_version` 为 base 重新做（整篇），或交还发起人。

## L2 补译（agy 执行者，第二期）
API 路（agnes-service 编排，`tr_via=gemini-api/agnes-api`）没能把文章做成 ready 时，文章保持 partial（照常可朗读），并进入 **L2 待办**：
- **执行者**：agy（Gemini agent），`tr_via=agy-gemini`，`executor` 填 agy 自己的标识。ddPC 上与 A001 VM 上各有一份 agy，同一 Google 账号、共用额度池。
- **谁调度**：agent 通道 → 发起入库的 agent 自己在 ddPC 调 agy 当场接手；网页通道（agnes-service 8793 的「添加文章」页签 / `/add`）→
  巡检者 A001 **每小时 1 次**（08:00–24:00 JST，夜间不巡）拉清单（agnes-service `GET /api/reading/l2`，MuseAgent1 的 JWT），
  用**自己 VM 上的 agy** 直接完成；清单为空静默，只在处理了待办或失败时报告。
- **怎么做**：按上面「补译（replace 口径）」整篇做：GET 文章 → 已 ready 就停 → preview → 通读全篇翻译、补齐读音 → commit replace（`base_version` 用 GET 到的 current_version）。
- **完成判定**：不用回填。agnes-service 会**自动对账**（拉清单时＋每小时）：文章 `status=ready` 且版本号高于清单里的 `base_version` 即关闭。
  失败原因想留档可选调 `POST /api/reading/l2/{job_id}/report {note}`。
- 遇到 VERSION_CONFLICT 按上节处理（多半是 API 路的延迟重试先完成了）。
- 每月 1 次 L2 演练：拿一篇短文走完整 L2 链，确认 agy 的凭据与本手册仍可用。

## 只读
- `GET /api/v1/articles?since=<ISO>`：文章列表（含 `added_via`）。
- `GET /api/v1/articles/{id}`：文章加当前版本的全部句子；`id` 也可以是别名短链。
- `POST /api/v1/ingest/readings_check`（read 令牌，第二期）：`{items:[{sentence, tok, s, r}]}` → `{results:[{supported, candidates}]}`。
  对该句做 nbest（N≈20），收集与该 token 边界对齐的读音候选；`r` 在候选里即 `supported:true`。用于读音改写的词典佐证（不进 commit 校验）。

## 错误码（错误体格式：`{ok:false, error:{code, message, hint, details}}`）
| code | HTTP | 怎么办 |
|---|---|---|
| AUTH_FAILED | 401/403 | 检查令牌；不要代填别人的 added_by；读令牌不能写 |
| BODY_INVALID | 400 | 按 details 修字段：缺字段、超长、readings 下标或表层形不符、读音非平假名、缺读音（按 details.missing 逐个补） |
| SPLIT_MISMATCH | 422 | 分句和服务端不一致：直接用 preview 的 sentences[].text |
| ALIGN_MISMATCH | 422 | kind=missing：某句缺译文；kind=orphan：译文多出一段；kind=altered：译文被改动 |
| TR_MISSING | 422 | 补齐译文，或者显式传 allow_partial |
| VERSION_CONFLICT | 409 | 对方已先提交：按上面「VERSION_CONFLICT 的语义」收敛，不算失败 |
| NOT_FOUND | 404/410 | ID 不存在，或文章已删除 |
| RETRY_LATER | 429/503 | 等待后重试（看 Retry-After） |

warning（不阻断）：TOKENIZER_FAILED、TR_SUSPECT（译文疑似没翻）、SPLIT_NONSTANDARD。

## 驻地运行手册
1. 收到任务（原文、标题、发起人，可能还有随文译文）。
2. 拉 `/ingest_prompt.md`。
3. 调 preview。
4. 按模板通读、翻译或对齐、补读音。
5. 调 commit。
6. 出错按 hint 自纠，**最多重试 2 次**；仍然失败就用 `POST /share {text, title, by}` 存原文（文章可以先播），并向发起人报告错误码。
7. 回报 share_id、句数、模型、prompt_version。
