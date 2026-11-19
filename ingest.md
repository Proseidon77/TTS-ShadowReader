# TtsWeb 入库接口说明（ingest_api: v1，2026-11-18）

入库前先读本文和 `/ingest_prompt.md`（翻译模板）。服务端**不调用任何 LLM**，翻译、读音校对、对齐都由你（执行者）完成。

## 鉴权
- 请求头：`Authorization: Bearer $TTSWEB_TOKEN`。令牌放在本机 Secrets，**不要写进输出或文件**。
- 写入需要写令牌。`added_by` 缺省时等于调用方本身；驻地令牌（ddpc-claude）可以代填 A001/A002/A003，其他令牌代填会被拒（AUTH_FAILED）。

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
- 可选字段：`allow_partial:true`（允许缺译文或缺读音，文章状态为 partial）；`replace:true` 加 `share_id` 加 `base_version`（改稿，生成新版本）。
- 成功返回 `{ok, share_id, url, version, status, duplicate, warnings}`。
  - 原文和已有文章相同时返回 `duplicate:true`，不做任何改动。
  - 要改已有文章，用 replace。

## 只读
- `GET /api/v1/articles?since=<ISO>`：文章列表。
- `GET /api/v1/articles/{id}`：文章加当前版本的全部句子；`id` 也可以是别名短链。

## 错误码（错误体格式：`{ok:false, error:{code, message, hint, details}}`）
| code | HTTP | 怎么办 |
|---|---|---|
| AUTH_FAILED | 401/403 | 检查令牌；不要代填别人的 added_by；读令牌不能写 |
| BODY_INVALID | 400 | 按 details 修字段：缺字段、超长、readings 下标或表层形不符、读音非平假名、缺读音（按 details.missing 逐个补） |
| SPLIT_MISMATCH | 422 | 分句和服务端不一致：直接用 preview 的 sentences[].text |
| ALIGN_MISMATCH | 422 | kind=missing：某句缺译文；kind=orphan：译文多出一段；kind=altered：译文被改动 |
| TR_MISSING | 422 | 补齐译文，或者显式传 allow_partial |
| VERSION_CONFLICT | 409 | base_version 已经过期：先 GET 最新版本再改 |
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
