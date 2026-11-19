# P4 报告：播放页 v3（Vue 3，挂在 /v3 与 v2 并行）—— 2026-11-19

**状态：开发验证通过（8794）**，部署清单见 §6。

红线遵守：
- 默认页 `/` 和 `player_v2.html` 一行未改（与底本 diff 无差异；`/` 返回内容的 sha256 等于 player_v2.html）；
- 没有切换默认页，没有动 ddPC 和生产；
- 后端只做了加性改动；播放内核按 v2 原样移植；
- 令牌没有出现在日志和输出里（grep `rk_` 结果为 0）。

## 1. 交付文件（`impl/service/`）

| 文件 | 说明 |
|---|---|
| `player_v3.html`（43,317 B） | Vue 3 全局包加 v2 内核。文件头注释包含移植对照表、与 v2 的差异、响应式与遥控说明 |
| `vue.global.prod.js` | 東紫乃提供，Vue 3.5.24，sha256 为 98478474…，已核对 |
| `tts_web.py` | **加性改动**：新增 `handle_v3`、`handle_vue_js`、`handle_v3_share`，以及 3 条路由 `/v3`、`/vue.global.prod.js`、`/v3/t/{sid}`（都是 no-store）。其他路由和函数一行没动 |
| `store.py` | **加性改动**：新增方法 `get_with_sentences()`，供 `/v3/t` 使用；已有方法都没动 |
| `verify_v3.py` | 离线核对 ruby 拼装和开关逻辑 |

## 2. 与简报的偏离

1. **简报的前提不成立：`/t/<id>` 并不返回 sentences**。P2a 的 `get_legacy` 按「与现行完全一致」的要求，只返回 text、lang、rate、title、added_by、added_at。为了不改动 `/t`（红线是「其他路由一行不动」），**新增了只读口 `GET /v3/t/<id>`**：返回旧字段，再加上 `share_id`、`version` 和当前版本的 `sentences`（seq、para、text、tr、kana、toks、len_class）。
   - 免令牌，暴露面和 `/t` 相同（短链本身就是凭据）；按 share_id 或别名都能命中；
   - PG 不可用时回落 SQLite，返回的数据不带 sentences，页面进入简易模式；
   - 页面先请求 `/v3/t`，这个口出错（404 以外的错误）时再退回 `/t`。
   - 因此 store.py 多了一个方法，**部署清单里比简报的预期多一个 `store.py`**。
2. **ruby 拼装的口径**：简报写的是「有读音 r 的 token 整体注音」。如果照字面执行，假名 token（例如「が」）上方也会标出同样的假名。所以改为：
   - token 有入库时算好的 `ruby` 段（送假名拆分）时，只给带读音的段加注音；
   - 没有 ruby 段、但 r 不为空且含汉字或数字时（例如数字合并 token），整个 token 注音；
   - 假名、标点、空白、拉丁字母都原样输出。

   熟字训（ruby 只有一段）就是整词注音，数字合并 token 是整串注音，标点没有注音，都符合简报的实际意图。
3. **富模式的权重**：简报说「kana/字数加权」。富模式下按 kana 长度计权，日语用 kcps=7.5 拍/秒；简易模式完全沿用 v2 的字数和 cps。
4. **新增保存者显示**：v2 只显示标题，没有显示保存者；v3 在标题旁显示「· A001 保存」。

## 3. 移植对照（摘要，全文在 player_v3.html 文件头）

- **原样搬入**：PRIOR、SPLIT_RE 等常量（三条正则与 v2 逐字一致）；splitSentences、speedOf、poll、bufferedEnd、totalDuration、locate、update、renderStatus、onUserScroll、sessionMatches、setBtn、refreshUI、hlsWanted、loadHlsLib、attachHls、startReading、onPlayActivate、toggleAudio、autoArmed、rewindOnce、结束看门狗、findNext、openSelect、keydown、setSelect、focusPlay、两种传参入口。
- **只改渲染落点**：
  - newSession 不再自己建 `.line` 节点，改为写 `V.lines`；
  - `S.nodes` 改为 `S.n`；
  - setCurrent 改为写 `V.idx`；
  - maybeScroll 在 `Vue.nextTick` 之后按下标取 `.line`；
  - showReader/showEditor 用 `V.readingView`（v-show）控制显示，textarea 仍由内核切换；
  - NAV 加入「假名」「译」两个开关。
- **新增**：
  - seekToLine：点句跳转，按 cum/W×总时长估算；直播期间不越过已生成的部分；
  - pendingSeek：还没开播时点句，先开播、起播后再跳；
  - 开关和 localStorage；
  - rubyHtml；
  - 简易模式标注。
- **Vue 只接管声明式渲染**：标题和保存者、简易模式标注、卡片、句子列表、高亮、开关状态。播放、HLS 和遥控仍是原生 JS。

## 4. 验证结果（8794）

| # | 项 | 结果 |
|---|---|---|
| ① | `/v3?s=a798e5d069` | **通过**：返回 200，`Cache-Control: no-store`；HTML 里有 Vue 挂载点 `#app`、`v-for="(ln, i) in lines"`、`rubyHtml`，并引用 `/vue.global.prod.js`（200，160,896 B，sha256 为 98478474…）。`/v3/t/a798e5d069` 和别名 `8fd9a79c45` 都返回 15 句（版本 2，译文齐全）；`ce605b3d6e` 返回 4 句 |
| ② | ruby 拼装离线核对（verify_v3.py；用页面函数的等价移植，并核对了页面源码中这几行确实未变） | **通过**。抽查 3 句：<br>seq 3 中「3万2,000」整串注音 さんまんにせん、「47」注音 よんじゅうなな；<br>seq 1 中「18」注音 じゅうはち，「立っ」只给「立」注音（た）；<br>seq 0 中「日本」整词注音 にほん、「北海道」整词注音 ほっかいどう，标点没有注音。<br>15 句全部满足：去掉 rt 后的文本等于原句、标点不注音；HTML 转义正确 |
| ③ | 简易模式 | **通过（数据路径和代码路径）**。PG 死端口时，`/v3/t/a798e5d069` 返回的字段里没有 sentences，样本存为 `dev_data/v3_fallback_a798e5d069.json`；`/v3?s=` 和 `/v3?text=` 页面都返回 200。代码路径：没有 sentences 或走 `?text=` 时 `V.simpleNote=true`；newSession 里 `RICH` 不匹配，于是用 v2 客户端分句，并置 `V.simple=true`，页面显示「简易模式」标注、隐藏两个开关。`/v3/t/ce605b3d6e` 在开发 SQLite 里没有这篇，返回 404，页面会保留默认文本（与 v2 行为一致） |
| ④ | 开关自查 | **通过**。键名 `ttsv3.kana`（"1"/"0"）、`ttsv3.tr`（"show"/"blur"/"hide"）；默认假名为开、译为显示；存值非法时回落为显示；三态循环 显示→模糊→隐藏→显示；切换「译」时清空揭开记录。模糊态口径：揭开记录卡片和列表共用一张表，点任意一处揭开该句，两处同时显示；模糊态下点列表里的译文是揭开、不跳转，点原文是跳转 |
| ⑤ | smoke.py 全量 | **无回归**：main 31/31，fallback 3/3。日志里有 2 条 `v3 get_share fallback` |
| ⑥ | 播放链路 | **通过**：POST /play 0.8 秒返回会话 URL；m3u8 共 3 段并已有 ENDLIST；seg000.ts 为 33,088 B，同步字节 0x47 |

**本期没有验证到的**：在真实浏览器里的渲染和交互（Vue 挂载、卡片和列表布局、点句跳转、iOS、webOS、电视遥控）。本会话没有可用的浏览器，node 也需要审批，所以 JS 的语法和运行只靠代码审读，没有实测。**建议部署到 /v3 后，先在手机和电视上各打开一次 `/v3?s=a798e5d069` 走查**；有问题直接回退 /v3，对默认页没有影响。

**测试数据清理**：冒烟产生的 `26e0b537ec`（冒烟测试文，v1、v2）和 `93480e6a12`（/share 测试）已软删除（2026-11-19）。目前在用的仍是 5 篇存量，都是 v2、ready。

## 5. 顺带观察

- 5 篇存量都已经是 v2、ready，带译文和补齐的读音（应该是驻地按 P2b 补的）。v3 在富模式下可以直接用。
- `/v3/t` 的响应比 `/t` 大（北海道那篇约 70 KB，主要是 toks）。手机首屏会多一次加载，目前可以接受。

## 6. 部署清单（由東紫乃执行；我没有动生产）

**前提**：生产已经在运行 P2a 版服务栈（tokenizer.py、ingest.py、auth.py、store.py、config.json、tokens.json 等）。如果生产还是旧版单文件服务，需要先按 P2a 整体部署，本清单不适用。

拷到现行目录 `/data/workspace/tts-web/`：

| 文件 | sha256 前 12 位 | 说明 |
|---|---|---|
| `player_v3.html` | 69ae92663d58 | 新文件 |
| `vue.global.prod.js` | 9847847474877 | 新文件 |
| `tts_web.py` | 57dd1b2af156 | 在 P2a 版基础上加了 3 个 handler 和 3 条路由 |
| `store.py` | 6f566fe279c6 | **简报预期里没有这一项**：新增了 `get_with_sentences()` |

步骤：
1. 备份现行的 `tts_web.py` 和 `store.py`。
2. 拷入上面 4 个文件。
3. **重启 tts-web.service 一次**。
4. 冒烟：
   - `/health`；
   - `/?s=a798e5d069` 仍是 v2；
   - `/v3?s=a798e5d069` 返回 200；
   - `/v3/t/a798e5d069` 带 sentences；
   - `/vue.global.prod.js` 返回 200；
   - 也可以把 `smoke.py` 的 SMOKE_BASE 指向生产只跑①，但不要跑写入项，免得留下测试数据。
5. 手机和电视走查 /v3（见 §4）。

回滚：恢复备份的 `tts_web.py` 和 `store.py` 后重启；两个新文件留着也无害。
