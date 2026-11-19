# P2a 报告：新版 TtsWeb 服务（开发验证）—— 2026-11-18（完整交付版）

**状态：交付完成**。冒烟①–⑩ 加上附加的反例项全部通过：主流程 29 项加对齐 2 项加回落 3 项；离线逻辑单测 14/14 通过。测试文章已软删除。

**红线遵守**：
- 没有动 `/data/workspace/tts-web/`、tts-web.service 和 systemd，没有重启现行服务；
- 开发实例只在 127.0.0.1:8794 运行，验证完已停止；
- PG 只写了 reading schema；
- 令牌和口令明文没有出现在任何输出或日志里（已用 grep 核对服务日志，`rk_` 出现 0 次）。

## 1. 实现清单（`impl/service/`）

| 文件 | 作用 |
|---|---|
| `tts_web.py` | 以 `service_src/tts_web.py` 为底本。**第 1–512 行（TTS、HLS、流式、页面）一行没改**（已用 diff 核对）。改动：分享区段改为 PG 优先、SQLite 回落；新增 `/api/v1/*`、`/ingest.md`、`/ingest_prompt.md` 路由；端口、地址和配置路径从 `config.json` 或环境变量 `TTSWEB_CONFIG` 读取 |
| `tokenizer.py` | 分句加骨架的唯一管线（preview、commit、/share 共用） |
| `ingest.py` | 契约的纯逻辑：8 个错误码、preview、commit 校验和计划生成、/share 计划 |
| `store.py` | psycopg 3 访问 reading schema（读、commit、replace、/share、列表）；连接或查询失败时抛 PgUnavailable，由上层回落 SQLite 或返回 RETRY_LATER |
| `auth.py` | 令牌只比对哈希（常数时间）；`added_by` 代填规则；鉴权失败限流（每 IP 每分钟 10 次，超过封 10 分钟） |
| `make_tokens.py` / `tokens.json` / `tokens/*.txt` | 6 个调用方：A001、A002、A003、ddpc-claude（可代填 A001–A003）、77b-claude 为写权限，ddpc-cache 为只读。json 里只存 sha256；明文文件 0600，目录 0700 |
| `config.json` / `config_deadpg.json` | 开发配置：PG 走 127.0.0.1:5432，用 reading_rw，口令读 `../reading_rw.pw`；SQLite 回落指向 `dev_data/ttsweb_dev.db`；`grace_until=2026-12-02`。死端口版专供冒烟② |
| `ingest.md`（3,913 B）/ `ingest_prompt.md`（2,734 B） | 接口说明，以及给驻地的模板（默认模型 Opus、`prompt_version: v1`、硬性规矩全部写入） |
| `tests_logic.py` / `smoke.py` | 离线单测 / 8794 冒烟（模式有 main、align、fallback；只用标准库，令牌从文件读取、不打印） |
| `venv/` | 東紫乃安装：aiohttp 3.14.5、websockets 17.4、edge-tts 7.3.1、psycopg 3.3.9、fugashi 加全量 UniDic（与 P1 迁移用同一份词典） |

**PG 连接**：pg_hba 已经放行 reading_rw 经 127.0.0.1 用密码登录，**没有需要東紫乃处理的事**。

## 2. 冒烟结果（8794）

| # | 项 | 结果 |
|---|---|---|
| ① | 旧短链 5 篇加别名 8fd9a79c45，经 `/t` 取文（PG 源）、`/?s=` 出页面 | **12/12 通过**。标题正确；8fd9a79c45 命中 a798e5d069 |
| ①附 | 软删除的测试文 d2a28f9d78 走 `/t` | 返回 404，通过 |
| ② | PG 指向死端口 1：`/t/a798e5d069`、`/t/d2a28f9d78` | **回落 SQLite 取到，通过**；日志里有 2 条 `get_share fallback to sqlite`（连接被拒） |
| ②附 | PG 不可用时请求 `/api/v1/articles` | 返回 503 RETRY_LATER，通过 |
| ③ | preview | **通过**：「3万2,000」合并成一个 token；「quiet quitting」中间有空白 token |
| ④ | commit 测试文（自译两句，「P2a」中的数字 2 经 readings 补读音 つー） | **通过**：share_id `69e00aee7c`，状态 ready，v1；短链可取；文章哈希和 2 句的行哈希都是 64 位；tr_by=77b-claude、tr_via=self、tr_model 已落列 |
| ⑤ | 同文再次 commit | **通过**：duplicate:true，返回同一个 share_id |
| ⑥ | replace 改一个字（試験用→試験向け） | **通过**：版本 2，`versions.tool.kind=replace`，并记有 prev_text；**v1 的 2 句仍在**。用旧 base_version 再 replace 返回 409 VERSION_CONFLICT |
| ⑦ | 对齐型：译文缺一句 / 译文多一段 | **通过**：ALIGN_MISMATCH，kind 分别为 missing / orphan |
| ⑧ | 无令牌写；ddpc-cache 写；ddpc-cache 读；A001 代填 A002 | **全部通过**：401 AUTH_FAILED；403 AUTH_FAILED；200（列表 5 到 7 篇）；403 AUTH_FAILED |
| ⑨ | `/share` 不带令牌（宽限期内） | **通过**：响应只有 `{id, url}`，格式和现行一致，share_id `e8e5b4120e`；`/t` 能取到，added_by=A001；日志记了 `share no-token call`；重跑时按去重命中同一个 ID |
| ⑩ | `/ingest.md`、`/ingest_prompt.md` | **通过**，均返回 200 |

另外直接查库核对了：69e00aee7c 的 versions 有 v1（commit）和 v2（replace），sentences 两个版本共 4 行，行哈希各不相同；e8e5b4120e 为 v1（kind=share），状态 partial，toks 已生成。

**冒烟过程中的失败及原因**（都是测试脚本的问题，服务没有改过）：
- 第一轮有 8 项失败：测试文「P2a」里的数字 2 没有补读音，服务端按规则返回 BODY_INVALID「有 1 个 token 缺读音」。这恰好验证了读音必填的规则。改为在 smoke 里经 readings 补读音后通过。
- 第二轮⑦的 2 项失败：我在对齐用例的日文里误写了简体字「齐」，词典没有读音。改成「整列用」后通过。

## 3. 测试数据清理

| share_id | 内容 | 处理 |
|---|---|---|
| `69e00aee7c` | P2a 冒烟测试文（v1 加 v2） | **已软删除**（2026-11-18 23:01:01 JST） |
| `e8e5b4120e` | P2a share 测试 | **已软删除**（同上） |

- 对齐用例（⑦）都被拒，没有写入任何数据。
- 清理后 reading.articles 共 8 行，在用的 5 篇就是迁移的 5 篇存量。
- 开发用的 SQLite 副本没有写入（/share 走的是 PG）。

## 4. 分词管线与 P1 迁移脚本的差异

| 项 | P1 迁移（`migrate_stock.py`） | 新管线（`tokenizer.py`） |
|---|---|---|
| 词间空白 token | 有 | 有 |
| 数字串合并 | **无** | **有**：连续的数字、逗号、小数点，加上紧随的 万/億/兆，合并成一个 token（`pos=num`、`r=null`、`unk`、`merged=n`）；末尾的逗号或小数点不并入 |
| 数字的 ruby | 无 | 补读音后按整个 token 作一段注音 |
| 规范化 | 相同 | 相同，另外去掉首尾空白 |

影响：已迁移的 82 句仍是旧切法。驻地补译文时走 commit 的 replace，届时用新管线重切；在那之前，preview 得到的 token 下标可能和库里旧版本的下标不一致。

## 5. 与简报的偏离

1. **replace 需要 `share_id`**（也接受别名）：改了原文后 content_hash 变化，没法再按哈希定位文章。
2. **读音完整性的口径**：只要求含汉字或数字的 token 有读音，纯拉丁字母的词不要求。缺读音时不带 allow_partial 返回 BODY_INVALID，带了就落为 partial。
3. **ALIGN_MISMATCH 多了 `kind=altered`**：各句 tr 拼接后与 translation_text 内容不一致的情况。错误码仍然只有 8 条。
4. **/share 改用完整规范化**，与 commit 的去重口径一致。
5. **replace 改了原文时**，旧版原文存进 `versions.tool.prev_text`。
6. **开发实例绑定在 127.0.0.1**。

## 6. 需要留意的事（方案外，只记录）

- **PG 故障回落时，SQLite 里软删除过的文章会被取到**：SQLite 没有删除标记。冒烟②里 d2a28f9d78 经 PG 返回 404、经 SQLite 回落返回 200。生产 SQLite 里同样有这篇测试文，回落期间它会可见。影响很小，可以在生产切换时从 SQLite 副本里删掉，或者接受这个现状，请定。
- **全量 UniDic 会把日期量词读错**（「11月12日」读成 月=つき、日=か），已写进 `ingest_prompt.md`。
- **底本是 ddPC 版**（ffmpeg 优先找 Windows 路径）。在 77B 上会回落到 PATH 里的 ffmpeg；切换生产前要核对 77B 现行版是否和底本一致。本期没有验证 `/play` 播放，因为播放逻辑没有改动。
- **commit 时 added_by 缺省等于调用方名**：例如 77b-claude 自己入库时，`added_by=77b-claude`。驻地代 agent 入库时，应显式传 `added_by`。

## 7. 需要東紫乃处理的事

无。pg_hba 已放行，依赖已就位；本机镜像建库按 P1 报告的注意点进行即可。
