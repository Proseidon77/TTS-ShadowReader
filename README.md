# TTS-ShadowReader：TtsWeb 日文朗读（v3 定版，2026-11-19）

微软 Edge TTS 流式朗读服务（Python 服务＋Vue 3 跟读页）的现行代码仓，以 77B
生产为准。数据源为 77B db77database 的 reading schema（PostgreSQL）。ddPC 侧
仍是 v2 旧版部署、与 77B 已分叉，不在本仓库范围。

## 文件

- 服务端：`tts_web.py`（主服务）、`store.py`（PG/SQLite 存储）、`tokenizer.py`
  （fugashi 分词与校验）、`ingest.py`（preview/commit 入库）、`auth.py`（令牌鉴权）
- 页面：`player_v3.html`（跟读版 v3，现行）、`player_v2.html`（现行默认页）、
  `player.html`（旧版）；静态资产 `vue.global.prod.js`、`hls.min.js`
- 入库手册：`ingest.md`、提示词模板 `ingest_prompt.md`
- 数据库：`reading_ddl.sql`（三表 DDL）、`reading_role.sql`（reading_rw 角色，
  口令经 psql 变量传入、不在本文件）、`migrate_stock.py`（存量 SQLite 迁移）、
  `pull_reading.py`（本机镜像单向拉取，口令从本机文件读、不在本文件）
- `docs/`：设计定版 `design_proposal.md` 与 P1–P4f 各期报告

## 不入库（有意排除）

运行配置 `config.json`、令牌文件与哈希（`tokens/`、`tokens.json`）、数据库文件
（`ttsweb.db` 等）、备份、venv、开发量测数据与截图（dev_data）、日志。
