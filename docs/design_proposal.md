# 日语朗读页改版 + 文章库迁移 PostgreSQL —— 设计方案

> 版本：v1（2026-11-18）· 范围：只出设计，不含实现代码；未改动任何现有服务与素材文件。
> 依据：本目录 `tts_web.py`（25,662 B）、`player_v2.html`（39,363 B）、`TASK-PLAYER-V2.md`、`task_design.md` 中的现状事实。
> **资料边界说明**：本轮会话里联网读取（WebFetch）和访问 `/data/workspace/tts-web/` 都没有获得权限，所以 **cokice/japanese-analyzer 的部分依据的是我先前对该仓库的了解，本轮没有重新核对源码**。文中凡涉及该仓库的说法都标了「〔待核〕」，需要在 P0 期打开仓库确认，确认结果不影响推荐路线（推荐路线不依赖它的运行时）。

---

> **修订注记 10（2026-11-18，用户确认·定版）**：**待拍板清单 38 条全部按推荐口径通过**，清单逐条状态已标「已确认」（作废条目 9、17、21、30 保持作废）。方案至此定版；**实施尚未开始，等用户下一步指示**。
> **修订注记 11（2026-11-18，用户追加决定）**：**P4 前端口径变更——前端换 Vue，后端继续 Python**。① TtsWeb 后端继续用 Python（现行 tts_web.py 路线），不做 .NET 重构；P2a 照常推进。② P4 播放页 v3 的前端改用 **Vue 3**：无构建链，Vue 3 用自托管全局包（vue.global.prod.js）放进 TtsWeb 静态目录（与自托管字体、hls.js 同一路子），页面仍由 TtsWeb 直出静态文件，ddPC 侧不引入 Node 构建。③ **播放内核照 player_v2 原样移植、不重写**：HLS 会话流程（POST /play 建会话、m3u8 追加、ENDLIST）、webOS 结尾看门狗判定、桌面 Chrome 走 hls.js 分支、iOS 自动播放被拦时的点按兜底、?s= 与 ?text= 入口、标题与保存者显示，逻辑一条不能丢；Vue 只接管句子列表渲染、逐句高亮、状态显示等声明式部分。④ 上线照 v2 先例：v3 先挂并行路径，手机与桌面验过、最后过电视 webOS 真机，全过才切默认页，v2 保留作回退。此决定不影响 P2a 的 preview/commit 契约与 reading 库 DDL。
> **勘误（2026-11-18，P0 核对）**：2.1 节 japanese-analyzer 的默认模型应为 **DeepSeek（deepseek-flash）**，Gemini 只是可选配置，原文写反，特此更正；不影响任何结论与口径。
> **修订注记 9（2026-11-18，用户拍板追加）**：新增第 37 条（`reading` 导出备份：每周 pg_dump、本机与 ddPC 各留 8 份、恢复手順与对账）与第 38 条（本机 PG 单向镜像：DDL 同构、经 dbapi 行级 pull 同步、只读冷备定位、不做在线晋升），正文新增第 9 节展开两条的设计与分工。
> **修订注记 8（2026-11-18，用户拍板·定版）**：**采用第 8 节混合方案为定版路线**——数据层/页面/读取链路保留、入库接口收缩为 preview＋commit、翻译固定走 ddPC Claude Code 驻地、时间轴推迟，总量约 **4.75 人日**。文末待拍板清单已按 8.4 落定：作废 3 条（17、21、30，保留占位）、改写 17 条、新增第 32–36 条（原候选 N1–N5）；第 7 节保留为全量设计稿（节首有指针注记）。
> **修订注记 7（2026-11-18）**：新增第 8 节「复杂度复核与简化建议」（只做评估）：推荐混合方案，入库接口收缩为 preview 加 commit，翻译固定交给 ddPC Claude Code 驻地，时间轴推迟；受影响的待拍板条目在 8.4 点名，**文末清单等用户拍板后再改**。
> **修订注记 6（2026-11-18）**：双语现成稿情形（与修订注记 4 ② 同一条用户要求）按细化意见补强 7.1.3：明写 `tr_by` 与译文来源的区别、对齐错误专码（`ALIGN_TR_MISSING`/`ALIGN_TR_ORPHAN`，已补进 7.3.4）、合并/拆分的落库与播放粒度、译文有误走「标注上报＋revise」的路径；7.5 提纲与待拍板第 31 条同步扩充。无结论变化。
> **修订注记 5（2026-11-18，用户决定重申＋落点强化）**：按核对意见把「全篇上下文翻译」规矩补强到明处——7.1.1 第 1 步明写 draft 一次返回全篇、不分页；第 2 步标准姿势为整批回填；7.5 提纲新增独立的「翻译规矩」一节（原第 7–13 项顺延为第 8–14 项）。内容与修订注记 4 ① 一致，无结论变化。
> **修订注记 4（2026-11-18，用户补充）**：两条入库规矩入册——① 两步型 draft **一次返回全篇句子**，agent 须通读全篇、按上下文统一翻译，整篇译文**一次整批回填**为标准姿势，禁止逐句孤立翻译（7.1.1、7.5）；② 材料自带原文＋译文时，agent **只做分句对齐、不重新翻译**，新增 7.1.3、`tr_via` 取值 `provided` 与待拍板第 31 条。
> **修订注记 3（2026-11-18）**：新增第 7 节「Agent 入库接口设计」（面向 A001/A002/A003 的入库 API、令牌、幂等、错误码、`/ingest.md`），待拍板续编为第 17–30 条；既有章节的结论不变。
> **修订注记 2（2026-11-18，用户决定）**：**没有 Claude API**——原第 4 条「77B worker 直调 Claude API」不成立。翻译改为 Agent 在入库时当场完成（Agent 本人逐句翻，或调用 Claude Code 无头 / 本机 agy 的 Gemini），第 6 条的无人值守 worker 流水线随之取消，解析与翻译并入入库动作。详见 2.2 与待拍板第 4、6 条。
> **修订注记（2026-11-18，用户决定）**：**词性色下划线不做了**——当前句卡片去掉词性配色下划线，保留原文＋ruby 假名注音＋句下中文译文；分词数据（tokens，含词性）仍入库备用，页面不按词性上色/划线。待拍板第 9 条（词性配色表）随之作废，第 8 条删去词性色一项。其余结论不变。

## 0. 一页纸摘要

**核心结论**

1. **高亮粒度和分段粒度不是一回事**。现行 player_v2 按「句」高亮，但当前句是前端用「字数加权 × 总时长」**估出来的**，并没有真实时间戳。tts_web.py 里有三层切分，都和句子不对齐：
   - 合成层：句子打包成 ≤2000 字的段，2000 字以内的文章只发一次 WSS 请求；
   - HLS 层：ffmpeg 按 **3 秒固定时长**切 TS 片段，和句界无关；
   - 服务端切句正则 `SENTENCE_RE` 与前端 `SPLIT_RE` 也不一致。

   **推荐的对齐方案**：服务端已经向 Edge 请求了 `sentenceBoundaryEnabled: true`，只是把返回的元数据帧丢掉了。改为收集这些帧（Offset/Duration，单位 100ns），写成会话目录下的 `timeline.json`，页面随 m3u8 一起轮询，再用字符偏移映射到库里的句子序号，得到**真实的逐句时间轴**。现行的字数加权估算保留，作为降级方案。
2. **页面布局**：推荐**同页上下分区**。上方固定一张「当前句卡片」，内容依次是原文、ruby 注音，下面是中文译文（分词数据仍入库备用，页面不按词性上色/划线）。下方是**缩小字号的全文滚动列表**，当前句高亮，点一句就跳到这一句。不做模式切换。电视大屏也用同样的结构，只放大字号。
3. **入库路线**：推荐 **(b)+(c) 混合**。分词、读音、词性用本地 **fugashi + UniDic** 生成，结果确定、免费、可离线；译文和读音校对由 **Claude Agent 在入库时一次完成**。Agent 不可用时只存分词、译文留空，页面照常播放，只是不显示译文。**不复用 japanese-analyzer 的运行时 LLM 解析**，只借用它的数据形状和展示样式。
4. **表结构**：新建 schema **`reading`**，包含以下几张表：
   - `articles`：文章，保留 10 位十六进制 `share_id`，旧短链继续可用；
   - `analyses`：解析版本，重跑时生成新版本；
   - `sentences`：分句、译文，分词数据用 **JSONB**；
   - `share_aliases`：短链别名。

   另提供软删除，以及一个「当前版本」视图。
5. **PG 落点**：推荐 **(B) 77B 的 db77database 新 schema，作为唯一真源**。
   - 77B 上的 TtsWeb 走本机连接直连 PG。
   - ddPC 上的 TtsWeb 通过 77B TtsWeb 的 HTTPS 只读 API 取文，本地 SQLite 做**直读缓存**。云机暂停或家里断网时，已缓存的文章照常可读；新写入先进本地待发队列（outbox），恢复后再补推。
6. **迁移**：两边 SQLite 先**原样导入**（保留 share_id、added_by、added_at），随后在同一期里批量补解析（总量十几篇，成本可以忽略）。`/?s=<id>` 和 `/t/<id>` 的地址不变。SQLite 冻结为只读兜底，**保留到 2027-02-28** 后归档。
7. **分期（混合方案定版，见 8.3）**：P0 验证 → P1 建库与迁移 → P2 入库 → P4 页面 v3 → P5 ddPC 接入 → P6 退役 SQLite；P3 时间轴推迟、按需再做。合计约 **4.75 人日**（时间轴补做 +1 人日）。文末清单共 **38 条**，已于 2026-11-18 全部按推荐口径确认通过（含作废占位条目）。

---

## 1. 页面设计

### 1.1 先查清现状：高亮粒度 vs. HLS 分段粒度

| 层 | 位置 | 粒度 | 说明 |
|---|---|---|---|
| 前端高亮 | player_v2 `splitSentences()` / `SPLIT_RE` | **句**（。！？!?… 和换行；收尾的 」』）等并入本句；只剩标点的碎片并入上一句） | 当前句是**估算**的：`target = (currentTime / T) × W`，W 是各句先验秒数（有效字数/cps + 句末停顿 + 逗号×0.2s + 省略号×0.35s）之和；T 由 ENDLIST/duration/#EXTINF 之和/先验估计取得（文件头注释第一节）。误差目标是「不超过一句」 |
| 合成分段 | tts_web.py `split_text()` / `SENTENCE_RE` / `SEGMENT_MAX_CHARS=2000` | **≤2000 字的句包** | 先按句界切，再打包成 ≤2000 字一段，每段单独发一次 WSS 合成，然后顺序拼接成一条 MP3 流。2000 字以内的文章（例如北海道那篇 543 字）**整篇一次合成**。`SENTENCE_RE` 把 ．.；; 也算作句界，但不处理收尾括号，**和前端切法不一致** |
| HLS 分段 | `_run_hls_session()`，`-hls_time 3` | **3 秒固定时长的 TS 片段** | ffmpeg 把 AAC 按时长切片，和句子完全无关：一个片段里常有好几句，长句也会跨好几个片段 |
| Edge 元数据 | `stream_segment()` | 句边界（SentenceBoundary） | `TTSConfig(..., "SentenceBoundary")`，并在 speech.config 里打开了 `sentenceBoundaryEnabled: "true"`，但**接收循环里所有文本帧只用来检测 `turn.end`，元数据被丢弃了** |

**结论**：句子粒度**不是**现有的分段粒度。「一段多句」和「一句多段」两种情况都存在，高亮全靠估算。合成越快、文章越短，误差越小（ENDLIST 出现以后只剩「字数≈时长」这一层近似）。不过遇到含大量假名的长句、数字、引号或对白停顿时，仍会偏差大半句到一句。改版后当前句卡片要同时显示注音和译文，**卡片切早或切晚都会非常显眼**，所以需要真实时间轴。

### 1.2 时间轴对齐方案（推荐）

**数据来源**：Edge 已经在推送句边界。服务端改为收集这些帧，不再丢弃。

1. **采集**：在 `stream_segment()` 的接收循环里，碰到 `Path:audio.metadata` 文本帧就解析 JSON 中的 `Metadata[]`，取 `Type == "SentenceBoundary"` 的 `Data.Offset`、`Data.Duration`（单位 100ns）和 `Data.text.Text`〔字段名需要在 P0 期对 edge-tts 7.3.1 的实际帧核实〕。
2. **跨合成段累加**：文本超过 2000 字时会有多次 WSS 合成，每段的 Offset 从 0 重新计算。累加基准用前面各段的**音频实际时长**：输出格式是 48 kbps 的 CBR MP3，时长 ≈ 字节数 × 8 / 48000 秒。也可以取上一段最后一个边界的 Offset+Duration，再加尾部静音估计。推荐用字节数，结果确定。
3. **映射到库里的句子**：服务端在 `/play` 时如果拿到的是**文章 ID**（见 1.6），就知道每句在全文里的字符区间 `[char_start, char_end)`。把 Edge 返回的边界文本依次在全文中向后查找（只向前推进的游标匹配），得到它的起始字符偏移，再落到包含这个偏移的库内句子上。这一句的开始时间取第一个落入它的边界的 Offset，结束时间取最后一个落入它的边界的 Offset+Duration。
   - Edge 自己的句切分和库里的分句不一定一致：Edge 可能把一句拆成两条边界，也可能把两句合成一条。上面「以字符偏移为准、多对一合并」的做法能吸收这两种差异。
   - 如果 Edge 合并了两句，就把这条边界的时长**按两句的先验权重**（沿用 player_v2 的 `weightOf`）拆开，保证每句都有起止时间。
4. **输出**：在会话目录写 `/hls/<sid>/timeline.json`，每拿到一批边界就整体覆盖写一次（先写临时文件再 rename，保证读到的总是完整文件）。格式如下：
   ```json
   {"v":1, "done":false, "sentences":[{"seq":0,"t0":0.12,"t1":2.86}, {"seq":1,"t0":3.31,"t1":6.02}]}
   ```
   合成结束时 `done:true`。`handle_hls_file` 的白名单正则需要加上 `timeline\.json`。
5. **前端定位**：沿用现有的 1.5 秒 m3u8 轮询，同时拉取 timeline.json。在 `timeupdate` 时用二分法找满足 `t0 ≤ t` 的最大 seq，句间停顿归前一句。只要 `t` 落在已知时间轴的范围内，就用精确值；落在范围外（合成还没赶到，极少见）就退回现行的字数加权估算。
6. **时间基准**：
   - hls.js 用 `startPosition:0`，`currentTime` 从 0 开始，可以直接对照。
   - 原生 HLS（Safari/iOS/webOS）在直播列表下，`seekable.start(0)` 可能不为 0。统一用 `t_rel = currentTime − 会话起点` 对照，会话起点在首次 `playing` 时记录（复用 `rewindOnce` 的时机）。
   - AAC 编码器的 priming 只有约 20–50 ms，可以忽略；如需补偿，在前端加一个常量 `OFFSET_MS`，P3 期实测后定值。
7. **降级链**：精确时间轴 → 现行字数加权估算（player_v2 算法原样保留）→ 只按 ENDLIST 后的比例估算。任何一环失败都**只影响精度，不影响播放**。
8. **数据侧需要的时间信息**：库里**不必**存时间，因为时间取决于 voice 和 rate，每次播放都能实时拿到。库里只需要保证每句有 `seq`、`char_start`、`char_end`，并且全文严格等于各句按顺序拼接。可选的 `reading.timings` 缓存表见 3.4，推荐暂不建。

> 被否掉的替代方案：
> - **逐句单独合成**：句间会多出 RTT 和静音，还会重新引入拼接点。iOS 的解码问题正是 2026-11-15 把段长放大到 2000 字的原因，见 `SEGMENT_MAX_CHARS` 的注释。
> - **SSML `<bookmark>`**：Edge ReadAloud 通道是否回传 bookmark 不确定，而且需要改 mkssml 的转义链。
> - **按 HLS 片段对齐**：片段固定 3 秒，和语义无关。

### 1.3 布局：同页上下分区（推荐）

不做模式切换，也不用浮动卡片。理由：
- 跟读时视线要同时照顾「当前句细节」和「上下文位置」，两者同屏最自然；
- 浮动卡片在手机上会遮住列表，在电视上会抢遥控焦点；
- 切换模式会多一次操作。

```
┌──────────────────────────── 标题（分享标题）· 第 7 / 32 句 ──┐
│ ┌ 当前句卡片（focus card，固定高度区，内容超出时卡内滚动）─┐ │
│ │   ほっかいどう   みりょくど                                │ │
│ │   北海道 、  魅力度 ランキング 18 年 連続 1 位            │ │  ← ruby 注音
│ │   ────  ─  ─────  ────── ── ─  ──── ──                  │ │
│ │   北海道连续 18 年位居魅力度排行榜第一。                    │ │  ← 译文（中文，较小、深灰）
│ │   [◀ 上一句]  [⟳ 重读本句]  [下一句 ▶]       [假名][译][色] │ │  ← 次级控件
│ └──────────────────────────────────────────────────────────┘ │
│ ┌ 全文列表（小字，可滚动；当前句高亮并自动居中）─────────────┐ │
│ │ 6  ……前一句（已读，淡色）                                  │ │
│ │ 7 ▌北海道、魅力度ランキング18年連続1位。（当前，黄底）      │ │
│ │ 8  次の句……                                               │ │
│ │    （每句下方可选显示一行小号译文：默认关闭）               │ │
│ └──────────────────────────────────────────────────────────┘ │
│ [        ▶ 播放 / ⏸ 暂停（大按钮，保持现有规格）        ]     │
│ 语言 [日语▾]  语速 [80%▾]  [✎ 编辑文本]                       │
│ <audio controls>                                              │
└───────────────────────────────────────────────────────────────┘
```

- **高度分配**：
  - 手机竖屏：卡片约 38vh，列表约 30vh，播放键保持 ≥72px，三者在一屏内放得下；
  - 平板和电视：卡片 40vh，列表 34vh。
- **全文小字**：列表字号相对现行 `--fs-line` 缩小约 30%：
  - 手机 `clamp(15px, 3.8vw, 17px)`，不低于 v2 任务书「正文 ≥17px」里「可读」的底线；全文列表定位为「通览」，可以略小，最小 15px；
  - 电视 `clamp(24px, 1.8vw, 32px)`，3 米外仍然可辨。
  - 「通览」靠缩小字号和行距 1.45 实现，不截断文字。
- **当前句卡片字号**：比现行 v2 的当前行更大，原文和 ruby 合起来的行高约为字号的 2.3 倍：
  - 手机原文 `clamp(22px, 6vw, 28px)`，rt 取原文的 0.5 倍；
  - 电视原文 `clamp(40px, 3vw, 56px)`；
  - 译文取原文的 0.68 倍。
- **列表的行为**：沿用 v2 的「已读变淡、当前黄底加左色条、自动居中、用户滚动后让行 4 秒」。新增**点一句就跳到这一句播放**：用时间轴里该句的 t0 设置 `currentTime`。合成还没赶到时，按钮显示「合成中」并暂不跳转。
- **卡片两个开关（跟读练习用）**：
  - 「假名」：rt 显示或隐藏；
  - 「译」：译文显示、隐藏或模糊，点一下揭开，用于先听后看。

  开关状态存在 localStorage。电视遥控用方向键可以聚焦到这两个开关，沿用 v2 的空间导航 `NAV` 列表并扩充。
- **重读本句**：跳回这一句的 t0 播放，到 t1 时自动暂停（跟读停顿）。是否默认开启「每句后自动停顿 N 秒」列为待拍板第 7 条，推荐先做成可选项，默认关闭。
- **编辑态**：沿用 v2 的 textarea。编辑过的文本属于「自由文本」，没有解析数据，卡片只显示原文（不带注音），译文区显示「（未入库文本，无译文）」，播放和高亮照常。
- **电视 ≥1280px**：结构保持上下排列，不改成左右分栏。原因是遥控上下导航更自然，日文长句横向也需要宽度。内容区限宽 1200px 维持不变。

### 1.4 分词注音的前端渲染

**DOM 结构**（以「買ってきました」为例，一个词一个 `span.tok`）：

```html
<span class="tok" data-i="4">
  <ruby>買<rt>か</rt></ruby>っ
</span><span class="tok">て</span><span class="tok">き</span>…
```

- **只给汉字部分标注假名**。送假名的拆分（「買っ」= 買[か] + っ）在**入库时**算好，存进 `ruby` 段数组（见 2.4 的示例）。前端只负责机械拼装，不做任何语言处理，符合「运行时不解析」的约束。
- 片假名词、纯假名词、数字、符号不加 ruby。数字的读法（例如「18年」读作 じゅうはちねん）也在入库时以段的形式存好，前端照样渲染。
- `rt` 用 `font-size: .5em; line-height: 1`，`ruby-position: over`。webOS 等老内核不支持 ruby 时，降级为括号形式，由一个 `@supports not (display: ruby)` 分支用 `rt::before/after` 加括号。
- **词性配色下划线：不做**（用户 2026-11-18 决定取消，见文首修订注记）。tokens 中的 `pos`/`pd` 仍随分词入库备用（将来做点词看释义等功能可直接取用），但页面不按词性上色、不划下划线；分词边界不在页面上做视觉标记，也不插入空格，以免破坏日文的阅读节奏。
- **长句换行**：
  - 每个 `.tok` 设 `white-space: nowrap`，**只在词与词之间换行**，避免出现「買」和「っ」被拆到两行、ruby 被截断的情况；
  - 容器设 `word-break: keep-all; overflow-wrap: anywhere`，超长的单个词（URL、长片假名）作为例外，允许在词内断开；
  - 卡片设 `max-height` 加卡内滚动；超过 3 行时字号自动降一档，用一个 CSS 类 `.long` 实现，入库时按字数预标 `len_class`，前端零计算。
- **译文**放在原文下方，左侧加细色条，字号 0.68 倍。「译」开关关闭时用 `filter: blur(6px)` 模糊，点一下或按确定键揭开。

### 1.5 与现有功能的兼容（不得丢失）

v2 的全部功能都要保留：两种传参（`?text=`、`?s=`）、静默自动播放、大播放键的状态机、edit/返回跟读、遥控空间导航、媒体键、webOS 结束看门狗、原生 HLS 起播纠正、桌面 Chrome 走 hls.js。新页面以 `player_v3.html` 的形式，先挂在 `/v3` 上和 v2 并行试用，验收后再转正（同 2026-11-15 v2 转正的做法）。

### 1.6 前后端接口增量（设计层面）

| 接口 | 现状 | 改版后 |
|---|---|---|
| `GET /t/<id>` | `{text, lang, rate, title?, added_by?, added_at?}` | 原有字段**原样保留**。新增 `article_id`、`version`，以及在 `?with=sentences` 时返回的 `sentences:[{seq, text, char_start, char_end, tr, toks, len_class}]`。旧页面忽略新字段，不受影响 |
| `POST /play` | `{text, lang, rate}` | 新增可选 `{s: <share_id>}`：服务端按库里**当前版本的句子**拼出全文去合成，因此知道句子区间、能生成 timeline.json。仍然接受 `{text}`（自由文本走估算降级）。返回值新增 `timeline: "/hls/<sid>/timeline.json"` |
| `GET /hls/<sid>/timeline.json` | 无 | 新增，见 1.2 |
| `POST /share` | 公开、无鉴权 | 写入 PG `reading.articles`，状态为 `raw`，并触发解析队列。**建议加写入令牌**（待拍板第 13 条），因为 77B 的地址是公网 |

---

## 2. 入库流程

### 2.1 三条路线评估

**(a) 入库时调用 japanese-analyzer 的解析能力**
- 它的解析从哪里来〔待核〕：据我了解，它是一个 Next.js 前端应用，**解析本身靠运行时调用 LLM**（默认 Google Gemini，可配置兼容 OpenAI 的接口），用提示词让模型返回 JSON：词、词性、假名、罗马字，另有逐词释义和整句翻译。它**没有内置的离线分词引擎**。
- 能否离线复用：不能。所谓「复用」，实际只是复用它的提示词和 JSON 形状，调用的仍然是在线 LLM，本质上等于路线 (c)，还多了一层对它的 API 路由和 Next.js 运行环境的依赖。
- 准确度：取决于模型，分词边界和词性标签不稳定（同一句跑两次可能结果不同）。
- 成本与维护：需要 Gemini key、需要部署一套 Node 应用，还要跟进上游变更。
- 结论：**不采用它的运行时**。它的价值在于**展示样式**（ruby、点词看释义）和**数据形状**，这两点已经吸收进本方案的 JSON（见 2.4）。

**(b) 本地分词工具做分词注音 + Agent/人工翻译**
- 工具对比：

| 工具 | 生态 | 读音和词性质量 | 部署 |
|---|---|---|---|
| **fugashi + UniDic**（unidic-lite 约 50MB，或完整 unidic 约 1GB） | Python，MeCab 绑定 | 学术标准，词性体系细致（大類/中類/活用型/活用形/語彙素/発音形） | `pip` 即可，Linux 和 Windows 都能用 |
| SudachiPy + SudachiDict | Python/Rust | 三档切分（A/B/C），对复合词友好，读音质量与 UniDic 相当 | `pip`，词典 core 约 70MB |
| kuromoji(.js) | JS/Java | IPADIC，较老，新词弱 | 适合放在浏览器端，但违反「运行时不解析」 |

- 准确度：分词和词性约 97–99%。读音的主要错误集中在**同形多读**（今日 きょう/こんにち、方 かた/ほう、何 なに/なん、上手 じょうず/うわて）、**人名地名**、**数词加量词的音变**（一本 いっぽん、18年 じゅうはちねん），以及**口语缩约**（〜てる、〜ちゃう）。这些恰好是 LLM 校对最擅长修正的地方。
- 成本：零。依赖一个 pip 包加词典。维护：词典版本固定之后结果完全确定，便于重跑和比较差异。
- 翻译：必须有人或 Agent 来做。

**(c) 全部由 Agent（Claude）入库时一次生成**
- 准确度：译文自然度最高，能结合上下文（例如逃げ恥的对白语气、省略的主语）。读音大多正确。但**分词边界和词性标签缺乏一致性**，偶尔还会出现「原文被悄悄改写」（全角半角、标点）导致句子拼接后对不上原文的情况，必须加校验。
- 成本：一篇 500 字的文章约 1–2 万 token 的会话用量；经 Claude Code / agy 的订阅会话执行，无 API 按量费用。
- 依赖：需要可用的 Agent 会话（Claude Code 无头或本机 agy/Gemini）；**没有 Claude API key 这条路**（用户 2026-11-18 确认）。Agent 不可用时翻译环节停摆，但分词骨架与朗读不受影响。

### 2.2 推荐：(b) 打骨架 + (c) 译文与校对（混合）

```
POST /share 或 Agent 直写 ─▶ articles(status=raw)
        │
        ▼  入库动作由 Agent 当场执行（无 worker、无定时扫描）：Agent 跑入库脚本 ingest.py 完成 ①–⑥ 后落库
 ① 规范化：NFKC 只处理半角片假名和全角英数（保留原标点）；记录 source_text
 ② 分句：沿用并统一 player_v2 的 SPLIT_RE 规则（。！？!?…＋换行；收尾括号并入；
          「」内部的。不切——这是 v2 没处理的点）→ seq/char_start/char_end
 ③ 分词注音：fugashi+UniDic → 每词 surface/lemma/pos/pos_detail/reading(片假名→平假名)/
          conj，再按汉字与假名的对齐算出 ruby 段（送假名拆分）
 ④ Agent 翻译（整篇一次处理，按句编号；执行者见下方「翻译由谁执行」）：
          输入 = 句子 + ③ 的分词结果；
          输出 = 每句中文译文 + 读音修正清单 [{seq, tok_i, reading}] + 可选的词释义 gloss
          约束：不得修改原文与分词边界，只能改 reading/gloss；返回 JSON，用 schema 校验
 ⑤ 校验：句子拼接 == 原文；每个 token 的 surface 拼接 == 句子；读音只含平假名；
          修正过的 token 打上 fixed_by="agent"
 ⑥ 写入新的 analyses 版本 → 事务内切换 articles.current_version → status=ready
```

- **兜底**：
  - ④ 失败（Agent 不可用或输出校验失败），就**只落 ③ 的结果**，`status=partial`、`tr=null`，页面卡片显示注音，译文区显示「译文待补」。不自动重试；下次 Agent 处理该文章时补齐，或人工补。
  - ③ 失败（极少见，例如字符异常），就只分句，`toks=null`，卡片只显示原文。
  - **任何情况下都不阻塞朗读**：status 为 raw 的文章照样能播，因为播放只需要全文。
- **人工修订**：允许人工或 Agent 直接修改某句的 `tr` 或某个 token 的读音，写入时生成新版本（`kind=manual`），不覆盖旧版本。
- **翻译由谁执行（用户 2026-11-18 定）**：没有 Claude API，不存在 worker 直调 API 的路线。翻译是**入库动作的一部分**，由 Agent 当场完成：(i) Agent（A001/A003）本人逐句翻译；或 (ii) Agent 在入库时调用 Claude Code（无头、订阅会话）或本机 agy（Gemini，已登录）生成。由 Agent 按当时情况二选一，写入的数据格式相同；读音校对由执行翻译的一方一并做。译文风格按待拍板第 5 条（自然意译）。
- **为什么不选纯 (c)**：分词和词性需要确定性和一致性，才能保证分词结果一致、重跑时差异可读，fugashi 正好提供这些。LLM 只做它擅长的两件事：翻译和消歧。

### 2.3 分句规则（入库侧唯一标准）

- 入库时切出的句子是**唯一真源**。播放时服务端用库里的句子拼出全文（`/play {s:…}`），前端也直接使用 `sentences[]`，**不再在前端切句**，从根本上消除 `SENTENCE_RE` 和 `SPLIT_RE` 不一致的问题。
- 自由文本（编辑过、未入库的文本）仍然走 v2 前端的切句和估算。
- 对白类文本（逃げ恥）：一行就是一句，即使一行里有两个「。」也继续按句末符号切开，同时记录 `para`（行号或段落号），便于列表按段落留出间距。

### 2.4 一句的完整示例数据

句子：「スポーツ飲料も買ってきました。」（摘自 player_v2 默认文本）

```json
{
  "seq": 2,
  "para": 2,
  "text": "スポーツ飲料も買ってきました。",
  "char_start": 17,
  "char_end": 32,
  "len_class": "s",
  "tr": "我还买了运动饮料回来。",
  "tr_by": "agent",
  "toks": [
    {"s": "スポーツ", "l": "スポーツ", "pos": "noun", "pd": "名詞-普通名詞-一般", "r": "すぽーつ", "ruby": null},
    {"s": "飲料", "l": "飲料", "pos": "noun", "pd": "名詞-普通名詞-一般", "r": "いんりょう",
     "ruby": [{"t": "飲料", "r": "いんりょう"}]},
    {"s": "も", "l": "も", "pos": "particle", "pd": "助詞-係助詞", "r": "も", "ruby": null},
    {"s": "買っ", "l": "買う", "pos": "verb", "pd": "動詞-一般", "conj": "五段-ワア行/連用形-促音便",
     "r": "かっ", "ruby": [{"t": "買", "r": "か"}, {"t": "っ"}], "gloss": "买"},
    {"s": "て", "l": "て", "pos": "particle", "pd": "助詞-接続助詞", "r": "て", "ruby": null},
    {"s": "き", "l": "来る", "pos": "verb", "pd": "動詞-非自立可能", "conj": "カ行変格/連用形-一般",
     "r": "き", "ruby": null, "sub": true, "gloss": "（〜てくる：表示动作后回来）"},
    {"s": "まし", "l": "ます", "pos": "aux", "pd": "助動詞", "conj": "助動詞-マス/連用形-一般",
     "r": "まし", "ruby": null},
    {"s": "た", "l": "た", "pos": "aux", "pd": "助動詞", "conj": "助動詞-タ/終止形-一般",
     "r": "た", "ruby": null},
    {"s": "。", "l": "。", "pos": "sym", "pd": "補助記号-句点", "r": "", "ruby": null}
  ],
  "fixes": []
}
```

字段约定：
- `s`：原形表记，按顺序拼接必须等于 text；`l`：词元；`pos`：词性大类枚举（入库备用，页面不按词性着色）；`pd`：UniDic 原始词性；`r`：平假名读音；`ruby`：注音段（null 表示不注音）；`sub`：非自立；`gloss`：可选的中文释义；`fixes`：Agent 修正记录。
- 字段名用短写，可以减小 JSONB 和传输体积：一篇 500 字的文章约 40 句 × 0.8 KB ≈ 32 KB。

---

## 3. 表结构（DDL 草案）

### 3.1 schema 名：`reading`（推荐）

- 按**内容领域**命名，不按程序命名：存放的是「供朗读跟读的文章」。以后如果换掉 TtsWeb，或者让别的程序（例如 Anki 导出、背词工具）来读，名字依然成立。
- 和已有的 `jpdb`（词库）、`drama`（剧集）在语义上区分得开：逃げ恥的对白作为**朗读文章**放进 `reading`，必要时用 `source_ref` 关联到 `drama`。
- 备选：`ondoku`（音読，更贴近日语学习语境，但对中文母语者辨识度一般）、`tts`（绑定了实现，不推荐）。

### 3.2 DDL

```sql
CREATE SCHEMA IF NOT EXISTS reading;

-- 文章：一行就是一篇，share_id 兼容旧短链
CREATE TABLE reading.articles (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  share_id        char(10)    NOT NULL UNIQUE
                  CHECK (share_id ~ '^[0-9a-f]{10}$'),
  title           text        CHECK (char_length(title) <= 120),
  source_text     text        NOT NULL CHECK (char_length(source_text) BETWEEN 1 AND 5000),
  lang            text        NOT NULL DEFAULT 'ja' CHECK (lang IN ('ja','zh','en')),
  rate            text        NOT NULL DEFAULT '-20%' CHECK (rate ~ '^[+-]\d+%$'),
  voice           text,                         -- NULL = 按 lang 取默认音色
  status          text        NOT NULL DEFAULT 'raw'
                  CHECK (status IN ('raw','analyzing','partial','ready','failed')),
  current_version int,                          -- 指向 analyses.version；NULL = 尚未解析
  origin_site     text        NOT NULL DEFAULT '77b' CHECK (origin_site IN ('77b','ddpc','agent','import')),
  source_ref      text,                         -- 出处（URL、剧名话数等）
  tags            text[]      NOT NULL DEFAULT '{}',
  added_by        text        CHECK (char_length(added_by) <= 40),
  added_at        timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  deleted_at      timestamptz,                  -- 软删除
  legacy          jsonb                         -- 迁移时保留原 SQLite 行（含原 added_at 字符串）
);
CREATE INDEX articles_added_at_idx ON reading.articles (added_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX articles_status_idx   ON reading.articles (status) WHERE deleted_at IS NULL AND status <> 'ready';
CREATE INDEX articles_tags_gin     ON reading.articles USING gin (tags);

-- 短链别名：两库合并时发生 ID 冲突，或文章合并后旧 ID 仍要可用
CREATE TABLE reading.share_aliases (
  alias       char(10) PRIMARY KEY CHECK (alias ~ '^[0-9a-f]{10}$'),
  article_id  bigint   NOT NULL REFERENCES reading.articles(id),
  note        text,
  created_at  timestamptz NOT NULL DEFAULT now()
);

-- 解析版本：每次（重新）解析生成一个版本，句子挂在版本下面
CREATE TABLE reading.analyses (
  article_id  bigint   NOT NULL REFERENCES reading.articles(id),
  version     int      NOT NULL,
  kind        text     NOT NULL CHECK (kind IN ('auto','manual','import')),
  status      text     NOT NULL CHECK (status IN ('building','partial','ready','failed','superseded')),
  tool        jsonb    NOT NULL DEFAULT '{}',   -- {"splitter":"v1","tokenizer":"fugashi 1.x/unidic-lite 1.0.8","translator":"agent"}
  text_hash   bytea    NOT NULL,                -- sha256(source_text)，原文变化时据此判定需要重跑
  note        text,
  created_by  text,
  created_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (article_id, version)
);
ALTER TABLE reading.articles
  ADD CONSTRAINT articles_current_fk FOREIGN KEY (id, current_version)
  REFERENCES reading.analyses (article_id, version) DEFERRABLE INITIALLY DEFERRED;

-- 句子
CREATE TABLE reading.sentences (
  article_id  bigint   NOT NULL,
  version     int      NOT NULL,
  seq         int      NOT NULL CHECK (seq >= 0),
  para        int      NOT NULL DEFAULT 0,
  text        text     NOT NULL,
  char_start  int      NOT NULL,
  char_end    int      NOT NULL CHECK (char_end > char_start),
  len_class   char(1)  NOT NULL DEFAULT 'm' CHECK (len_class IN ('s','m','l')),
  tr          text,                             -- 中文译文；NULL = 尚未生成
  tr_by       text,
  toks        jsonb,                            -- 分词注音数组（见 2.4）；NULL = 尚未分词
  kana        text,                             -- 整句平假名读音（用于检索和假名朗读对照）
  lemmas      text[],                           -- 由 toks 抽出的词元（写入时一并计算），用于按词查句
  fixes       jsonb    NOT NULL DEFAULT '[]',
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (article_id, version, seq),
  FOREIGN KEY (article_id, version) REFERENCES reading.analyses (article_id, version) ON DELETE CASCADE
);
CREATE INDEX sentences_lemmas_gin ON reading.sentences USING gin (lemmas);
CREATE INDEX sentences_text_trgm  ON reading.sentences USING gin (text gin_trgm_ops);  -- 需要 pg_trgm 扩展

-- 当前版本视图：业务读取只走这里
CREATE VIEW reading.v_current_sentences AS
SELECT a.id AS article_id, a.share_id, s.*
FROM reading.articles a
JOIN reading.sentences s ON s.article_id = a.id AND s.version = a.current_version
WHERE a.deleted_at IS NULL;

-- 权限：TtsWeb 专用角色，只能读写本 schema
-- CREATE ROLE reading_rw LOGIN ...;  GRANT USAGE ON SCHEMA reading TO reading_rw;
-- GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA reading TO reading_rw;（不授 DELETE，删除一律走软删除）
```

### 3.3 关键取舍

- **分词数据用 JSONB，不拆表、不拆列（推荐）**：
  - 读取模式永远是「整句取出、整句渲染」，没有逐词更新的需求；拆成 tokens 表只会多一次 join 和组装。
  - 规模小（几十篇 × 几十句），JSONB 的 TOAST 压缩之后体积可以忽略。
  - 字段会演进（以后可能加 gloss、音调核 accent），JSONB 不需要迁移结构。
  - JSONB 的短板是按词查询。为此用 `lemmas text[]` 加 GIN 索引补上，「哪些句子用到了 来る」可以直接查。`lemmas` 由写入方从 toks 抽出，避免依赖生成列能否使用 jsonb 函数。
  - 将来真要做全库词频统计，可以随时用 `jsonb_array_elements` 物化成一个视图。
- **版本化**：
  - 句子挂在 `(article_id, version)` 下，重跑解析时写入新版本，在**同一事务**里切换 `articles.current_version`，旧版本标为 `superseded`、不删除。
  - 可以回滚，可以对比两个版本的差异（例如更换模型或词典之后）。
  - 体积很小，**旧版本全部保留**（待拍板第 16 条）。
  - 原文改动（`source_text` 变化）必然触发新版本；`text_hash` 用来判断现有版本是否已经过期。
- **软删除**：`deleted_at` 不为空的文章，在 `/t/<id>` 返回 410（旧页面按 404 处理，表现一致），后台仍可恢复。
- **added_at**：统一用 `timestamptz`。迁移时把原 JST ISO 字符串解析后写入，原值同时保存在 `legacy` 里。
- **lang/rate**：保留在文章上，作为「打开时的默认值」，页面仍然可以临时改动。voice 允许为 NULL，即按 lang 取默认音色，与 `DEFAULT_VOICES` 一致。
- **主键**：内部用 `bigint` 自增，对外和链接一律用 `share_id`。新文章的 share_id 仍然由 `uuid4().hex[:10]` 生成，与现行写法一致，插入冲突时重试。

### 3.4 可选：`reading.timings`（推荐暂不建）

`(article_id, version, voice, rate, seq, t0_ms, t1_ms, measured_at)`，用来缓存实测时间轴，以后可以做到「打开就能点句跳转」，不必等合成。时间轴每次播放都能实时获得，而且合成通常远快于实时，这张表收益有限，等 P4 上线后看实际体验再决定（待拍板第 10 条）。

---

## 4. PG 落点与访问方式

### 4.1 三案对比

| 维度 | (A) 東紫乃本机 PG 16.2 | **(B) 77B db77database 新 schema（推荐）** | (C) ddPC 本地新装 PG |
|---|---|---|---|
| 77B TtsWeb 怎么连 | 跨 VM 网络，需要开放 5432 或隧道，加跨机延迟 | **本机 localhost/unix socket，亚毫秒级** | 77B 访问不到家里的 ddPC（无入站地址），只能靠反向隧道，不现实 |
| ddPC TtsWeb 怎么连 | 家宽到云机，PG 端口不应公网暴露，需要隧道 | 走 77B TtsWeb 的 **HTTPS 只读 API**（现成的公网地址 8791-…instabox.tech），每篇一次请求，延迟约 100–300ms，可以接受；本地 SQLite 缓存 | 本机直连 |
| 云机暂停或断网时 | ddPC 读不到；77B 读不到（取决于两台 VM） | 77B：与服务同生共死，没有额外故障点；ddPC：**已缓存的文章可读**，新写入进 outbox | ddPC 可用；77B 完全不可用 |
| 两地一致性 | 单库，但两边都是远程客户端 | **单库为真源**，ddPC 只是缓存加待发队列 | 单库，但 77B 无法访问，只能双库加同步 |
| 短链兼容 | 需要迁移 | share_id 原样保留，冲突时用别名表 | 同 B |
| 与用户定调的关系 | 违背「以后数据都放 db77database」 | **完全符合**：同库、按 schema 分开 | 违背 |
| 运维成本 | 多一个跨机依赖，还要维护防火墙或隧道 | 复用现有 PG 17.12、现有备份和 dbapi；只新增一个 schema 和一个角色 | Windows 上装 PG、计划任务守护、备份，还要另写同步，成本最高 |

### 4.2 推荐 (B)，以及另一处部署的读取路径与降级

- **77B TtsWeb**：通过 `psycopg` 或 `asyncpg` 本机连接 db77database，使用专用角色 `reading_rw`，`search_path=reading`。**不经过 dbapi**：dbapi 是给人和 Agent 用的管理 API，JWT 会过期，还多一跳，不适合放在服务的热路径上。
- **dbapi 的定位**：Agent（A001/A003）和人工通过它读写 `reading` schema，用于补译文、修读音、批量导入，前提是 dbapi 已有对该 schema 的授权。
- **ddPC TtsWeb 的读取路径**：
  1. `/t/<id>` 请求进来时，先查本地 SQLite 缓存表 `article_cache(share_id, json, version, fetched_at)`；
  2. 缓存未命中，或缓存超过 10 分钟而远端可达，就请求 `https://8791-…instabox.tech/t/<id>?with=sentences`，超时 4 秒；成功后写回缓存；
  3. 远端不可达时，用缓存（过期也用），页面状态栏提示「离线缓存」；
  4. 缓存也没有时，回落到旧的 `shares` 表（迁移前的 ddPC 存量，P6 之前一直保留）。只有原文，没有解析数据，但能播。
- **预热**：ddPC 侧每小时拉一次 `GET /api/articles?since=<ts>`（增量列表加句子），把新文章都缓存下来，保证断网前已入库的文章都能离线读。
- **ddPC 写入**：`POST /share` 先转发给 77B（带令牌）。失败时写入本地 `outbox` 表，并**在本地先分配 share_id、立即返回**，链接马上可用，本地照常能播；随后每 5 分钟补推一次，77B 用同一个 share_id 入库（冲突时返回 409，人工处理，概率约为 16^10 分之一）。
- **合成不集中**：ddPC 照常在本机做 Edge TTS 和 HLS（服务家里的电视和局域网），只有**文章数据**集中在 77B。时间轴在两处都是实时生成，与数据库无关。
- **云机暂停时的行为**：instabox 的公网端口在机器暂停时是否会自动唤醒，没有核实。方案**不依赖自动唤醒**，靠缓存兜底。如果自动唤醒可用，只会额外带来第一次请求较慢的影响（超时可以放宽到 15 秒）。

---

## 5. 迁移路径

### 5.1 存量迁移（推荐：先原样迁移，同期补解析）

1. **导出**：两边都先**复制** ttsweb.db（不在运行中的文件上操作），用只读脚本导出 JSON，并记录行数和每行文本的 sha256。
2. **导入**：写入 `reading.articles`，字段对应如下：
   - `share_id = id`；`source_text = text`；`lang`、`rate`、`title`、`added_by` 原样写入；
   - `added_at` 解析为 timestamptz；`origin_site` 取 `'77b'` 或 `'ddpc'`；`legacy` 存放原始行；
   - `status = 'raw'`。

   导入前先检查 share_id 是否冲突，冲突时给 ddPC 一侧重新生成 ID，旧 ID 写进 `share_aliases`。同一文本两边重复时（sha 相同），保留较早的一条，较晚的 ID 作为别名。
3. **核对**：导入行数等于两库行数之和减去去重数；逐行对比 sha256；随机打开 3 个旧链接，确认在 77B 上能正常播放。
4. **补解析**：总量十几篇、合计几千到一两万字，**在 P2 里一次跑完**，并人工抽查（每篇抽 3 句，看读音和译文）。不需要分批，也不需要等待。先迁移后解析的原因：迁移能独立验收、风险最低；解析流水线在 P2 才就绪，两者不必互相阻塞。
5. **77B 的 27 字测试文章**：迁移时标记 `deleted_at`（软删除），不丢数据，也不出现在列表里（待拍板第 11 条）。

### 5.2 旧链接保持可用

- URL 形式不变：`/?s=<id>` 和 `/t/<id>`。查找顺序：`articles.share_id` → `share_aliases.alias` → 本地 SQLite `shares`（兜底期内）。
- 旧版页面（v2）读取 `/t/<id>` 时，响应里的原字段原样存在，**不受影响**；新页面 v3 发现有 `sentences` 时启用富模式。
- 两处部署共用一个库之后，**ddPC 的短链在 77B 上也能打开，反过来也一样**（迁移前两边互不相通）。这是一个顺带的收益。

### 5.3 SQLite 退役计划

- **P1 上线后**：77B 的 `shares` 表冻结为只读（服务不再写入），只作为查找失败时的最后兜底。
- **ddPC**：原 `shares` 表保留为兜底；新增 `article_cache` 和 `outbox` 两张表（属于缓存，长期保留）。
- **保留到 2027-02-28**（约 3 个月）。这段时间里，如果日志中没有出现「命中 SQLite 兜底」，就把 ttsweb.db 改名归档（不删除），并移除兜底代码路径（P6）。

---

## 6. 实施步骤

> 注：本节为原全量方案的分期；**定版分期与人日以 8.3 的重估为准**（约 4.75 人日，P3 时间轴推迟）。

| 期 | 交付物 | 验收点 | 工作量 |
|---|---|---|---|
| **P0 验证**（不动服务） | 在独立临时目录里写一个验证脚本：① 用 edge-tts 7.3.1 合成北海道那篇文章，抓取 `audio.metadata` 帧，确认 SentenceBoundary 的字段名、单位，以及开启 rate=-20% 后的偏移；② fugashi+unidic-lite 跑同一篇，统计读音错误；③ 用一次 Claude 调用做译文和校对，确认 JSON 能通过校验；④ 打开 japanese-analyzer 仓库核对标了〔待核〕的说法 | 有一份结论：边界帧可用（或否，否则 P3 退回估算并加强先验）；读音错误率；示例输出 | 0.5 人日 |
| **P1 建库与迁移** | `reading` 的 DDL 与角色；迁移脚本；77B TtsWeb 的 `/t/<id>`、`/share` 改读写 PG，SQLite 兜底；`/t/<id>?with=sentences` | 两边存量全部入库，sha 一致；旧链接在 77B 上能播；PG 停掉后能回落到 SQLite 只读 | 1–1.5 人日 |
| **P2 入库流程** | `ingest.py`（分句、fugashi、Agent 翻译、校验、版本化，由 Agent 当场执行，无 timer）；存量全部解析 | 所有文章状态为 ready（或有说明的 partial）；每句都满足 `toks` 拼接等于原文；人工抽查 30 句，读音错误 ≤1 处，译文无明显误译 | 1.5 人日 |
| **P3 时间轴** | `stream_segment` 收集边界、`timeline.json`、`/play {s}`；前端定位逻辑（可以先接到 v2 上验证） | 北海道那篇在桌面 Chrome、iOS Safari、webOS 三端，当前句切换和声音的偏差 ≤0.3 秒；超过 2000 字的长文跨段后依然对齐；关掉 timeline 后能回落到估算 | 1 人日 |
| **P4 页面 v3** | `player_v3.html` 挂在 `/v3`：上卡片加下列表、ruby、两个开关、点句跳转、重读本句、遥控导航扩充 | 手机竖屏一屏放得下；电视 3 米外可读；v2 的全部功能回归通过（对照 v2 文件头的差异清单）；并行试用一周后转正为 `/` | 1.5–2 人日 |
| **P5 ddPC 接入** | ddPC TtsWeb 的远端读取、缓存、预热、outbox；同步 P3 和 P4 的改动 | 拔网线后已缓存的文章能播（富模式）；断网时新存的文章，在网络恢复后 5 分钟内出现在 77B | 1 人日 |
| **P6 退役** | 2027-02-28 归档 SQLite `shares`，移除兜底路径 | 日志中 SQLite 兜底命中为 0 | 0.25 人日 |

合计约 **6.75–7.75 人日**。各期之间的依赖：P1 → P2；P0 → P3；P2 与 P3 可以并行；P4 依赖 P1、P2、P3；P5 依赖 P1，最好也在 P4 之后。

**风险与对策**：
- Edge 不回传句边界，或格式有变：P3 退回估算，同时用 Agent 入库时给出的每句假名字数（`kana` 长度）替代汉字字数作为权重，比现行算法更准。
- iOS 或 webOS 的 ruby 渲染问题：有括号降级方案。
- 公网 `/share` 被滥用：加写入令牌。
- 77B 磁盘或 PG 故障：db77database 现有的备份覆盖 `reading`；另外 ddPC 的缓存实际上就是一份离线副本。

---

## 7. Agent 入库接口设计

> **指针注记（定版口径）**：本节为全量设计稿；**定版实施范围以第 8 节混合方案为准**，被砍/降级部分保留作恢复参考。

> 本节承接的前提：
> - 真源是 77B db77database 的 `reading` schema（第 3、4 节）；
> - 翻译在 agent 侧完成，服务端**不调用任何 LLM**（修订注记 2）；
> - 服务端只跑确定性的分句和 fugashi+UniDic 骨架（2.2 的 ①–③、⑤）；
> - 调用方 `added_by ∈ {A001, A002, A003}`（龙奈 A002、糖糖 A003 为 Tang 入库）。
>
> 本节只补充接口层，不改变前文结论。

### 7.0 总览

| 端点 | 作用 | 权限 |
|---|---|---|
| `GET /ingest.md` | 给 agent 读的调用说明（7.5） | 公开 |
| `GET /api/v1/whoami` | 验证令牌，返回 `{agent, scopes}` | read |
| `POST /api/v1/ingest/preview` | **试运行**：分句加骨架，**不写库** | write |
| `POST /api/v1/ingest/full` | **一步事务型**：提交整篇（原文、分句、逐句译文、可选读音覆盖） | write |
| `POST /api/v1/ingest/draft` | **两步回填型第 1 步**：只提交原文，服务端分句加骨架后落库，返回句子清单 | write |
| `POST /api/v1/articles/{share_id}/draft` | 给已存在的 raw 文章（迁移的存量、`/share` 存入的、ddPC outbox 补推的）生成骨架，效果同上 | write |
| `PATCH /api/v1/articles/{share_id}/sentences` | **两步回填型第 2 步**：批量回填译文和读音覆盖，可以多次调用 | write |
| `GET /api/v1/articles/{share_id}/pending` | 列出尚未回填译文的句子（用于断点续做） | read |
| `GET /api/v1/articles/{share_id}?with=sentences[,toks]` | 读取文章（默认不带 toks，节省 agent 上下文） | read |
| `GET /api/v1/articles?status=partial&mine=1&since=…` | 列表和增量（ddPC 预热也用这个） | read |
| `POST /api/v1/articles/{share_id}/revise` | 定稿后修改：写时复制出新版本（`kind=manual`） | write |
| `POST /share` | 保留旧路径，内部等同于 `ingest/draft`（7.6） | write（有宽限期） |

- 页面使用的读取口（`/t/<id>`、`/?s=`、`/hls/.../timeline.json`）**仍然免令牌**，见 7.4。
- 所有 `/api/v1/*` 都需要 `Authorization: Bearer <token>`。
- 请求和响应都是 JSON（UTF-8）。成功时一律返回 `{"ok": true, ..., "warnings": [...]}`，失败时返回 7.3.4 的错误体。

### 7.1 入库姿势（两步型 / 一步型 / 对齐型）

#### 7.1.1 两步回填型（推荐作为默认）

**适用场景**：一般文章、长文、剧集对白。agent 只需要手里有原文。

推荐作默认的理由：
- **分句以服务端为准**，对应 2.3 的「入库侧唯一标准」；
- 第 1 步结束后文章就能播放，ruby 注音也已经有了；
- agent 会话中途断开也不丢进度，可以用 `/pending` 接着做；
- 服务端返回的每句都带 `kana`（整句读音），agent 翻译时可以直接参考，不必自己装分词工具。

**第 1 步：提交原文**
```http
POST /api/v1/ingest/draft
Authorization: Bearer rk_A003_…
```
```json
{
  "text": "大丈夫ですか？\n水…\nスポーツ飲料も買ってきました。常温です。",
  "title": "逃げ恥 第1話 看病",
  "lang": "ja",
  "rate": "-20%",
  "source_ref": "drama:nigehaji/ep01",
  "tags": ["逃げ恥", "对白"]
}
```
服务端在单个事务里依次完成：规范化 → 分句 → fugashi 骨架 → 写入 `articles`（status=partial）、`analyses`（v1，status=partial，`kind=agent`）和 `sentences`（toks 已填，tr 为 NULL）。其中 `current_version=1`，所以文章立刻可以播放。**响应一次包含全篇全部句子（不分页、不分批返回）**，每句带 sid、seq、原文与整句读音 kana，并带 `para` 段落号帮 agent 把握段落结构（下例从简，未逐个标出 para）。响应如下：
```json
{
  "ok": true,
  "share_id": "a3f09c1b2e",
  "url": "/?s=a3f09c1b2e",
  "version": 1,
  "status": "partial",
  "duplicate": false,
  "sentences": [
    {"sid": "v1-0-7c1d", "seq": 0, "text": "大丈夫ですか？", "kana": "だいじょうぶですか？"},
    {"sid": "v1-1-0b9e", "seq": 1, "text": "水…", "kana": "みず…"},
    {"sid": "v1-2-e41a", "seq": 2, "text": "スポーツ飲料も買ってきました。", "kana": "すぽーついんりょうもかってきました。"},
    {"sid": "v1-3-55f0", "seq": 3, "text": "常温です。", "kana": "じょうおんです。"}
  ],
  "next": "PATCH /api/v1/articles/a3f09c1b2e/sentences",
  "warnings": []
}
```

**sentence ID（`sid`）的设计**：格式为 `v<version>-<seq>-<hash4>`，其中 hash4 是该句文本 sha256 的前 4 个十六进制字符。
- agent 只需要**原样带回 sid**，不用计算任何东西。
- 服务端用 sid 里的版本、序号和哈希三项一起校验，可以防止「译文贴错句」（例如 agent 在本地增删了句子，导致序号整体错位），也可以防止「文章已经修订成新版本，旧句单还在被回填」。
- **一次返回全篇（硬要求，用户 2026-11-18 定）**：draft 的响应把全篇句子一次给全，**不分页、不分批**，每句另带 `para`（段落号，上例从简未标出），帮翻译时把握段落结构。agent 必须先通读全篇再动手翻译（见第 2 步的规矩）。

**第 2 步：回填译文（标准姿势：整篇一次回填）**

**翻译与回填的规矩（硬性）**：先通读全篇、结合上下文与段落语境统一人称、语气、译名，再整篇翻译，**全篇译文一次整批回填**；禁止逐句孤立翻译——逐句翻容易出现前后句衔接不合理、不符合段落意思。下面 PATCH 分批（≤200 句）只作断线或超长文的容错手段，不许把分批当成逐句翻译的借口。
```http
PATCH /api/v1/articles/a3f09c1b2e/sentences
```
```json
{
  "translator": {"via": "agy-gemini", "model": "gemini-2.5-pro"},
  "items": [
    {"sid": "v1-0-7c1d", "tr": "你没事吧？"},
    {"sid": "v1-1-0b9e", "tr": "水……"},
    {"sid": "v1-2-e41a", "tr": "我还买了运动饮料回来。",
     "readings": [{"tok": 3, "s": "買っ", "r": "かっ"}]},
    {"sid": "v1-3-55f0", "tr": "是常温的。"}
  ]
}
```
响应：
```json
{"ok": true, "share_id": "a3f09c1b2e", "version": 1,
 "applied": 4, "unchanged": 0, "remaining": 0, "status": "ready", "warnings": []}
```

**状态流转（两步型）**：
```
(无) ──draft──▶ partial（toks 已有、tr 全空，可以播放）
partial ──PATCH 若干批──▶ partial（remaining > 0）
partial ──最后一批使 remaining=0──▶ ready（analyses.status=ready，该版本冻结）
ready ──revise──▶ 新版本 v(n+1)（kind=manual），直接 ready；旧版本标为 superseded
```
- fugashi 出错时，该句的 `toks` 为 NULL，并在 `warnings` 里给出 `TOKENIZER_FAILED`。文章仍然是 partial，翻译完成后照样会变成 ready。
- `raw` 只出现在还没生成骨架的存量文章上（迁移数据、ddPC outbox 补推的）；对这些文章调用 `/articles/{id}/draft` 后即进入上面的流程。
- `analyzing` 状态在这条流程里不会用到：骨架是同步生成的，5000 字约百毫秒级。

#### 7.1.2 一步事务型（补充用）

**适用场景**：短文，或者 agent 手里**已经有完整的分句和译文**（例如双语材料、自己早就翻好的稿子；材料自带译文、只需对齐不重译的走 7.1.3），想要「要么整篇成功，要么什么都不写」的情形。

推荐做法是先调用 `preview` 拿到服务端的标准分句（不写库），在本地翻译，再调用 `full` 提交。这样分句仍然以服务端为准。
```http
POST /api/v1/ingest/full
```
```json
{
  "text": "大丈夫ですか？\nスポーツ飲料も買ってきました。",
  "title": "看病（短）",
  "lang": "ja", "rate": "-20%",
  "translator": {"via": "self"},
  "sentences": [
    {"text": "大丈夫ですか？", "tr": "你没事吧？"},
    {"text": "スポーツ飲料も買ってきました。", "tr": "我还买了运动饮料回来。",
     "readings": [{"tok": 3, "s": "買っ", "r": "かっ"}]}
  ]
}
```

服务端的处理规则：
- **单个事务**内依次执行：校验 → 按 agent 提交的分句生成骨架（agent 不需要提交 toks）→ 应用读音覆盖 → 写入 articles、analyses、sentences。所有句子都有 tr 时，文章直接为 `ready`。
- **任何一项校验失败都整篇回滚**，不写入任何一行，错误一次性全部列出（7.3.4）。
- 句子文本按顺序拼接后，必须**等于规范化后的原文**。只允许句间空白和换行不同，服务端会把它们回填到相邻句的 `char_start`/`char_end` 区间里。
- agent 的分句和服务端规则不一致时，**接受但给出 warning `SPLIT_NONSTANDARD`**，并在 `analyses.tool.splitter` 里记为 `"agent"`。
- 默认要求每句都有 `tr`。如果确实只译了一部分，必须显式传 `"allow_partial": true`，这时文章落为 `partial`，之后用 PATCH 补齐（待拍板第 18 条）。

响应形状和 draft 相同，区别是 `status` 通常为 `ready`。

**状态流转（一步型）**：`(无) ──full──▶ ready`；带 `allow_partial` 时为 `(无) ──full──▶ partial ──PATCH──▶ ready`。

#### 7.1.3 原文＋现成译文：只对齐、不重译（用户 2026-11-18 补充）

**适用场景**：提供给 agent 的材料本身就带译文（双语稿、已出版的译文、用户已译好的文本）。此时 agent 的工作**只有分句对齐**：把原文与译文分别切句、配成对子，`tr` 直接取自随文译文——**不重新翻译、不润色改写**，译文原样入库；发现明显笔误也只记进 `note`，不擅自改动。

**姿势**：一步型的变体，仍走 `POST /api/v1/ingest/full`，两点区别：
- 请求带 `"translation_text": "<随文译文全文>"`，`translator.via` 填 **`provided`**（7.2 的 `tr_via` 新增取值）；`tr_by` 仍按令牌记提交的 agent（对齐执行者），译文的原始出处写进 `source_ref` 或 `note`；
- `sentences[].tr` 由 agent 从 `translation_text` 切分对齐得到，服务端不生成任何译文。
- **骨架照旧由服务端生成**：分词与读音由服务端 fugashi 按对齐后的原文句生成（同一步型），agent 不需要因为对齐重做分词；读音有问题仍可走 `readings` 覆盖。

**`tr_by` 与译文来源的区别（承接第 23 条）**：`tr_by` 记的是**谁把这篇文章提交入库**（按令牌决定，= 做对齐的那个 agent），**不是**译文的原作者；译文的来历记在 `tr_via=provided` 加 `source_ref`（如「《××》×× 译」）里。日后追溯「这篇是谁入库的」看 `tr_by`，「译文是谁译的」看 `source_ref`，两者不要混填。

**对齐规则**：
- 原文侧沿用一步型校验：`sentences` 的 `text` 按序拼接 == 规范化后的原文；
- 译文侧新增校验：`sentences` 的 `tr` 按序拼接 == `translation_text`（只允许句间空白/换行差异）——保证译文一字不差地拆进各句，没有漏句、没有私自改写；
- **不强求 1:1**：原文一句、译文两句（或反之）时，agent 可以把多句译文并进同一条 item 的 `tr`（换行分隔），或把一条原文拆成两条 item（`text` 拼接仍须等于原文）；对齐方式记进 `analyses.tool.align`（如 `{"merged": [[3,4]], "split": [7]}`），便于事后核查；
- **落库与播放粒度**：合并的译文存进同一条 sentence 行的 `tr`（换行分隔），不新增行——播放与逐句高亮的粒度以**原文句**为准，不受译文句数影响，当前句卡片把该句的完整 `tr` 多行原样展示；拆分原文则落成两行、各自成为一句播放/高亮。
- **两类对齐错误有专码**（7.3.4）：原文句没有对应译文 → `ALIGN_TR_MISSING`；译文有段落悬空、无对应原文 → `ALIGN_TR_ORPHAN`。两者都由译文侧拼接校验定位到具体位置，hint 写明并入/拆分的改法，agent 可自纠。
- 任一侧拼接对不上 → 整篇回滚（一步型语义），错误中给出第一处对不上的位置，agent 修正后重提；
- **发现译文明显有误时**：入库环节**不改**——照原样对齐入库，疑点写进 `note` 并上报用户；确认要改的走 `revise` 出新版本（`kind=manual`、留痕），不在对齐提交里顺手改字。
- 篇幅长、想以服务端分句为准时，也可以先 draft 拿到 sid 清单，再把随文译文按 sid 对齐回填（PATCH，`via=provided`），译文拼接校验在回填完成时执行。

**状态流转**：与一步型相同，`(无) ──full──▶ ready`。

### 7.2 翻译归属与读音校对的表达

**字段设计**（`reading.sentences` 新增三列，`tr_by` 沿用）：

| 列 | 类型 | 来源 | 说明 |
|---|---|---|---|
| `tr_by` | text | **服务端按令牌身份写入**，请求体不能指定 | `A001` / `A002` / `A003`；人工修订时为 `human:<名字>`，迁移数据为 `import` |
| `tr_via` | text，CHECK ∈ (`self`,`claude-code`,`agy-gemini`,`human`,`import`,`provided`) | 请求体 `translator.via`，默认 `self` | 这句是 agent 自己翻的，还是**经 Claude Code 无头或 agy Gemini 代翻**的；`provided` = 材料自带译文、agent 只做对齐未重译（7.1.3） |
| `tr_model` | text，可空 | 请求体 `translator.model` | 代翻时的模型名，自由文本，只做记录 |
| `tr_at` | timestamptz | 服务端写入 | 译文写入时间 |

- `translator` 可以写在请求顶层作为整批的默认值，也可以写在单个 item 里覆盖（比如一批里有几句是 agent 手工改过的，那几句标 `self`）。
- 版本层面的汇总写进 `analyses.tool.translator`，例如 `{"A003": {"agy-gemini": 38, "self": 2}}`，便于一眼看出一个版本是怎么来的。

**读音校对的提交方式**：用 `readings` 数组，只覆盖骨架 token 的**读音字段**，不允许改动分词边界和原文。
- 每条格式为 `{"tok": <token 下标>, "s": <该 token 的表层形，用来核对>, "r": <平假名读音>}`；可选 `"ruby": [...]`，用来手工指定送假名的拆分。
- 不给 `ruby` 时，服务端按新读音重新做汉字和假名的对齐，自动生成 ruby 段。对齐失败时（例如熟字训「今日」），整个词作为一段注音，并给出 warning `RUBY_WHOLE_WORD`。
- `s` 必须等于 `toks[tok].s`，否则报 `TOKEN_SURFACE_MISMATCH`。这一条用来防止 agent 数错下标。
- 每次覆盖都追加一条记录到该句的 `fixes`：`{"tok":3,"from":"かう","to":"かっ","by":"A003","via":"self","at":"…"}`，同时更新整句的 `kana` 和 `lemmas`（lemma 本身不变）。
- 分词边界确实切错、需要改的情况很少见。这时不走 readings，而是走 `revise` 时提交整句的 `toks` 替换，服务端只校验 `s` 拼接后等于句子本身。

**与 analyses 版本机制的衔接**（补充 3.3）：
- 版本处于 `partial`（**未定稿**）时，PATCH 回填和读音覆盖都**原地写入当前版本**。草稿阶段不生成新版本，避免一篇文章因为翻译一半就产生几十个版本。
- 版本变为 `ready` 后就**冻结**。此后对译文或读音的任何修改，都必须走 `POST /articles/{id}/revise`：
  ```json
  {"base_version": 1, "note": "修正 今日 读音",
   "items": [{"sid": "v1-5-9a0c", "readings": [{"tok": 0, "s": "今日", "r": "きょう"}]}]}
  ```
  服务端复制 v1 的全部句子，生成 v2（`kind=manual`，未修改的句子保留原来的 tr_by/tr_via），应用修改后，在同一事务里切换 `current_version`，并把 v1 标为 `superseded`。如果 `base_version` 不等于当前版本，返回 `VERSION_STALE`，防止两个 agent 基于同一旧版本各改一处、后提交的覆盖掉先提交的。
- DDL 增量（设计草案）：`analyses.kind` 的取值新增 `'agent'`；`sentences` 新增 `tr_via`、`tr_model`、`tr_at` 三列；`articles` 新增 `text_hash bytea NOT NULL`，以及部分唯一索引 `UNIQUE (text_hash) WHERE deleted_at IS NULL`（见 7.3.1）。

### 7.3 事务与健壮性

#### 7.3.1 幂等与去重

- **幂等键是自然键**，即 `text_hash = sha256(规范化后的原文)`。规范化方式和 2.2 ① 一致，另外去掉首尾空白，并统一换行为 `\n`。不另外要求 `Idempotency-Key` 头：agent 网络重试、会话重启后重发同一篇，都会算出同一个键。
- **去重范围是全库**，在未删除的文章之间去重，不区分 agent（待拍板第 19 条）。同一段原文只存一份，糖糖和龙奈各自入库同一篇时，会共用同一个短链。
- **服务端的行为**：

| 已存在的文章状态 | `draft` 重复提交 | `full` 重复提交 |
|---|---|---|
| 不存在 | 新建 | 新建 |
| raw（存量） | 就地生成骨架（等同于 `/articles/{id}/draft`），返回 `duplicate:true` | 就地按提交内容建 v1，返回 `duplicate:true` |
| partial | **不新建**，返回已有的 share_id、version 和 sentences（相当于从断点续做），`duplicate:true` | 把提交的译文**当作一次 PATCH** 补进去（按 7.3.3 的规则处理冲突），`duplicate:true` |
| ready | 不改动任何内容，返回已有的 share_id，`duplicate:true`，并给出 warning `ALREADY_READY`（要改请走 revise） | 同左；即使提交的译文不同也不覆盖，在 warning 里列出差异条数 |

- 重复命中时，title、tags 等元数据**不覆盖**；只在原值为空时补上。
- HTTP 状态码统一为 200。agent 只需要看 `duplicate` 和 `status` 两个字段。
- 迁移进来的存量在导入时也计算 `text_hash`。两边重复的存量已经在 5.1 里用别名合并，不会触发唯一索引冲突。

#### 7.3.2 部分回填与续做

- 每次 PATCH 是一个独立事务，批与批之间互不影响。中途断开时，已经提交的批次都保留。
- 续做方式：调用 `GET /articles/{id}/pending`，返回 `remaining` 和尚未翻译的 `[{sid, seq, text, kana}]`。agent 不需要在本地记录进度。
- 每批最多 200 句；一篇文章最长 5000 字，大约 150–250 句，一到两批就能交完。
- 一直停在 partial 的文章（agent 中途放弃）照常可以播放，不需要清理；可以用 `GET /api/v1/articles?status=partial&mine=1` 列出来，之后找时间补齐。

#### 7.3.3 并发与重复回填的覆盖规则

- **串行化**：每个 PATCH、revise 请求在事务开始时对 `analyses(article_id, version)` 这一行执行 `SELECT … FOR UPDATE`。同一篇文章的写请求排队执行，不同文章之间互不阻塞。
- **逐句规则**：

| 该句现状 | 提交内容 | 结果 |
|---|---|---|
| tr 为 NULL | 任意合法译文 | 写入，计入 `applied` |
| tr 与提交内容逐字相同 | 相同 | no-op，计入 `unchanged`（所以重试是安全的） |
| tr 已有，内容不同 | 不同，且未带 `overwrite` | 拒绝，返回 `TR_EXISTS`，响应里附上现有译文和译者 |
| tr 已有，内容不同 | 不同，并带 `"overwrite": true` | 只有**原译者本人**或 admin 可以覆盖，否则返回 `OVERWRITE_DENIED` |
| 版本已经 ready | 任意 | 拒绝，返回 `VERSION_FROZEN`（提示改走 revise） |

- **一个 PATCH 请求是原子的**：只要有一条 item 校验失败或冲突，整批都不写入，并一次性列出所有出错的 item。agent 修正后原样重发整批即可，已经写入的句子会被判为 unchanged，不会重复计入。不采用「部分成功」，是为了让 agent 不必去分辨哪些写进去了、哪些没写进去。
- readings 的覆盖规则与 tr 相同：同一个 token 已被**别人**改过读音、提交值又不同时，返回 `READING_EXISTS`。

#### 7.3.4 错误格式与错误码

统一的错误体中，`message` 用中文写清问题，`hint` 写清怎么改，`details` 定位到出错的具体字段：
```json
{
  "ok": false,
  "error": {
    "code": "SPLIT_MISMATCH",
    "message": "sentences 按顺序拼接后与 text 不一致",
    "hint": "不要改动原文字符；先调用 POST /api/v1/ingest/preview 取得标准分句，再逐句填 tr",
    "details": [
      {"path": "sentences[3].text", "at_char": 41,
       "expected": "常温です。", "got": "常温です"}
    ]
  },
  "request_id": "r-20261118-7f3a"
}
```

| code | HTTP | 含义 | agent 应该怎么做 |
|---|---|---|---|
| `AUTH_MISSING` | 401 | 没有带令牌 | 加上 `Authorization: Bearer $TTSWEB_TOKEN` |
| `AUTH_INVALID` | 401 | 令牌无效或已吊销 | 停止重试，报告给用户 |
| `SCOPE_DENIED` | 403 | 令牌没有这个权限（例如只读令牌去写） | 换用对应的令牌，或报告给用户 |
| `BY_MISMATCH` | 403 | 请求体里的 `by`/`added_by` 与令牌身份不一致 | 删掉 `by` 字段（服务端会按令牌填） |
| `RATE_LIMITED` | 429 | 超过限流 | 等待 `Retry-After` 秒后重试 |
| `BODY_INVALID` | 400 | 不是合法 JSON，或字段类型错误 | 按 `details[].path` 修正 |
| `TEXT_EMPTY` / `TEXT_TOO_LONG` | 400 | 原文为空，或超过 5000 字 | 拆成多篇（标题加「上/下」） |
| `FIELD_TOO_LONG` | 400 | title 超过 120 字、tr 超过 500 字、批次超过 200 句等 | 截短或分批 |
| `SPLIT_MISMATCH` | 422 | 分句拼接后与原文不一致（只在一步型出现） | 用 preview 返回的分句 |
| `SENTENCE_EMPTY` | 422 | 某句为空，或只有空白 | 并入相邻句 |
| `TR_MISSING` | 422 | 一步型缺译文，且没有带 allow_partial | 补齐，或者显式传 `allow_partial` |
| `TR_INVALID` | 422 | 译文为空，或与原文完全相同 | 重新翻译这一句 |
| `ALIGN_TR_MISSING` | 422 | 对齐型：某原文句没有对应译文（tr 为空且未与相邻句合并） | 把该句译文补进对应 item，或把译文并入相邻 item 的 tr（换行分隔）并在 align 里标 merged |
| `ALIGN_TR_ORPHAN` | 422 | 对齐型：译文拼接后多出无对应原文的段落（悬空译文） | 把悬空段并入相邻 item 的 tr，或把对应原文拆成两条 item；不要删改译文原文 |
| `SID_UNKNOWN` | 422 | sid 格式错误，或这篇文章里没有这一句 | 重新调用 `/pending` 取 sid |
| `SID_STALE` | 409 | sid 里的版本已经不是当前版本，或哈希对不上 | 重新调用 `/pending`，只回填仍然缺译文的句子 |
| `TOKEN_SURFACE_MISMATCH` | 422 | readings 里的 `s` 与该下标的 token 不一致 | 用 `?with=toks` 取回 toks，核对下标 |
| `READING_INVALID` | 422 | 读音不是纯平假名（允许 ー） | 把片假名转成平假名，去掉空格和罗马字 |
| `TR_EXISTS` / `READING_EXISTS` | 409 | 已经有不同的译文或读音 | 确认确实要改：本人可以加 `overwrite`；别人写的走 revise 或报告给用户 |
| `OVERWRITE_DENIED` | 403 | 想覆盖他人的译文 | 改走 revise（会留下版本记录），或报告给用户 |
| `VERSION_FROZEN` | 409 | 版本已定稿，不能原地修改 | 改用 `POST /articles/{id}/revise` |
| `VERSION_STALE` | 409 | revise 的 base_version 不是当前版本 | 重新读取文章，基于新版本重做修改 |
| `ARTICLE_NOT_FOUND` | 404 | share_id 不存在（别名也查过了） | 检查 ID |
| `ARTICLE_DELETED` | 410 | 文章已被软删除 | 报告给用户（恢复只能由 admin 操作） |
| `DB_UNAVAILABLE` | 503 | PG 不可用 | 指数退避重试，最多 5 次；之后报告给用户 |

warning（不阻断提交，出现在成功响应的 `warnings[]` 里）：
- `SPLIT_NONSTANDARD`：分句和服务端规则不同；
- `TOKENIZER_FAILED`：某句骨架生成失败，toks 为空；
- `RUBY_WHOLE_WORD`：送假名对齐失败，整个词作为一段注音；
- `TR_SUSPECT`：译文中假名占比超过 30%，疑似没有翻译；
- `ALREADY_READY`：文章已经定稿，本次提交没有改动任何内容。

#### 7.3.5 回滚边界

| 操作 | 事务范围 | 失败时的结果 |
|---|---|---|
| `draft` | articles、analyses、sentences 全部在一个事务里 | 什么都不写；不会出现「有文章没句子」的情况 |
| `full` | 同上，再加读音覆盖 | **整篇回滚** |
| `PATCH` | 单批 | 本批全部不写入；之前的批次不受影响 |
| `revise` | 建新版本、复制句子、应用修改、切换 current_version、标记旧版本为 superseded，全部在一个事务里 | 什么都不写，当前版本保持不变 |

另外，**不提供跨批回滚**。一批写错了，用 overwrite（本人）或 revise 修正，这样可以留下记录。

### 7.4 权限与安全（承接待拍板第 13 条）

- **令牌形态**：每个 agent 一枚，格式为 `rk_<agent>_<32 位随机 base62>`，例如 `rk_A003_…`。前缀带身份，日志里只记录前 12 个字符，方便辨认。
  - 服务端**只存哈希**，新增表 `reading.api_tokens(token_hash bytea PK, agent text, scopes text[], note, created_at, last_used_at, revoked_at)`。比对用常数时间比较。
- **发放**：在 77B 本机通过命令行工具发放（`tokens issue A003 --scopes read,write`，属于运维动作，**不提供 HTTP 发放接口**）。令牌明文只显示一次，由用户放进对应 agent 所在机器的 instabox Secrets，作为环境变量 `TTSWEB_TOKEN`。不写进任何文件、仓库或聊天记录。
- **吊销与轮换**：通过 CLI 设置 `revoked_at`；服务端的令牌缓存 60 秒，所以最迟 1 分钟生效。轮换方式是先发新令牌、更新 Secrets，再吊销旧令牌，同一个 agent 允许同时有两枚有效令牌。
- **作用域**：

| scope | 能做什么 | 发给谁 |
|---|---|---|
| `read` | `/api/v1` 下的全部 GET（列表、增量、pending、whoami） | 所有 agent；ddPC 同步用的令牌 |
| `write` | draft、full、PATCH、revise（只能覆盖自己写的译文）、`/share` | A001、A002、A003 |
| `relay` | 代转发 `/share`：允许请求体里的 `by` 在白名单 {A001,A002,A003,ddpc} 内，`origin_site` 固定为 `ddpc` | 只发给 ddPC TtsWeb |
| `admin` | 覆盖他人译文、软删除和恢复、查看审计日志 | **不发放 HTTP 令牌**，只能在 77B 本机用 CLI 操作 |

- **读写分离**：
  - 页面的读取口 `/t/<id>`、`/?s=`、timeline **保持免令牌**。短链本身就是分享凭据，10 位十六进制约 2^40 种，无法枚举，旧链接和分享给家人的链接都照常可用。
  - 能**列举**文章的 `/api/v1/articles` 列表和增量接口**必须有 read 令牌**，防止公网有人拉取全库。
- **防冒名**：`added_by` 和 `tr_by` **一律由服务端按令牌身份填写**。请求体里的 `by` 只在和令牌身份一致时被接受，不一致就返回 `BY_MISMATCH`（relay 令牌按上面的白名单处理）。审计表 `reading.ingest_log(at, agent, token_prefix, method, path, share_id, code, ip)` 记录每一次写请求。
- **公网暴露面的最小防护**（77B 的 8791 端口经 instabox 公网 HTTPS 暴露）：
  - 限流：写入每个令牌每分钟 60 次；鉴权失败每个 IP 每分钟 10 次，超过后封禁该 IP 10 分钟；免令牌的 `/t/<id>` 每个 IP 每分钟 120 次。实现为进程内令牌桶，不引入外部组件。
  - 请求体上限 512 KB；`/api/*` 不开放 CORS（agent 不是浏览器）。
  - 错误信息里不回显令牌，也不暴露 SQL 细节。
  - 提醒：已有的 `/play` 同样免令牌，会触发 Edge 合成，可以顺手加上每 IP 限流，不过这不在本节范围内。
- **ddPC 的同步与调用**：agent 一律**直接调用 77B 的接口**入库，不经过 ddPC。ddPC TtsWeb 只持有一枚 `read+relay` 令牌：read 用于 4.2 的读取和预热，relay 用于转发本地 `/share` 和补推 outbox（补推时走 `/api/v1/ingest/draft`，并带上本地预先分配的 share_id）。

### 7.5 给 agent 读的调用说明：`GET /ingest.md`

- **放在哪里**：服务根路径 `/ingest.md`，以 `text/markdown; charset=utf-8` 返回，公开可读，内容不含任何秘密。文件随服务代码一起维护，头部写明 `ingest_api: v1` 和更新日期。另外提供 `/api/v1/schema/{draft,full,patch,revise}.json` 共 4 份 JSON Schema，agent 可以在提交前自行校验。
- **怎么用**：在各 agent 的记忆或技能里只放一句「入库前先读 https://8791-…instabox.tech/ingest.md」，接口变更时不需要逐个更新 agent。
- **提纲**（目标是 agent 一次读完就能正确调用，控制在约 6–8 KB）：
  1. **30 秒上手**：三条 curl 示例，分别是 whoami、两步型的完整流程（draft、PATCH、pending）、一步型（preview、full）；
  2. **鉴权**：Bearer 头、环境变量 `TTSWEB_TOKEN`、不要把令牌写进输出、whoami 自检；
  3. **选哪种姿势**：一张决策表（默认两步型；手里已有整套译文时用一步型；材料自带译文时用对齐型 7.1.3）；
  4. **两步型详解**：请求和响应字段表、sid 的含义（原样带回）、标准姿势整批回填、分批（≤200 句）仅作容错与断点续做；
  5. **一步型详解**：先 preview 再 full、allow_partial、整篇回滚；对齐型（7.1.3）：交原文＋译文＋配对清单，职责是切分对齐、不是翻译，不得改写现成译文；发现译文有误先在 note 标注上报，修正走 revise；
  6. **翻译规范**：自然中文意译（待拍板第 5 条）；**不得改动原文**；对白保留语气词；人名用常见中文译名，或保留日文原名；`translator.via` 和 `model` 的填法；**先通读全篇再译、全篇译文整批回填，禁止逐句孤立翻译**；**材料自带译文时只做分句对齐、不重译**（`via=provided`，见 7.1.3）；
  7. **翻译规矩**（硬性，独立成节）：先通读全篇再动手，按段落语境统一人称、语气与译名；译完后**整批回填**，不得逐句孤立翻译（逐句翻会出现前后衔接不合理、不符段落整体意思）；分批提交只用于超长文章或断点续做；
  8. **读音覆盖**：readings 格式、只用平假名、`s` 核对规则、熟字训会整体注音；
  9. **定稿后修改**：revise 与 base_version；
  10. **状态与幂等**：raw、partial、ready 的含义；重复提交返回什么（`duplicate`）；
  11. **错误码表**：同 7.3.4，「怎么做」一列原样保留；
  12. **限额**：字数、批大小、限流；
  13. **提交前检查清单**：例如「句数等于 pending 数」「每个 tr 非空」「没有片假名读音」；
  14. **变更记录**。

### 7.6 与现有 `/share` 的关系（推荐：保留路径，内部并入两步型第 1 步）

- 保留 `POST /share {text, lang, rate, title, by}`，**响应保持兼容**：原有的 `{id, url}` 不变，另外附加 `status`、`version`、`sentences`，旧调用方会忽略这些新字段。
  - 内部实现等同于 `POST /api/v1/ingest/draft`：分句、生成骨架、落为 partial，并按 text_hash 去重。
  - 好处是旧用法（只存原文、拿短链）**零改动**也能拿到 ruby 注音，之后任何 agent 都可以用 `/pending` 补译文。
- 不推荐把 `/share` 保留成只写原文（raw）的独立口子：那样会形成第二条写入路径，还要另外维护去重和鉴权逻辑；而且分词骨架既然是同步生成、开销很小，就没有理由跳过。
- **鉴权过渡**：已核实 player_v2 页面本身**不调用** `/share`，调用方只有 agent 和 ddPC。因此 `/share` 和新接口用同一套令牌，给 **2 周宽限期**（不带令牌的请求照常受理，但在日志里标记，并在响应里加 warning `AUTH_DEPRECATED`）。宽限期结束后强制要求令牌（待拍板第 27 条）。宽限期内，不带令牌的 `by` 字段原样写入 `added_by`，但会在审计日志里标为「未验证」。

### 7.7 落入实施分期（补充第 6 节）

- **P1**：令牌表、鉴权、限流、审计中间件；`/api/v1/whoami` 和读取接口；`/share` 改走新逻辑（含宽限期）。约 +0.5 人日。
- **P2**：preview、draft、full、PATCH、pending、revise；`/ingest.md` 和 JSON Schema；由 A001、A002、A003 各用一篇真实文章走通一次（至少覆盖一次断点续做和一次 `TR_EXISTS`）作为验收。约 +1 人日。
- 第 6 节的合计因此变为约 **8.25–9.25 人日**。

---

## 8. 复杂度复核与简化建议

> 本节只做评估，不改动前文。凡因简化而作废或改写的待拍板条目，在 8.4 逐条点名；**文末清单保持原样，等用户拍板后再改**。
>
> 评估的权重前提：
> - 业务量：朗读文章存量只有几篇，入库是偶发触发，一天最多几篇，**不是高吞吐流水线**；
> - 入库不在播放路径上，翻译慢几分钟、甚至晚几个小时才完成，都不影响听；
> - 77B 是免费沙箱，30 分钟无活动会暂停，单次运行超过 480 分钟后有冷却；
> - ddPC 一般常开，但会重启，也断过电。

### 8.0 一句话结论

**方案确实偏复杂**，复杂的地方集中在第 7 节。第 7 节是按「多个 agent 并发、长时间、可续做地写同一篇文章」来设计的，但实际业务量下这种场景几乎不会出现。

**让 Muse agent 把翻译、校对、对齐整包交给一个 Claude Code 驻地去做，这个方向是对的**，主要收益在**质量一致性**和**写入方收敛成一个**，后者让第 7 节可以砍掉大半。不过它**换不来速度**（两条路线都是分钟级），在稳定性上还**多了一个依赖**。好在这个依赖不在播放路径上，可以接受。

**推荐混合方案**，具体是：
- 数据真源、页面 v3、ddPC 只读缓存：保留；
- 入库接口：收缩为 preview 加一次性 commit 两个写端点；
- 翻译：固定交给 ddPC Claude Code 驻地，77B 驻地做人工备份，agent 自己翻作为兜底；
- 时间轴：推迟。

工作量从约 8.25–9.25 人日降到约 **4.75 人日**（如果之后补做时间轴，约 5.75 人日）。

### 8.1 复杂点盘点

判定口径：
- **必需**：去掉以后核心需求（跟读页、译文、注音、旧链接、数据安全）会受损；
- **可降级**：需求存在，但可以用更薄的做法满足；
- **过度设计**：按现在的业务量和写入方数量，几乎用不上。

| # | 机制（出处） | 判定 | 理由 |
|---|---|---|---|
| 1 | `preview`（7.0） | **必需** | 让服务端的分句和 kana 成为唯一标准，翻译方据此逐句作答；它不写库，零状态，成本最低 |
| 2 | 一步事务型 `full`（7.1.2） | **必需（改为唯一的写入姿势）** | 整篇一次提交、单事务、失败整篇回滚，最符合「偶发、整篇、一次做完」的实际用法 |
| 3 | 两步回填型 draft、PATCH、pending、sid 哈希（7.1.1、7.3.2） | **过度设计** | 为的是断点续做和多批回填。驻地或 agent 几分钟就能翻完一篇，中断了重来一次成本更低。sid 哈希防错位的作用，在整篇 commit 里由「拼接必须等于原文」这一条校验覆盖 |
| 4 | 对齐型（7.1.3） | **必需，可降级** | 「只对齐、不重译」是用户定的规矩，要保留：`via=provided` 和译文侧拼接校验都保留。两个专码可以合并成一个 `ALIGN_MISMATCH`，在 details 里区分缺失和悬空。合并信息记进 `tool.align` 可以保留，成本很低 |
| 5 | 错误码表 25 条加 5 种 warning（7.3.4） | **可降级** | 有用的是统一的错误体（message、hint、details 定位），而不是码的数量。单一写入方加单一姿势，8 条就够（见 8.3） |
| 6 | 版本 analyses 加 revise 端点、草稿原地写和定稿冻结、`VERSION_FROZEN`/`STALE`（7.2） | **可降级** | 版本留痕本身值得保留，重跑或换模型以后能回看。但不需要独立的 revise 端点和草稿状态机：改动一律用 `commit` 带 `replace:true` 整篇重交、生成新版本 |
| 7 | 覆盖规则 TR_EXISTS、overwrite、OVERWRITE_DENIED、行锁串行（7.3.3） | **过度设计** | 前提是多个 agent 在同一篇草稿上逐句争写。整篇提交加单一写入方之后，这个场景不存在了；同一篇被同时重交的概率可以忽略，用 `base_version` 做一次乐观校验就够 |
| 8 | 原文哈希幂等去重（7.3.1） | **必需（行为表可降级）** | 防止副本、防止重复建短链，代价只是一列加一个唯一索引。四种状态乘两种姿势的行为矩阵，可以简化为一条：已存在就返回现有短链，带 `replace` 才出新版本 |
| 9 | 每个 agent 一枚令牌、四级 scope、令牌表、CLI 发放、60 秒吊销缓存（7.4） | **可降级** | 77B 是公网地址，写入必须有令牌；身份绑定也很便宜，值得保留。但不需要四级 scope、PG 里的令牌表、relay 和 admin。降级为：服务配置文件里存「令牌哈希对应调用方名和读写标志」，吊销就是改配置再重启 |
| 10 | 限流三档、审计表 `ingest_log`（7.4） | **过度设计（保留一档）** | 只保留「鉴权失败按 IP 限流」和请求体上限，用来防暴力猜令牌。写请求每天几次，用 journald 日志代替审计表就够了 |
| 11 | `/ingest.md`（7.5） | **必需，可降级** | 给 agent 或驻地一份自描述的说明，确实能降低出错率。篇幅从 6–8 KB、14 节压到约 3 KB，另外配一份**翻译提示词模板**（8.2.3） |
| 12 | 4 份 JSON Schema（7.5） | **过度设计** | 只剩一个写端点，`/ingest.md` 里放一个完整示例就足够。服务端的校验本身就是 schema |
| 13 | `/share` 兼容并改为生成骨架（7.6） | **必需** | 旧调用方和旧链接都依赖它。改造只是复用 preview 的骨架代码，几乎没有额外成本 |
| 14 | `reading.articles` 和 `sentences` | **必需** | 文章、分句、译文、注音的落点 |
| 15 | `reading.analyses` | **可降级** | 保留成一张轻量的版本记录表 `(article_id, version, tool, note, created_by, created_at)`，去掉 building/partial/superseded 这套状态机，当前版本由 `articles.current_version` 指明 |
| 16 | `reading.share_aliases` | **过度设计** | 只在两边 10 位 ID 冲突（约 2^40 分之一）或两边存有同一原文时才用得上，而存量只有十几篇。改为在 `articles` 上加一列 `alt_ids text[]`，迁移时手工处理 |
| 17 | 真实时间轴：Edge SentenceBoundary 生成 timeline.json（1.2） | **可降级（推迟）** | 能提升精度，但涉及 `stream_segment` 改造、P0 验证、三端实测，约 1 人日。先在 v3 上用**库里的 kana 字数做权重**改进现有估算（几乎零成本，比汉字字数准），上线一周后看卡片切换的错位是否扎眼，再决定做不做 |
| 18 | 页面 v3：当前句卡片加小字全文（1.3、1.4） | **必需** | 这是用户的核心需求。只有「重读本句、到句尾自动暂停」依赖准确的句末时间，随时间轴一起推迟；点句跳转用估算时间也能用 |
| 19 | ddPC 的 HTTPS 只读读取加本地 SQLite 缓存和预热（4.2） | **必需** | 77B 会睡眠，电视在家里，没有缓存的话，云机一睡家里就读不到文章。这个新事实反而让缓存更有必要 |
| 20 | ddPC 的 outbox、relay 令牌、本地预分配 share_id（4.2、7.4） | **过度设计** | ddPC 侧写入是偶发的。77B 不在线时让调用方稍后重试即可，不值得为此维护一条离线写入队列，以及随之而来的 ID 冲突处理 |

### 8.2 两条路线对比

- **路线 X（现行方案）**：每个 agent 自己完成翻译（自己翻、调用 agy Gemini 或 Claude Code 均可），再通过第 7 节的全套接口写入。
- **路线 Y（驻地方案）**：agent 通过现有的「Claude 协作」通道（SSH 或 Tailscale 到 ddPC，执行 `claude -p`）把整篇交给 **ddPC Claude Code 驻地**。驻地完成以下流程后，向发起的 agent 汇报短链：
  1. 调用 77B 的 `preview`，拿到标准分句和 kana；
  2. 按模板通读全篇，翻译（或做对齐）并校对读音；
  3. 自行核对；
  4. 调用 77B 的 `commit` 一次写入。

#### 8.2.1 「最薄落库方式」的可行性

| 方式 | 可行性判断 |
|---|---|
| **① 驻地直调 77B 的极简写接口**（`preview` 加 `commit`，驻地持有一枚令牌） | **推荐**。两次 HTTPS 请求就能完成；分句、骨架、拼接校验都留在服务端，保证一致；驻地在 Windows 上不需要装 fugashi |
| ② 通过 dbapi（JWT）直接写 reading schema | 不推荐。要在 ddPC 侧自己生成骨架（装 fugashi 和 UniDic、复刻分句和 ruby 对齐），三张表的单事务写入是否被 dbapi 支持也没有核实，JWT 过期还要另外处理；校验逻辑会分散在两处 |
| ③ 驻地生成 JSON 或 SQL 文件，传到 77B 后由服务端脚本导入 | 可行，但需要一条 ddPC 到 77B 的文件通道（SSH 是否打通没有核实），校验仍然要在服务端做一遍，本质上是绕路版的 ① |
| ④ ddPC 直连 77B 的 PG | 不可行。5432 端口不对公网开放，第 4 节已经否掉 |

#### 8.2.2 稳定性

**入库链路上的常驻组件和失败点**：

| 组件或前提 | 路线 X | 路线 Y |
|---|---|---|
| 77B 在线（TtsWeb、PG、fugashi） | 需要 | 需要（**两条路线共同的前提**，因为写入目标就在 77B） |
| 第 7 节的有状态接口（draft、PATCH、pending、revise、覆盖规则、令牌表、审计表） | 需要，代码面大、状态多 | 不需要，只剩 preview 加 commit |
| 每个 agent 各自的翻译手段（模型、agy 登录、会话上下文） | 需要，**三份**，口径各不相同 | 不需要，agent 只负责转交 |
| ddPC 开机 | 不需要 | **需要（新增）** |
| ddPC Claude Code 的 OAuth 有效 | 不需要 | **需要（新增）**：凭据 **2026-12-10 到期**，只剩约 3 周，必须安排续期提醒 |
| Pro 订阅的额度和限流 | 只在 agent 选择调用 Claude Code 时才需要 | **需要（新增）**：和其他「Claude 协作」任务共用一份额度；限流时最坏要等几个小时 |
| 驻地忙闲 | 不涉及 | **需要**：`claude -p` 可以并行跑，主要是额度问题，不是并发问题 |
| 转交通道（SSH 或 Tailscale） | 不需要 | 需要。A003 本来就在 ddPC 上；A002 通过 Tailscale 可达；**A001 到 ddPC 的转交路径需要确认**（東紫乃已有 SSH 转交的先例） |
| ddPC 侧的 outbox 和 relay | 需要 | 不需要（随 8.1 #20 一起砍掉） |

**算不算新的单点**：
- 算，ddPC 驻地是**翻译这一环**的单点。
- 但这个单点**不在播放路径上**：驻地不可用时，文章仍然可以先用 `/share` 存原文（服务端会生成骨架，ruby 照有），立刻能播；译文等驻地恢复后再用 `commit replace` 补上。
- 按一天几篇的业务量，这类失败的代价只是「译文晚到」，可以接受。

**双驻地是否现实**：
- **人工备份现实，自动主备切换不现实，也没有必要**。
- 77B 驻地和 PG 同机、零网络跳，而且**和写入目标处在同一个故障域**：77B 睡着时本来就写不进去，所以它不会额外增加失败点，单从稳定性看甚至优于 ddPC。它的短板是闲置会暂停、有单次运行时长上限（一次入库几分钟，远低于 480 分钟），以及 agent 能否到达它（现有的协作通道在 ddPC 上）。
- 推荐口径：**ddPC 为主驻地，77B 为人工备份**。ddPC 关机或额度用尽时，由用户或 A001 手动在 77B 上执行同一条命令。两边读取**同一份提示词模板**（放在 77B，见 8.2.4），模型口径相同，产出没有差别。
- 两处 OAuth 分别在 12-10 和 12-13 到期，续期要一起管。

**稳定性结论**：
- 路线 Y **减少的**是代码面和状态面：整个第 7 节的有状态部分、三份各不相同的翻译手段、ddPC 的 outbox；
- **增加的**是三个运维前提：ddPC 开机、OAuth 有效、额度够用，而且都只影响入库，不影响播放；
- 综合下来，路线 Y **略好**，前提是做好 OAuth 续期提醒，并且失败时先 `/share` 兜底。

#### 8.2.3 速度（一篇 500–2000 字，从提交到 ready）

| 阶段 | 路线 X | 路线 Y |
|---|---|---|
| 转交和启动 | 无 | SSH 加 `claude -p` 冷启动，约 10–30 秒 |
| 分句和骨架 | draft 约 1 秒 | preview 约 1 秒 |
| 通读、翻译、读音校对 | 视 agent 的模型而定：500 字约 1–3 分钟，2000 字约 3–8 分钟 | 驻地模型固定：500 字约 1–3 分钟，2000 字约 3–8 分钟 |
| 写入 | PATCH 约 1 秒 | commit 约 1 秒 |
| 核对和汇报 | agent 自行核对 | 驻地核对后汇报，约 +0.5–1 分钟 |
| **合计** | **约 2–8 分钟** | **约 2–10 分钟**；额度受限时可能要等几小时，有 `/share` 兜底 |

- 两条路线**处在同一个量级**，路线 Y 只多了转交和汇报的固定开销。不论是哪条路线，**速度都不是选择的依据**。
- **页面读取和播放不受影响**：两条路线都只改变入库方式。播放只需要全文；注音在骨架阶段就生成了；译文属于异步补齐的内容。
- 如果希望「立刻就能听」：先 `/share`（秒级出短链，带注音），再转交驻地补译文。

#### 8.2.4 质量与一致性

- **固定一个驻地、一份模板、一个模型**，风格一致性**明显更好**：人称、语气、人名译名在同一篇内和不同文章之间都能统一。路线 X 下 A001、A002、A003 各用各的模型和人格（Claude、Gemini 混用），同一部剧的对白可能出现三种译名。
- **提示词模板的归属**：
  - **放在哪里**：作为 77B 上 TtsWeb 代码的一部分，以 `/ingest_prompt.md` 公开下发，和 `/ingest.md` 放在一起。驻地每次运行都现取最新版本，ddPC 和 77B 两个驻地自然一致。
  - **怎么版本化**：模板头部写 `prompt_version: 3`，再加一行变更说明。commit 时把 `prompt_version` 和模型名写进版本记录的 `tool` 字段，以后可以按模板版本找出需要重译的文章。
  - **谁维护**：一位指定的维护者（推荐由方案实施方，也就是 77B 侧负责）。改动要经用户点头，因为它决定了全部译文的口径。
  - **内容**：译文风格（待拍板第 5 条）、全篇上下文翻译规矩（修订注记 4、5）、对齐型的「只对齐不重译」（第 31 条）、读音校对要点（同形多读、数词音变、熟字训），以及**术语或译名表**（例如逃げ恥的人名，作为附录持续积累）。
- **模型口径**：现行规矩是用户在每次任务时点名模型。走驻地路线需要**预先定死一个默认模型**，写在模板头部，驻地运行时用 `--model` 传入。用户点名时可以覆盖，覆盖情况记进 `tool`。推荐默认模型列为新的拍板候选 N2。
- **质量风险**：LLM 偶尔会悄悄改动原文，或者漏句、并句。这一点**两条路线都存在**，必须靠服务端 commit 时的「原文拼接等于原文」「对齐型译文拼接等于译文」两条校验兜住。这是简化之后**仍然必须保留**的核心。

**三维结论**：

| 维度 | 结论 |
|---|---|
| 稳定性 | Y 略好：代码和状态少很多；新增的三个运维前提只影响入库、有兜底 |
| 速度 | 持平，都是分钟级，不是选择依据 |
| 质量和一致性 | **Y 明显更好** |

**总评**：Y 更好，但它的主要价值在于「质量一致」加「单一写入方，所以接口可以砍薄」，而不在于提速。

### 8.3 简化后的方案形态

**保留**：
- 第 1–5 节的主体：真源放在 77B 的 `reading` schema；当前句卡片加小字全文；ruby 在入库时预生成；旧链接兼容；SQLite 兜底到 2027-02-28。
- `preview` 和 `commit` 两个写端点，外加一个只读 `GET /api/v1/articles[/{id}]`（ddPC 预热和驻地自查都会用到），以及兼容用的 `/share`。
- 服务端的三条核心校验：原文拼接、对齐型的译文拼接、读音只能是平假名。
- 原文哈希去重：已存在时返回现有短链，带 `replace:true` 时生成新版本。
- 版本留痕：用轻量的版本表，所有版本全部保留。
- `/ingest.md`（约 3 KB），加上 `/ingest_prompt.md` 提示词模板。
- ddPC 的只读缓存和每小时预热。

**砍掉**：
- 两步回填型：draft、PATCH、pending、sid 哈希、`/articles/{id}/draft`；
- revise 端点；草稿和定稿状态机；`VERSION_FROZEN`；
- 覆盖规则：TR_EXISTS、overwrite、OVERWRITE_DENIED，以及行锁串行；
- 四级 scope、relay、admin HTTP、PG 里的令牌表、审计表 `ingest_log`；
- 逐令牌写入限流和 `/t` 限流；
- 4 份 JSON Schema；
- `share_aliases` 表（改为 `articles.alt_ids`）；
- ddPC 的 outbox 和本地预分配 share_id。

**降级**：

| 机制 | 降级后的形态 |
|---|---|
| 错误码 | 从 25 条降到 **8 条**，错误体格式（message、hint、details）不变：<br>`AUTH_FAILED`（401/403，合并缺失、无效、身份不符三种情况）<br>`BODY_INVALID`（400，合并空文本、超长、字段错误）<br>`SPLIT_MISMATCH`（422）<br>`ALIGN_MISMATCH`（422，details 里用 `kind: missing/orphan` 区分）<br>`TR_MISSING`（422）<br>`VERSION_CONFLICT`（409，replace 时 base_version 不是当前版本）<br>`NOT_FOUND`（404/410）<br>`RETRY_LATER`（429/503，DB 不可用或鉴权失败过多）<br>warning 只保留 `TOKENIZER_FAILED`、`TR_SUSPECT`、`SPLIT_NONSTANDARD` 三种 |
| 令牌 | 每个调用方一枚，在服务配置文件里登记「哈希、名字、能否写入」：A001、A002、A003、`ddpc-claude`（写）、`77b-claude`（写）、`ddpc-cache`（只读）。普通 agent 的令牌绑定自身身份；**驻地令牌允许代填 `added_by`**（白名单为 {A001, A002, A003}），因为驻地是代表发起的 agent 入库的 |
| 归属字段 | `added_by` 记发起的 agent；`tr_by` 记执行者（`ddpc-claude`、`77b-claude` 或 agent 本人）；`tr_via` 和 `tr_model` 不变（驻地执行时 `tr_via=claude-code`，对齐型仍为 `provided`），并在版本记录里加 `prompt_version` |
| 修改译文或读音 | 整篇重新 `commit` 并带 `replace:true` 和 `base_version`，生成新版本留痕，**不设单独的 revise 端点**。少量改动由驻地基于 `GET` 取回的现稿修改后重交 |
| 时间轴 | **推迟**。v3 先用 kana 字数做权重来估算；「重读本句、到句尾自动暂停」也随之推迟。v3 上线一周后，由用户判断卡片的错位是否需要修 |
| `reading` 表 | 三张：`articles`（加 `alt_ids`）、`versions`（原 analyses 的轻量版）、`sentences` |

**驻地运行手册**（写进 `/ingest.md`，这里只列步骤，不写实现）：
1. agent 转交一份任务，内容包括原文、标题、发起人，以及可选的随文译文；
2. 驻地拉取 `/ingest_prompt.md`；
3. 调用 `preview`；
4. 按模板通读全篇，翻译或对齐、校对读音；
5. 调用 `commit`；
6. 失败时按错误码表的 hint 自行修正，最多重试 2 次，仍失败就用 `/share` 存原文并向发起方报告原因；
7. 回报短链、句数、模型和 `prompt_version`。

**简化后的分期与人日重估**：

| 期 | 交付 | 人日 |
|---|---|---|
| P0 验证 | 用 fugashi 跑一篇；驻地用真实文章走一遍「preview、翻译、commit 草稿」的模拟（不写库）；核对 analyzer〔待核〕的说法；Edge 边界帧的验证随时间轴一起推迟 | 0.5 |
| P1 建库与迁移 | 三张表；迁移脚本；`/t`、`/share` 改读写 PG，SQLite 兜底；配置文件令牌 | 1.0 |
| P2 入库 | preview、commit（含对齐型、replace、去重）；只读 GET；`/ingest.md` 和提示词模板；驻地运行手册；**由驻地把存量全部翻完**作为验收 | 1.0 |
| P4 页面 v3 | 卡片加小字全文、ruby、两个开关、点句跳转（用估算时间）、kana 加权估算；**前端改用 Vue 3 实现（无构建链、自托管 vue.global.prod.js，播放内核照 player_v2 原样移植），后端继续 Python**（用户 2026-11-18 追加决定，见修订注记 11） | 1.5 |
| P5 ddPC 接入 | 只读缓存和预热（不做 outbox） | 0.5 |
| P6 退役 | 2027-02-28 归档 SQLite | 0.25 |
| **合计** | | **约 4.75 人日**（原方案约 8.25–9.25） |
| （推迟项）P3 时间轴 | 按需再做 | +1.0 |

### 8.4 受影响的待拍板条目（文末清单暂不改动，等用户拍板）

| # | 影响 | 简化后的新口径（作废的给出原因） |
|---|---|---|
| 1 | 不受影响 | — |
| 2 | 不受影响 | — |
| 3 | **改写** | ddPC 经 77B 的 HTTPS 只读 API 读取，加本地 SQLite 缓存和每小时预热；**取消 outbox**，77B 不在线时写入由调用方稍后重试 |
| 4 | **改写** | fugashi+UniDic 打骨架不变；**翻译执行者固定为 ddPC Claude Code 驻地**（按统一模板和统一模型），77B 驻地为人工备份，agent 自己翻只作兜底；仍然没有 API、没有 worker |
| 5 | 不受影响 | （由提示词模板落实） |
| 6 | **改写** | 仍然不设无人值守流水线；触发方式改为「agent 通过 Claude 协作通道转交驻地，驻地当场做完并 commit」 |
| 7 | **改写** | 同页上卡下列不变；「每句后自动停顿」和「重读本句、到句尾自动暂停」**随时间轴推迟** |
| 8 | 不受影响 | — |
| 9 | 已作废（原状） | — |
| 10 | 不受影响 | — |
| 11 | 不受影响 | — |
| 12 | 不受影响 | — |
| 13 | **改写** | 写入仍然需要令牌；改为服务配置文件里每个调用方一枚（只分读和写两级），不建令牌表 |
| 14 | 不受影响 | — |
| 15 | 不受影响 | — |
| 16 | 不受影响（实现降级） | 全部版本保留不变；analyses 降级为轻量的 `versions` 表 |
| 17 | **作废** | 只剩一种写入姿势（preview 加 commit），不再有默认姿势需要选 |
| 18 | **改写** | commit 允许缺译文，但仍然必须显式传 `allow_partial`；补齐方式由 PATCH 改为 `replace` 整篇重交 |
| 19 | 不受影响 | — |
| 20 | **改写** | 重复提交时返回现有短链、不做改动；要修改就带 `replace:true` 整篇重交，生成新版本（不设 revise 端点） |
| 21 | **作废** | 没有逐句回填，也就没有逐句覆盖规则；整篇 replace 时用 `base_version` 做一次乐观校验 |
| 22 | **改写** | 版本一经写入就不再原地修改；所有修改都通过 `replace` 生成新版本；不再有草稿原地写和定稿冻结的区分 |
| 23 | **改写** | `added_by` 记发起的 agent（驻地令牌可以在白名单内代填）；`tr_by` 记执行者（`ddpc-claude`、`77b-claude` 或 agent 本人）；`tr_via` 和 `tr_model` 保留，并新增记录 `prompt_version` |
| 24 | **改写** | 令牌哈希登记在 77B 服务的配置文件里，改配置加重启就是吊销；明文仍然放在各机器的 Secrets |
| 25 | **改写** | 页面读取口仍然免令牌；`/api` 只分读写两级，不设 relay，也不提供 admin HTTP |
| 26 | **改写** | 普通 agent 的令牌仍然绑定身份；驻地令牌允许在 {A001, A002, A003} 白名单内代填 `added_by` |
| 27 | **改写** | `/share` 保留，响应兼容；内部改为「只交原文」形式的 commit（生成骨架、去重、状态 partial）；2 周令牌宽限期不变 |
| 28 | **改写** | 只保留鉴权失败按 IP 限流（每分钟 10 次、封禁 10 分钟）和请求体 ≤512 KB；取消逐令牌写入限流和 `/t` 限流 |
| 29 | **改写** | `/ingest.md` 保留并压缩到约 3 KB，新增 `/ingest_prompt.md` 提示词模板；**取消 4 份 JSON Schema** |
| 30 | **作废** | 不建审计表，写请求记在 journald 结构化日志里（保留 90 天） |
| 31 | **改写（轻）** | 「只对齐不重译」、`via=provided`、两侧拼接校验、留痕都不变；`ALIGN_TR_MISSING` 和 `ALIGN_TR_ORPHAN` 合并为 `ALIGN_MISMATCH`，在 details 里用 kind 区分；修正译文走 `replace`。注意这两个专码是用户定过的，**如果坚持保留两个码，成本可以忽略**，不影响其余简化 |

汇总：**作废 3 条**（17、21、30），**改写 17 条**（3、4、6、7、13、18、20、22、23、24、25、26、27、28、29、31，再加 16 的实现降级），**不受影响 11 条**（1、2、5、8、9、10、11、12、14、15、19）。

**简化后会新增的拍板候选**（暂不编号，拍板时再并入清单）：
- N1：驻地主备，推荐 ddPC 为主、77B 人工备份；
- N2：驻地默认模型，推荐写进模板头部，用户点名时可以覆盖；
- N3：提示词模板的维护者和存放位置，推荐由 77B 侧维护，挂在 `/ingest_prompt.md`；
- N4：时间轴推迟到 v3 上线一周后再评估；
- N5：两处 OAuth 续期提醒（12-10、12-13），推荐各提前 3 天提醒。

### 8.5 明确推荐：混合方案

**推荐采用混合方案**：

| 部分 | 处理 |
|---|---|
| 数据层、页面、读取链路 | 保留原方案（只做表级瘦身） |
| 入库接口 | 按 8.3 简化 |
| 翻译执行者 | 改为驻地方案 |
| 时间轴 | 推迟 |

**判断依据**：
1. **业务量**：一天至多几篇、写入方实际上只剩一个驻地。第 7 节里为并发、续做、多方争写准备的机制，在这个业务量下永远用不上；它们留下的只是代码量和失败面。
2. **质量**：用户要的是「上下文一致、风格统一」的译文。统一驻地加统一模板，是比任何接口规则都更直接的保证。
3. **稳定性**：播放路径上的组件一个都没减（PG 真源、ddPC 缓存都保留），砍掉的全是入库侧的状态。新增的驻地依赖只影响「译文何时到」，而且有 `/share` 兜底。
4. **不选纯原方案**：用 8–9 人日去换并发和续做能力，对这个业务量不划算。
5. **不选「更彻底的简化」**（例如连 PG 迁移、ddPC 缓存都不做，只让驻地生成 JSON 文件）：会丢掉单一真源和离线可读这两项刚需，而 77B 会睡眠的事实恰恰让离线缓存更有必要。

**简化路线的代价，以及什么时候会不够用**：
- **失去断点续做**：如果将来要入库很长的材料（例如整季剧本，单篇接近 5000 字上限，或者需要多次会话才能翻完），整篇重来的成本会变高。到那时可以恢复两步回填型，第 7 节的设计稿还在，可以直接取用。
- **失去细粒度的冲突保护**：如果将来有多方同时编辑同一篇（例如几个 agent 分头校对同一部剧），`replace` 的「最后写入者生效」加一次 base_version 校验就会显得粗糙。届时可以恢复逐句覆盖规则。
- **失去 HTTP 层的细分权限和审计表**：如果令牌要发给 Muse 体系之外的调用方，或者需要追查谁改了什么，journald 日志就不够了，需要恢复四级 scope 和 `ingest_log`。
- **对驻地的依赖**：ddPC 长时间关机、OAuth 过期没有续上、或者 Pro 额度长期紧张时，译文会积压。这时只能临时回到「agent 自己翻」的兜底；这条兜底路径保留了接口能力，但会牺牲风格一致性。
- **时间轴推迟**：如果 v3 上卡片切换的错位确实扎眼（长句、对白停顿多的材料尤其明显），需要补做 P3（约 1 人日），「重读本句」也随之上线。

---

## 9. 备份与本机镜像（第 37、38 条）

### 9.1 导出备份（第 37 条）

- **导出方式**：在 77B 上对真源做 schema 级导出：`pg_dump -n reading -Fc`（只导 `reading` 一个 schema，含三张表与全部版本；数据量小，一次几 MB 以内）。文件名带日期，如 `reading_20261201.dump`。
- **频率与保留**：**每周一次**，与既有的本机四库周备份（周一凌晨）同一节奏顺带执行；**本机与 ddPC `D:\db-backups` 各留一份、每处保留最近 8 份**，更早的自动清掉。另在 P1 迁移完成、批量改版等大动作前后各做一次临时导出（不占 8 份名额、单独标记）。
- **恢复手順**（77B 重建后灌回）：① 在新 PG 里建 `db77database` 与相关角色（同 P1 的建库脚本）；② `pg_restore` 把最近一份 dump 灌回；③ **对账**：三张表逐表行数比对导出时的记录、`articles` 的 share_id 集合一致、随机抽 3 篇逐句比对 `content_hash`；④ 若本机镜像（9.2）比这份 dump 更新，再用 9.2 的行级同步反向补齐差量（以镜像为准逐行 hash 对比、只补缺失/不一致行）；⑤ TtsWeb 配置指回新库，冒烟：打开一篇已知短链 `/t/<id>` 能读能播、`/share` 存一篇测试文成功。
- **防的是什么**：误删、逻辑写坏、需要回滚到某个历史时点——dump 是时间点快照，这是它和镜像的分工（见 9.3）。

### 9.2 本机镜像（第 38 条）

在本机（東紫乃这台 VM）的 PostgreSQL 里建一个与 77B **一模一样的 `reading` schema**，作为真源的单向镜像。方向与 jpdb 正好相反：jpdb 是本机为源、77B 为副本；reading 是 **77B 为源、本机为副本，只下行、永不上行**。

- **落点与 DDL 一致性**：本机新建数据库 `db77database`（与源同名，恢复时少改配置），里面只建 `reading` 一个 schema（drama、jpdb 等其他 schema 不镜像）；属主角色 `reading_owner`（口令存本机 600 权限文件，沿用本机既有规矩）。DDL 与 77B 共用同一份 P1 建表脚本、两边各跑一次；脚本进版本管理，改 DDL 必须两边同跑，跑完比对 `information_schema` 的列清单确认一致。注意两边 PG 大版本不同（77B 17、本机 16.2）：行级同步不受影响，但不要拿 77B 的 pg_dump 直接往本机 restore（版本差异可能报错），这也是同步走行级而非 dump 的原因之一。
- **同步机制**：复用 jpdb 已验证的做法、方向反过来——本机脚本 `pull_reading.py`：经 **dbapi 读** 77B 的三张表（MuseAgent 账号，只读浏览即可）→ 按主键顺序**批量写**本机镜像 → 逐行 `content_hash` 对账（与 sync_77b.py 同一套哈希口径），不一致才覆盖。删除只可能是软删除（`deleted_at` 标记随行同步），设计上没有硬删除，镜像不需要处理行消失。
- **触发时机（推荐两者结合）**：① **每日定时全量对账 pull 一次**（数据量小，全量拉取＋对账是秒级到分钟级）；② **入库收尾顺带触发一次**：驻地运行手册（8.3）的回报步之后，由发起 agent 顺手跑一次 pull，镜像延迟从「最多一天」压到「几分钟」。最坏情形（只有定时在跑）镜像落后真源不超过一天，对冷备定位可以接受。
- **镜像定位**：**只读副本，不承接任何写入**——没有服务指向它，TtsWeb、页面、ddPC 缓存都不读它；用途只有两个：灾难恢复的数据底本，和本机侧临时查询（查库存、核对内容时不打扰 77B）。
- **晋升与回切（结论：不做在线晋升，只作恢复底本）**：不建议把镜像临时晋升为在线真源，理由有三——① 访问路径不通：TtsWeb 服务跑在 77B，它连不到本机 PG（本机 PG 不对外直连、本机 VM 也没有可被 77B 主动连入的通道），ddPC 侧更是只认 77B 的 HTTPS API；② 真源短时不可用时，播放侧已有兜底（ddPC 本地缓存＋SQLite 只读兜底到 2027-02-28），不需要镜像顶上去当写库；③ 为「几篇文章的朗读服务」做跨网在线故障转移，复杂度与第 8 节刚砍掉的东西同级，不划算。**替代做法（恢复手順）**：77B 修好或换新驻地后，按 9.1 的恢复手順灌回——先 restore 最近 dump，再以镜像为准行级补齐到最新；对账通过后服务指回，即完成回切。因为镜像从不接写入，恢复期间产生的新文章按第 3 条口径由调用方在真源恢复后重交，**不存在双写合并问题**，回切就是「灌回＋对账＋指回」三步。

### 9.3 备份与镜像的分工（两者并存，不互相替代）

| | 导出备份（37） | 本机镜像（38） |
|---|---|---|
| 形态 | pg_dump 时间点文件，本机＋ddPC 各一份 | 本机 PG 里的活 schema |
| 防的风险 | 误删、写坏、要回滚到历史时点 | 77B 库损坏/机器丢失后快速拿到近最新数据 |
| 新鲜度 | 最多落后一周 | 最多落后一天（入库顺带触发时几分钟） |
| 恢复方式 | pg_restore 灌回＋镜像补差量 | 作恢复底本，行级灌回新真源 |

## 待用户拍板（已全部确认）

> 状态：2026-11-18 用户逐条确认，38 条全部按推荐口径通过（修订注记 10）。方案定版，实施等用户下一步指示。

| # | 事项 | 推荐 | 状态 |
|---|---|---|---|
| 1 | schema 名 | **`reading`**（备选 `ondoku`） | 已确认 |
| 2 | PG 落点 | **(B) 77B db77database 单库为真源** | 已确认 |
| 3 | ddPC 读取方式 | **经 77B TtsWeb 的 HTTPS 只读 API，加本地 SQLite 缓存和每小时预热；取消 outbox**，77B 不在线时写入由调用方稍后重试；不做 PG 直连或隧道（混合方案改写） | 已确认 |
| 4 | 入库路线与执行者 | **fugashi+UniDic 打骨架不变；翻译执行者固定为 ddPC Claude Code 驻地**（统一模板、统一模型），77B 驻地为人工备份，agent 自己翻只作兜底；仍无 API、无 worker（混合方案改写） | 已确认 |
| 5 | 译文风格 | **自然中文意译**，必要时括注直译（例如惯用句），不逐词硬译 | 已确认 |
| 6 | 解析触发方式 | **不设无人值守流水线**：触发方式改为 agent 经 Claude 协作通道转交驻地、驻地当场做完并 commit（混合方案改写） | 已确认 |
| 7 | 页面布局与跟读停顿 | **同页上卡下列**；「每句后自动停顿」做成可选开关，**默认关**——其中「每句后自动停顿」与「重读本句、到句尾自动暂停」**随时间轴推迟**（混合方案改写） | 已确认 |
| 8 | 译文、假名的默认显示 | **两者默认都显示**；「译」开关支持模糊后点开揭示 | 已确认 |
| 9 | 词性配色表 | **已取消**（用户 2026-11-18 决定：不做词性色下划线，本条作废；编号保留以免其余条目序号变动） | 已确认（作废） |
| 10 | 是否建 `reading.timings` 缓存表 | **暂不建**，P4 上线后视体验再定 | 已确认 |
| 11 | 77B 的 27 字测试文章 | **迁移后软删除** | 已确认 |
| 12 | SQLite 兜底保留期 | **到 2027-02-28**，之后归档（不删除） | 已确认 |
| 13 | `/share` 及新写入接口加令牌 | **加**（77B 是公网地址）；改为服务配置文件里每个调用方一枚、只分读写两级，不建令牌表；明文放在 Secrets 里（混合方案改写） | 已确认 |
| 14 | 新页面上线方式 | **先挂 `/v3` 并行试用一周再转正**，v2 留盘备份 | 已确认 |
| 15 | TtsWeb 访问 PG 的方式 | **服务直连（专用角色 `reading_rw`）**，dbapi 只给人和 Agent 做管理 | 已确认 |
| 16 | 解析旧版本保留策略 | **全部保留**（体积可以忽略），可以回滚、可以比对差异；实现降级：analyses 改为轻量 `versions` 表（混合方案） | 已确认 |
| 17 | agent 默认采用哪种入库姿势 | **已作废·混合方案**（只剩一种写入姿势 preview＋commit，不再有默认姿势需要选；保留占位） | 已确认（作废） |
| 18 | commit 是否允许缺译文 | **允许，但必须显式传 `allow_partial:true`**；不传时缺一句就整篇拒绝；补齐方式由 PATCH 改为 `replace:true` 整篇重交（混合方案改写） | 已确认 |
| 19 | 去重范围 | **全库按原文哈希去重**（不区分 agent），同一原文只存一份、共用一个短链 | 已确认 |
| 20 | 重复提交已定稿的文章 | **原样返回已有短链，不做任何改动**（`duplicate:true`）；要改带 `replace:true` 整篇重交、生成新版本，不设 revise 端点（混合方案改写） | 已确认 |
| 21 | 已有译文的覆盖规则 | **已作废·混合方案**（没有逐句回填，也就没有逐句覆盖规则；整篇 replace 时用 `base_version` 做一次乐观校验；保留占位） | 已确认（作废） |
| 22 | 定稿后的修改方式 | **版本一经写入就不再原地修改；所有修改都通过 `replace:true` 整篇重交生成新版本**；不再有草稿原地写和定稿冻结的区分（混合方案改写） | 已确认 |
| 23 | 翻译来源的标记 | **`added_by` 记发起的 agent；`tr_by` 记执行者**（`ddpc-claude`、`77b-claude` 或 agent 本人）；`tr_via`、`tr_model` 保留，并新增记录 `prompt_version`；驻地令牌可在白名单内代填 added_by（混合方案改写） | 已确认 |
| 24 | 令牌的发放、存放与吊销 | **每个调用方一枚，令牌哈希登记在 77B 服务的配置文件里**（改配置＋重启即吊销）；明文放各机器 Secrets 的 `TTSWEB_TOKEN`；不建令牌表、不开 HTTP 发放口（混合方案改写） | 已确认 |
| 25 | 读写分离的边界 | **页面读取口（/t、/?s）保持免令牌；`/api` 只分读写两级**，不设 relay、不提供 admin HTTP（混合方案改写） | 已确认 |
| 26 | added_by 防冒名 | **普通 agent 的令牌绑定自身身份**：added_by、tr_by 按令牌写入，请求体 `by` 与令牌不一致时返回 403；**驻地令牌允许在 {A001, A002, A003} 白名单内代填 added_by**（混合方案改写） | 已确认 |
| 27 | `/share` 的去留 | **保留路径，响应兼容**；内部改为「只交原文」形式的 commit（生成骨架、去重、状态 partial）；令牌**宽限 2 周**后强制（混合方案改写） | 已确认 |
| 28 | 公网限流数值 | **只保留鉴权失败每 IP 10 次/分、超过封 10 分钟；请求体 ≤512KB**；取消逐令牌写入限流与 `/t` 限流（混合方案改写） | 已确认 |
| 29 | 调用说明的发布方式 | **`/ingest.md` 公开挂在服务根路径并压缩到约 3KB，新增 `/ingest_prompt.md` 提示词模板；取消 4 份 JSON Schema**；agent 的记忆里只记这一个入口（混合方案改写） | 已确认 |
| 30 | 写请求审计日志 | **已作废·混合方案**（不建审计表；写请求记 journald 结构化日志，保留 90 天；保留占位） | 已确认（作废） |
| 31 | 现成译文的对齐口径 | **只对齐不重译**：`tr` 取自随文译文原样、`via=provided`（比 `import` 更贴切，`import` 只留给存量迁移）；句数不一致时允许 agent 合并/拆分并在版本记录的 `tool.align` 留痕；原文与译文两侧拼接校验都不过则整篇回滚；对齐错误码按简化建议**合并为 `ALIGN_MISMATCH`**（details 里用 kind 区分 missing/orphan）——此项系按第 8 节简化建议调整，**用户原定的两个专码口径如需保留可恢复**（成本可忽略）；译文明显有误先标注上报、修正走 `replace` 整篇重交（见 7.1.3、8.4） | 已确认 |
| 32 | 翻译驻地主备 | **ddPC Claude Code 驻地为主、77B 驻地为人工备份**，agent 自翻兜底（混合方案新增，原候选 N1） | 已确认 |
| 33 | 驻地默认模型 | **写进 `/ingest_prompt.md` 模板头部**，用户点名时可覆盖（混合方案新增，原候选 N2） | 已确认 |
| 34 | 提示词模板维护 | **由 77B 侧维护，挂 `/ingest_prompt.md`**，带版本号（混合方案新增，原候选 N3） | 已确认 |
| 35 | 时间轴评估时机 | **推迟到 v3 上线一周后再评估**是否补做（补做约 +1 人日）（混合方案新增，原候选 N4） | 已确认 |
| 36 | Claude 登录续期提醒 | **两处 OAuth（ddPC 2026-12-10、77B 2026-12-13）各提前 3 天提醒续期**（混合方案新增，原候选 N5） | 已确认 |
| 37 | `reading` 导出备份 | **每周一次 `pg_dump -n reading -Fc`（与本机四库周备份同节奏），本机与 ddPC `D:\db-backups` 各留一份、每处保留最近 8 份**；大动作前后另做临时导出。恢复：pg_restore 灌回新库→逐表行数＋share_id 集合＋抽样 content_hash 对账→镜像补差量→冒烟短链可读可播（用户 2026-11-18 拍板追加，见 9.1） | 已确认 |
| 38 | 本机镜像 | **在本机 PG 建同名库 `db77database`、只含与 77B 一模一样的 `reading` schema（共用同一份 DDL 脚本），77B 为真源单向 pull 同步**：本机脚本经 dbapi 读、批量写、逐行 content_hash 对账（sync_77b.py 的反向版）；每日定时全量一次＋入库收尾顺带触发一次；镜像只读、不承接写入，定位灾难恢复底本＋本机查询；**不做在线晋升**（访问路径不通、播放侧已有缓存兜底、复杂度不划算），77B 出事时以「dump 灌回＋镜像补差量＋对账指回」恢复，无双写合并问题；与第 37 条并存不互替（用户 2026-11-18 拍板追加，见 9.2、9.3） | 已确认 |
