# P1（77B 侧）执行报告 —— 2026-11-18

红线遵守情况：
- 只读打开了 `impl/data/` 下的两份 SQLite 副本（`mode=ro`）；
- 在 PG 里只新建了 `reading` schema 和 `reading_rw` 角色，drama、jpdb、dbapi、agnes_auth、public 都没有动；
- 没有重启任何服务，也没有碰现行 TtsWeb 和它的 SQLite。

## 1. DDL 与角色

- `impl/reading_ddl.sql`：两侧共用的唯一一份 DDL。已在 77B 上执行；**再次执行只产生 NOTICE、不报错**（可以重复执行）。包含的对象：
  - schema `reading`；
  - 函数 `reading.sha256_hex(text)`（IMMUTABLE）；
  - 表 `articles`、`versions`、`sentences`；
  - 外键 `articles(share_id, current_version) → versions`（DEFERRABLE）；
  - 索引：未删除文章的 content_hash 部分唯一、alt_ids GIN、added_at、lemmas GIN；
  - 视图 `v_current_sentences`；
  - 全部注释。
- **两个 content_hash 都是生成列**，数据库自己计算，镜像两侧各算各的再对账，不依赖写入方：
  - `articles.content_hash` = sha256(规范化原文)；
  - `sentences.content_hash` = sha256(行内容，不含时间戳)。
- `impl/reading_role.sql` 加 `impl/setup_role.py`：建 `reading_rw`（LOGIN，search_path=reading）。
  - 授权：数据库 CONNECT；reading 的 USAGE；所有表 SELECT、INSERT、UPDATE（**不给 DELETE**）；函数 EXECUTE；默认权限也同样设置。
  - 口令写在 `impl/reading_rw.pw`（0600），只经 stdin 交给 psql，没有打印过。
  - 核验结果：对 reading 以外各 schema 的 USAGE 全部为 f，对其他 schema 可 SELECT 的表数为 0。

## 2. 迁移（`impl/migrate_stock.py`，用 p0/venv 运行，经本机 psql 单事务写入）

| 项 | 结果 |
|---|---|
| 源头条数 | 77B 2 条加 ddPC 5 条，共 7 条 |
| 去重后的 articles | **6 条**（含 1 条软删除的测试文），活跃 5 条 |
| 句子 | 82 句；tr 全部为 NULL；行哈希 82 个互不相同 |
| versions | 6 行，均为 v1，`tool.kind=import`，`dict=unidic/dicdir/sys.dic` |
| 状态 | 全部为 partial；只有测试文 `d2a28f9d78` 设了 deleted_at |
| 第一次执行 | 新增 6，补别名 0 |
| **第二次执行（幂等）** | **新增 0**，补别名 0，命中已存在 6 条 |

逐条结果：

| share_id | 来源 | 字数 | 句数 | content_hash 前 16 位 | alt_ids | 备注 |
|---|---|---|---|---|---|---|
| b02daa25df | ddPC | 323 | 27 | 72d36c2f879ca1e1 | — | 逃げ恥 第1話 |
| 71be6cc46e | ddPC | 413 | 26 | bbae68943bc73c3b | — | 逃げ恥 第2話 |
| ce605b3d6e | ddPC | 161 | 4 | 82932840a3c6e888 | — | 静かな退職（A003） |
| 75666a9829 | ddPC | 313 | 8 | 72fbc4a3bc5bbbb3 | — | マイクロソフト障害 |
| d2a28f9d78 | 77B | 27 | 2 | 581500b3ce78b4c9 | — | 测试文，**已软删除** |
| a798e5d069 | 77B | 543 | 15 | ec8457539655fe20 | **{8fd9a79c45}** | 北海道一文：77B 版 11:54:37 早于 ddPC 版 11:56:48，所以**以 77B 版为正主**，ddPC 的 ID 进 alt_ids |

校验情况：
- 提交前：①句子拼接等于原文、②toks 拼接等于句子，6 篇全部通过。
- 提交事务内：库里生成的 content_hash 等于本地计算值；每句的 `substr(text)` 等于句子原文。
- 两侧没有 share_id 冲突；7 条原文经规范化后都没有被改动。

## 3. 首份备份

- `/data/backups/reading_20261118.dump`，**39,055 字节**（sha256 前 16 位为 cefd941363b0fe15）。
- `pg_restore -l` 可以正常列出内容：49 个 TOC 条目，包括 schema、函数、3 张表、视图、数据、约束、索引、ACL。

## 4. 镜像侧（PG 16.2）复用 DDL 的注意点

- DDL 本身**没有用到 PG17 专属语法**。用到的特性和最低版本：`RETURN` 式 SQL 函数体（PG14 起）、`GENERATED … STORED`（PG12 起）、部分唯一索引、`IF NOT EXISTS`。PG16 都可以执行，但**还没有在 16.2 上实测过**。
- 数据库必须是 **UTF8 编码**。`sha256_hex` 声明为 IMMUTABLE 的前提是库编码固定为 UTF8，否则两侧哈希没法比。
- 两侧的生成列哈希都依赖 `jsonb::text` 的输出格式。这个格式在 16 和 17 之间是一致的，并且 toks 里不含浮点数。
- **备份是用 pg_dump 17 导出的，归档格式版本 1.16，PG16 自带的 pg_restore 读不了**。要在本机灌回，需要用 17 版的客户端工具。另外，PG17 的 pg_dump 输出里会带 `SET transaction_timeout`，PG16 服务端不认识这个参数，会报错但可以忽略；也可以改用 `--no-acl` 或纯 SQL 格式导出。这一点会影响第 37 条恢复流程在镜像侧的落地。
- 备份里**带了 reading_rw 的 ACL**。镜像侧没有这个角色，恢复时要么先建角色，要么加 `--no-acl --no-owner`。

## 5. 方案外的问题（只记录，没有扩大范围）

1. **MeCab 会吞掉词间空白**，空白存在 `word.white_space` 里。ce605b3d6e 的「quiet quitting」因此第一次没通过 ② 校验，整批没有写入。迁移脚本已在 `tokenize()` 里把空白补回成空白符号 token（`pd=空白`、`r=""`）。P0 那篇北海道文章句内没有空格，所以 P0 没暴露这个问题。**P2 的 preview 和 commit 骨架必须用同一套逻辑**；目前补丁只在 `migrate_stock.py` 里，`p0/fugashi_run.py` 没改。
2. **现在实际用的是全量 UniDic**：venv 里已经有 `unidic/dicdir`，fugashi 会优先用它，P0 时用的是 lite。所以 P0 报告里的读音错误率是 lite 的口径。全量词典的读音表现（大雪山、日本→にっぽん、数字）还没有复测。
3. **ce605b3d6e 原文有缺损**：开头是「静かな退職（quiet quitting）」が…」，缺了开头的「。按原样迁入，没有修正。
4. **简报里的字段清单和定版数据模型有出入**：
   - articles 按简报的清单建，没有第 3 节里的 `source_ref`、`tags`、`origin_site`、`legacy`；
   - sentences 在简报清单之外，按第 3 节和第 23 条补了 `len_class`、`kana`、`lemmas`、`fixes`、`tr_by`、`tr_via`、`tr_model`、`tr_at`。

   要不要补齐或删减，请定。
5. `/data/backups` 本来就存在（建目录的命令需要审批，没有执行，pg_dump 照样写成功了）。
