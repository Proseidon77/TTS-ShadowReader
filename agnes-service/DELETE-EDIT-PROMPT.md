# 朗读系统「删除＋编辑」功能实施任务书

日期：2026-11-20。执行者：77B Claude（Opus，用户已定默认 Opus）。发起：用户 2026-11-20 拍板，经「日文小助手」转交「Agnes API 测试」发车。

## 背景

朗读系统（77B：TtsWeb 8791＋agnes-service 8793＋reading 库）目前没有任何删除/修改入口：两边都无删除端点（只有 `articles.deleted_at` 软删字段与读侧过滤），修改只有底层 replace、无用户入口。本任务补齐：入口放在 8793 的 /add（添加文章）页面。先通读现行代码（agnes-service 的 Program.cs / src/IngestService.cs / src/TtsWebClient.cs / index.html，以及 TtsWeb 的 tts_web.py / store.py / ingest.py）再动手，按既有模式实现；细节可按代码事实微调，但下列口径不要改。

## 授权范围（与二期实施不同，本任务含上线）

本任务**包含**：实现 → 开发实例/Mock 测试 → 生产发布（改前备份、按既有方式 publish＋重启、核 /health）→ 生产验收实测 → GitHub 同步。若你的环境拒绝 sudo/systemctl 导致某步执行不了，不要假装完成：把已完成的部分与待执行的上线动作逐项写进报告的「上线清单」节，标明卡点，其余验收能做的先做。

## 范围

### 1. TtsWeb（77B，/data/workspace/tts-web）

- 新增软删端点：`DELETE /api/v1/articles/{sid}`（aiohttp 的 add_delete），写令牌鉴权（沿用现有 auth 体系），置 `articles.deleted_at`，经 store 层落库，不物理删。
- 读侧过滤已有：核实删后短链（/t、/v3/t）404、/v3/articles 与 /api/v1/articles 列表消失、播放页选文列表消失。
- **ddPC 只读缓存的 prune 衔接（重点核实）**：ddPC 靠现有机制自动跟随删除、不改 ddPC 代码。但要核实增量同步 feed 的实现——若已删行被过滤得干干净净，ddPC 的 prune 就看不见这条删除。须在 TtsWeb 侧让删除对现有 prune 逻辑可见（如增量返回带 deleted 标记的墓碑行），以 ddPC 现有代码能正确删掉缓存为准。把核实结论写进报告。
- 新增元数据更新端点（供只改标题/语言用）：如 `PATCH /api/v1/articles/{sid}`，写令牌鉴权，只更新 title/lang，不动正文与版本。

### 2. agnes-service（文章管理接口，供 /add 页同源调用）

- 在 /api/reading/ 下新增文章管理接口（列表＋删除＋编辑），JWT 鉴权、不开 CORS，沿用现有 IngestService/TtsWebClient 的模型与错误形状。
- 列表数据源：经 TTSWEB_TOKEN 调 TtsWeb `GET /api/v1/articles`（127.0.0.1:8791），或按现有代码里已有的等价通道；回给页面的字段：share_id、标题、status、added_by、added_via、时间、句数、短链。
- 删除：经 TTSWEB_TOKEN 代调 TtsWeb 软删端点（127.0.0.1），与 /share 代调同一条路，不直连数据库删。
- 编辑提交：只改标题/语言（正文未变）→ 代调元数据更新端点直接完成；改正文 → 在同一 share_id 上走现有作业流水线出新版本（preview→enrich→fixes 核对→commit replace，旧版本保留），复用 ingest_jobs 与现有作业状态查询，不另造一套；非日文仍按 D8 只存原文不 enrich。

### 3. 权限（按归属管，接口侧强制，不只是前端隐藏）

- 操作者通道身份：web 通道账号（reading_ingest='web'）↔ 文章 added_by='web'；agent 通道账号（reading_ingest='agent'，有 muse_id 映射）↔ 文章 added_by＝本人 muse_id（A001/A003）。
- **admin 账号全权**：可删改全部文章（含 agent 加的）。
- 归属不符且非 admin：删除/编辑接口一律 403。
- 列表对所有登录用户可见全部文章（含 added_by/status 标注）；前端按同一规则决定是否渲染删改按钮（非归属且非 admin 不给按钮），但后端必须独立校验，不能只靠前端。

### 4. /add 页前端（index.html）

- 在「添加文章」页签内加「文章管理」区（或同页签下分区）：文章列表（标题、状态、added_by、时间、句数、短链），每篇按权限显示编辑、删除按钮。
- 删除须二次确认（confirm 即可，沿用页面现有交互风格）。
- 编辑打开预填表单（标题/正文/语言，正文从文章详情接口取回预填），保存后：直改类立即回显成功；走作业的显示作业进度到终态（复用现有 ingest 作业轮询展示）。
- 删改成功后列表自动刷新。回显一律 textContent、不拼 HTML，沿用页面现有写法。

## 验收（生产实测，逐项在报告里给结果）

1. 网页通道新建一篇测试文 → 只改标题成功（不产生新版本）→ 改正文出新版本、短链不变、旧版本还在 → 删除后短链 404、列表消失。
2. 权限负面测试：一篇 agent 通道的（测试）文章，用普通 web 账号删/改应被接口拒绝（403）；用 admin 删/改成功（用一次性的测试文验，验完即删，不留垃圾）。
3. 回归：现有保存（web/agent 两通道）、播放（v3/v4）、L2 巡检链路不受影响；/health 两边正常。
4. 测试产生的文章最后清理干净（软删即可），报告里列明用过哪些 share_id。

## 仓库同步

实现与验收通过后，把 TtsWeb、agnes-service 的全部改动与实现报告同步进 GitHub 仓库 **Proseidon77/TTS-ShadowReader**（与 2026-11-20 早上的全量同步同一仓库、同一目录布局）。若 77B 上 git 推送凭据不可用：在本地克隆里提交好，把卡点写进报告，等 operator 推送，不要卡住其余交付。

## 红线

- 不改 ddPC 任何东西；不动生产数据里与本任务无关的文章（今早已手动软删的 2a85988389 不用再处理）。
- 任何令牌/口令/Key 的值不打印、不写进报告与代码。
- 改生产前按惯例备份（publish tar＋改动源文件 .bak），备份位置写进报告。

## 交付

`DELETE-EDIT-REPORT.md`（放 agnes-service 项目内）：实现清单（逐文件）／接口契约（方法、路径、鉴权、请求响应）／权限规则落点／ddPC prune 核实结论／验收逐项结果（含 share_id 与状态码）／上线动作记录／git 同步结果／遗留与偏差。
