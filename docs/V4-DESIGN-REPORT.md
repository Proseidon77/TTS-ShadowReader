# 朗读页 v4 视觉重设计报告（参考 Animal Island UI）

日期：2026-11-20 · 产物：`/data/workspace/tts-redesign/v4/player_v4.html`（已部署到 `/data/workspace/tts-web/player_v4.html`，md5 `c31ffd23eb46a72d48dce58883ce3b3f`）

> **结论速览**
> - 基底为 `player_v3.html`（md5 `7f14dc1d…`，含 lastPointerNav 与编辑视图下线两项修正），内联 `<script>` 与 v3 **逐字节相同**。
> - 可读性硬指标全部达标（表见 §2）；截图自检发现 1 处问题（当前句左条对比 2.8:1）已改并复核。
> - ⚠️ **`/v4` 生产口尚未生效**：`tts_web.py` 已加路由（diff 仅此一处），但本会话 **无权重启 `tts-web.service`**（`systemctl restart` → Access denied；`sudo` 需人工批准）。当前 `127.0.0.1:8791/v4` 仍 404。需 operator 执行一次：`sudo systemctl restart tts-web.service`，然后 `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8791/v4` 应为 200。
> - 截图是经临时只读预览口 `127.0.0.1:8796`（node 小代理：/v4 读同一个 `player_v4.html`，其余请求原样转发 8791）做的真实渲染，与重启后 8791 的 /v4 读同一文件。

---

## 0. 过程说明（本次为续跑）

上一轮运行（00:2x JST）撞到 5 小时限额中断，已留下 `player_v4.html` 草稿、`tts_web.py` 的 `/v4` 路由与备份 `tts_web.py.bak-20261120-v4`，但未重启服务、未截图、未出报告。本轮先逐段核对草稿（JS 与 v3 逐字节相同、样式改动逐条审过）确认可用，再在其上完成：截图自检、据截图修 1 处、对比度核算、报告。

## 1. 设计令牌映射表（v3 旧值 → v4 新值 → animal 出处）

| 语义变量 | v3 旧值 | v4 新值 | 出处 animal 令牌 / 说明 |
|---|---|---|---|
| `--bg` 页底 | `#f7f5f0` | `#f8f8f0` | `--animal-bg` |
| `--paper` 卡片/全文/面板底 | `#fffdf8` | `rgb(247,243,223)` = `#f7f3df` | `--animal-bg-content`（纯色平整，无装饰） |
| `--field` 下拉/列表项/关态开关底 | `#fff`（硬编码） | `#fffbe7` | `@bg-color-input` |
| `--ink` 正文字 | `#1b1b1b` | `#4a3823` | `--animal-text-body #725d42` 加深（正文要 ≥4.5，且大段阅读要更稳） |
| `--ink-strong` 当前句字 | `#000`（硬编码） | `#3b2a17` | 暖深棕，绝不用纯黑 |
| `--title` 标题 | （继承 ink） | `#794f27` | `--animal-text`，字重 800 |
| `--muted` 副字/rt/状态/标签 | `#5a554c` | `#6b5640` | `--animal-text-muted #8a7b66` 加深至 ≥4.5 |
| `--tr-ink` 译文 | `#3a3a3a`（硬编码） | `#5e4a33` | 暖棕中间档 |
| `--line-done` 已读句 | `#8a857c` | `#8a7b66` | `--animal-text-muted`（≥3:1） |
| `--brand` 主色 | `#1f5a99` | **删除** → `--accent` `#19c8b9` | `--animal-primary` |
| `--brand-dark` | `#163f6b` | **删除** → `--accent-active` `#11a89b` | `--animal-primary-active` |
| （新）`--accent-bg` | `#eef4fb` 等硬编码 | `#e6f9f6` | `--animal-primary-bg` |
| （新）`--accent-ink` 开关/选文键文字 | `var(--brand)` | `#0b5f58` | 主色加深至可读 |
| （新）`--accent-deep` 当前句左条/hover 边 | `var(--brand)` | `#0b6e66` | 主色加深（截图复验后由 `#0e8a7f` 再加深，见 §5） |
| （新）`--on-accent` 播放键字/图标 | `#fff` | `#2f2112` | 深棕字（白字在薄荷上仅 2.1:1） |
| `--hl` 当前句底 | `#ffe07a` | `#f7cd67` | Card 调色板 `app-yellow` |
| `--focus-ring` 焦点描边 | `#0b1b33`（冷深蓝） | `#3b2a17` | animal 硬规则 4：禁冷蓝焦点 |
| `--focus-halo` 焦点光环 | `#ffc800` | `#ffcc00` | `--animal-focus-yellow` |
| `--focus-bg` 列表/页头键焦点底 | `#fff3c4`（硬编码） | `#fff0b8` | 焦点黄浅调 |
| `--tr-bar` 译文竖条 | `#9bb7d6`（蓝） | `#82d5bb` | Card 调色板 `app-teal` |
| `--edge` 框线 | `#cbc4b6` / `#aaa294` | `#c4b89e` | `--animal-border` |
| （新）`--edge-soft` | `#ddd5c6` / `#e3ddd0` | `#ddd1b4` | 暖浅边 |
| （新）`--err` | `#9a2b1b` | `#a8382f` | 暖红，≥4.5 |
| 3D 阴影色 | — | `#bdaea0` | `--animal-shadow-btn`，仅播放键 |
| 缓动 | `ease` / 默认 | `cubic-bezier(0.4,0,0.2,1)` | `--animal-ease`，0.15–0.25s |
| 圆角 | 卡 12/18，按钮 10/14，开关 8/10 | 卡/面板 22（电视 24），按钮/开关/下拉 50 pill，列表项 18（电视 22），句行 12 | `--animal-radius*`；交互元素均 ≥12 |
| 字体链 | 系统链 | 链首加 `Nunito, "Hiragino Maru Gothic ProN", "M PLUS Rounded 1c", "Zen Maru Gothic"`，其后 v3 原链 | 仅名字，**无任何字体请求** |
| 字重 | body 默认 400 | body 500、标题 800、rt 400→500 | animal 字重表 |

装饰：`body` 背景 = 页顶 6px 六色糖条（primary/app-yellow/app-pink/app-blue/app-green/app-orange）＋ 26px 网格极淡波点 `#efe9d6`。纯 `background`，不占版面，不进入卡片/全文/面板（它们自带纯色底）。无持续性动画。

## 2. 对比度核算表（WCAG 2.x 相对亮度；`tools/contrast.js` 生成，可复跑）

| 用途 | 前景 | 背景 | 比值 | 门槛 | 结论 |
|---|---|---|---|---|---|
| 当前句卡片日语 .card-ja | `#4a3823` | `#f7f3df` | 10.02:1 | ≥4.5 | 达标 |
| 卡片译文 .card-tr | `#5e4a33` | `#f7f3df` | 7.53:1 | ≥4.5 | 达标 |
| 卡片 ruby rt | `#6b5640` | `#f7f3df` | 6.22:1 | ≥4.5 | 达标 |
| 全文日语（未读）.line | `#4a3823` | `#f7f3df` | 10.02:1 | ≥4.5 | 达标 |
| 全文译文（未读）.line .tr | `#5e4a33` | `#f7f3df` | 7.53:1 | ≥4.5 | 达标 |
| 全文 ruby rt（未读） | `#6b5640` | `#f7f3df` | 6.22:1 | ≥4.5 | 达标 |
| 当前句日语/译文/rt .line.cur | `#3b2a17` | `#f7cd67` | 9.08:1 | ≥4.5 | 达标 |
| 已读句（日语/译文/rt）.line.done | `#8a7b66` | `#f7f3df` | 3.69:1 | ≥3 | 达标 |
| 当前句底 vs 未读底（区分度，非文字） | `#f7cd67` | `#f7f3df` | 1.36:1 | — | 参考：靠色相区分 |
| 当前句左条 vs 当前句底（非文字） | `#0b6e66` | `#f7cd67` | 4.04:1 | ≥3 | 达标（原 `#0e8a7f` 2.80 不达标，已改） |
| 当前句左条 vs 卡片底（非文字） | `#0b6e66` | `#f7f3df` | 5.48:1 | ≥3 | 达标 |
| 播放键 文字/图标（常态） | `#2f2112` | `#19c8b9` | 7.43:1 | ≥4.5 | 达标 |
| 播放键 文字/图标（播放中/hover） | `#2f2112` | `#11a89b` | 5.27:1 | ≥4.5 | 达标 |
| （对照）白字 on `#19c8b9` —— 未采用 | `#ffffff` | `#19c8b9` | 2.10:1 | ≥4.5 | 不达标，故不用 |
| 应用标题/文章标题 | `#794f27` | `#f8f8f0` | 6.65:1 | ≥4.5 | 达标 |
| 应用标题 on 波点色 | `#794f27` | `#efe9d6` | 5.84:1 | ≥4.5 | 达标 |
| 副字/状态行/字段标签 | `#6b5640` | `#f8f8f0` | 6.49:1 | ≥4.5 | 达标 |
| 副字 on 波点色 | `#6b5640` | `#efe9d6` | 5.71:1 | ≥4.5 | 达标 |
| 假名/译/选文 开（文字） | `#0b5f58` | `#e6f9f6` | 6.89:1 | ≥4.5 | 达标 |
| 假名/译 关（文字） | `#6b5640` | `#fffbe7` | 6.67:1 | ≥4.5 | 达标 |
| 横屏页头键焦点文字 | `#0b5f58` | `#fff0b8` | 6.59:1 | ≥4.5 | 达标 |
| 下拉框文字 | `#4a3823` | `#fffbe7` | 10.74:1 | ≥4.5 | 达标 |
| 选文列表项 标题 / 副行 | `#4a3823` / `#6b5640` | `#fffbe7` | 10.74 / 6.67:1 | ≥4.5 | 达标 |
| 选文 当前项 标题 / 副行 | `#4a3823` / `#6b5640` | `#e6f9f6` | 10.23 / 6.35:1 | ≥4.5 | 达标 |
| 选文 焦点项 标题 / 副行 | `#4a3823` / `#6b5640` | `#fff0b8` | 9.79 / 6.07:1 | ≥4.5 | 达标 |
| 面板错误提示 | `#a8382f` | `#f7f3df` | 5.76:1 | ≥4.5 | 达标 |
| 面板标题 | `#794f27` | `#f7f3df` | 6.36:1 | ≥4.5 | 达标 |
| 说明段 .note | `#6b5640` | `#f8f8f0` | 6.49:1 | ≥4.5 | 达标 |
| 焦点描边 vs 页底 / 黄光环 / 播放键底 / 卡片底 | `#3b2a17` | `#f8f8f0` / `#ffcc00` / `#19c8b9` / `#f7f3df` | 12.86 / 9.08 / 6.54 / 12.32:1 | ≥3 | 达标 |

逐项说明：
- **已读句**：日语、译文、rt 一律 `#8a7b66`（animal text-muted 原值），3.69:1 ≥ 3:1，明显弱于未读（10.0:1）但可读。
- **ruby**：字号沿用 `.5em`，颜色 6.22:1（≥4.5），字重 400→500。
- **焦点环**：保留 v3 结构与宽度（描边 5/7px＋黄光环 12/16px；滚动区/横屏卡片内收），只把冷深蓝描边换成暖深棕 `#3b2a17`、光环 `#ffc800`→`#ffcc00`。理由：animal 硬规则禁止冷蓝焦点环；暖深棕对各底色对比均 ≥6.5:1，遥控下仍醒目（截图 05、06 实证）。播放键聚焦时保留 3D 底边并叠加黄光环。
- **译文模糊**：`.tr.blur { filter: blur(6px) }` 原样保留（只给 filter 过渡加了 animal 缓动），模糊后仍见薄荷竖条与文字块轮廓，可辨"有译文但被模糊"。

## 3. v3 → v4 diff 概要（证明 JS 零改动）

`diff player_v3.html player_v4.html`：删 52 行、增 127 行。按段比对（`tools/sections.js`，逐字节比较）：

| 段 | 结果 | v3 行数 | v4 行数 |
|---|---|---|---|
| 头注释区 `<!-- … -->`＋`<title>` | 改 | 147 | 179 |
| `<style>` | 改 | 257 | 300 |
| `</style>`→`<body>` 之间 | **相同** | 3 | 3 |
| `<body>` 模板区（到 `<script>` 前） | 改 1 行 | 112 | 112 |
| **内联 `<script>`** | **逐字节相同** | 1004 | 1004 |
| 尾部 `</script></body></html>` | **相同** | 4 | 4 |

**结构区改动（仅 1 行）**：标题行可见文字 `v3`→`v4`（`<span class="t-sub">… v4</span>`）。没有改 id、class、`v-show`、`v-if`、`:tabindex`、`@click`，没有加或删元素，焦点序与可见性逻辑不变。

**头注释区**：追加「【九、v4 视觉主题（2026-11-20…）】」一节，体例与【一】–【八】一致；静态 `<title>` 改为 v4。注意：JS 内动态 `document.title` 仍写「日语朗读页 v3」，localStorage 键仍为 `ttsv3.*`，数据口仍为 `/v3/t`、`/v3/articles`。这些都在 JS 里，按"JS 零改动"原则保留，与 v3 共用偏好与数据，属预期。

**样式区改动**（全部为 CSS）：
1. `:root`：新增 `--animal-*` 令牌与语义变量，删除 `--brand` 与 `--brand-dark`（映射见 §1）。
2. `body`：背景加糖条＋波点，字体链首加 Nunito 和圆体，字重 500。
3. 原硬编码色全部换成变量：`.picker`、`.pick-item`、`.card`、`.reader`、`.tog`、`rt`、`.tr`、`.line.*`、`select`、`.ghost`、`.note`、`.picker-msg.err`、焦点底。
4. 圆角：交互元素一律 pill（50px），卡片和面板 22/24px，列表项 18/22px。
5. 播放键：底色改主色，深棕字；加 3D 底边阴影（静 5／悬 6／按 1px，电视 7／8／1px）和配套 `margin-bottom`，聚焦时保留底边。
6. 交互态：新增 hover 边色、`.line.done rt`、`.line.cur .tr/rt` 着色，当前项左侧加 inset 薄荷条，`audio` 播放条底色入主题。
7. 过渡：统一 animal 缓动，加 `background-color` 和 `border-color` 过渡（0.15s）。
8. 未改：所有 `@media` 断点与条件（含 P4d 的 `SPLIT_Q` 同条件）、字号体系（`--fs-card` / `--fs-list` / clamp 值）、`--card-h` / `--list-h`、`display` / `flex` / 宽高规则、`prefers-reduced-motion`。改了的布局量只有：几处按钮 `padding` 左右 +2–4px（pill 化补偿），以及播放键 `margin-bottom` 5/7px（给 3D 底边留位）。后者位于 `.controls` 内，P4c/P4d 运行时按实际几何量测，会自动计入。

## 4. 部署与验证结果

| 项 | 结果 |
|---|---|
| 备份 | `tts_web.py.bak-20261120-v4` 存在（上一轮所做；与现 `tts_web.py` 的 diff 恰为新增 `handle_v4` 一个函数＋`add_get("/v4", handle_v4)` 一行，其余路由与后端逻辑未动） |
| `tts_web.py` diff | 仅新增 /v4 路由一处（照 `handle_v3` 镜像，读 `player_v4.html`，`no-store`） |
| 部署 | `player_v4.html` 已复制到 `tts-web/`，工作区与部署副本 md5 一致 `c31ffd23…` |
| **服务重启** | **未完成**：`systemctl restart tts-web.service` → `Access denied`；`sudo` 需批准，本会话不可用。服务仍运行 23:50 启动的进程 |
| `node --check`（内联 JS 抽出 `tools/v4-inline.js`） | 通过 |
| v3 / v2 md5 | v3 `7f14dc1df051fa36acebd13571eb5191`、v2 `87ba5bc1aa0e58ad6e3f524810ef018e`：本轮开始与结束时一致；mtime 分别为 00:13 / 11-16，均早于 v4 工作，未被触碰 |
| curl 8791 `/v3` / `/health` | 200 / 200 |
| curl 8791 `/v4` | **404**（待重启；路由代码已在） |
| curl 预览口 8796 `/v4` | 200；含 `--animal-primary`；`--brand`、`#1f5a99`、`#163f6b` 命中 0；外部字体/资源/`<link>` 命中 0 |

功能自查（代码层依据）：
- **选文开合**：`#pickBtn` / `#pickClose` / `#picker` 的 id、`v-show="pickOpen"`、`:style` 高度绑定都未改；`.picker` 只改了色、边、圆角。截图 04/05 中开合正常，打开时整幅替代两栏，关闭后回到朗读区。
- **选中切换**：`.pick-item` 的 `@click="choose(...)"`、`:class current`、`data-sid` 都未改；只改了色和圆角，另加 inset 条（`box-shadow`，不占位）。
- **播放键**：`#play` 的结构和 `autofocus` 未改；`.is-playing` / `aria-busy` 状态样式保留，只换了色值。截图 03 显示暂停态，06 显示播完的"重新播放"态并聚焦。
- **假名/译开关**：`#kanaBtn`、`#trBtn` 的 `aria-pressed` 绑定和点击都未改；样式选择器仍是 `.tog[aria-pressed=…]`。
- **横竖屏两套规格**：所有 `@media` 条件与 v3 一字不差。截图 01/06（390 宽竖屏上下排）和 03/04/05（1920×1080 横屏分栏，≥1280 电视字号 40–56px 卡片、18–22px 右栏）都按 v3 规格渲染。

## 5. 浏览器截图自检

工具：open-computer-use 操作本机 Chromium（DevTools 设备模拟），用 DevTools「Capture screenshot」保存 PNG，DPR 2。为写入 `shots/`，把 Chromium 下载设置临时改为「每次询问保存位置」，结束后已恢复（见 §7）。页面状态（选句、开选文、聚焦）通过 DevTools Console 执行等价于用户操作的 `.click()` / `.focus()` 设置，未改页面代码。

| 文件 | 视口（CSS px） | 内容 |
|---|---|---|
| `shots/01-portrait-390x844-reading.png` | 390×844（实际像素 780×1688） | 竖屏首屏，开播前 |
| `shots/02-portrait-390x844-playing.png` | 390×844 | ⚠️ 作废参考：误触发模拟双指缩放（`visualViewport.scale=1.42`），画面被放大裁切，**不是页面溢出**（`scrollWidth=390`）。保留仅作记录，以 06 为准 |
| `shots/03-landscape-1920x1080-playing.png` | 1920×1080（3840×2160） | 横屏电视规格，播放中，当前句高亮 |
| `shots/04-landscape-1920x1080-picker-open.png` | 1920×1080 | 选文面板展开 |
| `shots/05-landscape-1920x1080-picker-focus.png` | 1920×1080 | 选文面板＋第 2 项键盘焦点（DevTools「Emulate a focused page」） |
| `shots/06-portrait-390x844-fullpage-playfocus.png` | 390×1285 全页 | 竖屏整页，播放键聚焦（播完态） |

逐项结论：
- **正文清晰度**：深棕 `#4a3823` 在奶油底上清楚，不发虚；卡片日语 600 字重，大字醒目；ruby 小字可辨。
- **当前句高亮**：app-yellow 底配深棕字，在全文列表中一眼可见（03、06）。加深后的左侧薄荷条也清楚。已读句明显变淡但可读。
- **播放键**：薄荷底配深棕字和图标，清楚；3D 底边阴影可见（03/06）；播放中为深一档的 `#11a89b`。
- **选文面板**：薄荷描边，奶油底，虚线分隔头部；当前项为浅薄荷底加左侧薄荷条，其余项为输入底色 `#fffbe7`，层次清楚。
- **焦点环**：选文焦点项为深棕 5px 内收描边加浅黄底（05），播放键为深棕描边加 12px 黄光环（06），都一眼可见。
- **装饰侵入**：波点只出现在页边和控件之间的空白；卡片、全文区、面板底色纯净平整；糖条只占页顶 6px。
- **横屏大字号**：≥1280 规格成立（卡片约 40px 级、右栏 18–22px、页头键 64px 高、播放键 104px 高）。

据截图修改与复验：
1. **当前句左条对比不足**（自检加核算发现）：`#0e8a7f` 对高亮黄仅 2.80:1。高亮黄与卡片底的亮度比只有 1.36:1，主要靠色相区分，所以亮度线索必须由左条独立达到 3:1。已把 `--accent-deep` 改为 `#0b6e66`（4.04:1 / 对卡片 5.48:1）。该变量同时用于 hover 边色，无副作用。注意：03/05/06 截于修改前，色差肉眼几乎不可辨，复验靠核算表。修改后 `/v4` 经 curl 确认已生效（md5 `c31ffd23…`）。

未改、仅记录：
- **竖屏播放键聚焦光环被视口左右裁掉少许**（06）：v3 原样行为（`scale(1.03)`＋12px 光环作用于整宽按钮），不是 v4 引入的，焦点仍清晰可辨。按"行为与焦点样式结构不改"原则未动。
- **"暂停"图标显示为方框**（03/04/05）：图标字符由（未改动的）JS 写入，本机字体缺这个字形，v3 在本机同样如此；webOS 和手机字体通常有该字形。不属视觉层改动范围。

## 6. 已知取舍

- 当前句底与未读底的**亮度**比只有 1.36:1：为保留 animal 的 app-yellow 暖色，区分主要靠色相（黄 vs 奶油）加深色左条（≥4:1）加字色加深。截图中区分明确。对色觉异常用户，左条是主要线索。
- 正文字色没有用 animal 原值 `#725d42`（在 `#f7f3df` 上约 5.6:1），改为更深的 `#4a3823`（10:1）：电视远距离阅读优先。标题保留 animal 原色 `#794f27`。
- Nunito 和圆体只写名字、不加载，webOS 上多半回退到原系统字体，圆体观感取决于设备。这是"不得新增外部请求"的硬约束换来的。
- animal 规范要求"不用原生 select / button"，本页**保留原生控件**：形态不变的硬约束优先，只换皮。
- 图标沿用原 Unicode 字符（▶ ⏸ ☰ ×），未引入 naive-icons，原因同上。

## 7. 环境变更与清理

- 临时预览代理 `tools/preview_proxy.js`（node，127.0.0.1:8796，只读 /v4、其余转发）：截图完已停止。
- Chromium 设置「下载前询问保存位置」：为保存截图而临时打开，结束时已关闭。DevTools 设备模拟和「Emulate a focused page」只作用于 DevTools 会话。
- 截图过程中有一次 POST 开播会话（选句即开播），属页面正常功能，未改任何数据。
- 未改动：默认页指向、v2/v3 本体、tokens、数据库；未同步 ddPC；未提交 git。

## 8. 回滚方法

1. 删除部署页：`rm /data/workspace/tts-web/player_v4.html`
2. 恢复后端：`cp /data/workspace/tts-web/tts_web.py.bak-20261120-v4 /data/workspace/tts-web/tts_web.py`（即删掉 `handle_v4` 与 `/v4` 路由）
3. `sudo systemctl restart tts-web.service`，验活：`curl http://127.0.0.1:8791/health` → 200，`/v3` → 200，`/v4` → 404

（当前服务从未加载过 /v4，所以在 operator 重启之前，只做第 1、2 步也等于已回滚。）

## 9. 待 operator 处理

- `sudo systemctl restart tts-web.service`，然后验证 `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8791/v4` 返回 200。
