# TASK6 第二期·网页保存认证设计（PHASE2-AUTH-DESIGN）

日期：2026-11-19 晚
任务书：`PHASE2-AUTH-DESIGN-PROMPT.md`
定稿：`TASK6-PHASE2-PLAN.md`。本设计替换定稿 §1.5 的认证口径，回写由 operator 负责。
本次只读代码和文档，没有改代码，没有改定稿，也没有碰生产。只做了一次只读核对：本机 `GET /health`，以及对 `/api/reading/ingest` 发了一个空 POST，返回 404。

---

## 0. 结论

**推荐：「添加文章」做成 agnes-service（8793）现有页面里的一个页签，同时开一个可以直接收藏的 `/add` 地址。用户沿用自己现有的 8793 账号登录（会话复用现成的 refresh Cookie）。保存走现有的 `POST /api/reading/ingest`：该账号开通 `reading_ingest='web'` 后，用 JWT 直接提交标题和原文。agnes-service 进程用 `TTSWEB_TOKEN` 代调 TtsWeb 的 `/share` 拿到短链，并在同一个作业里接着补译。**

- 人这一侧：口令就是用户每天登录 8793 管理页用的那个，浏览器密码管理器已经替它保存、自动填充。登录后 refresh Cookie 能维持 7 天，期间有活动就一直续，最长到 30 天会话上限，所以手机上几乎不用再输口令。不存在「短口令不安全」的问题：撞库有现成的账号锁定兜底，口令强度由密码管理器保证。
- 机器这一侧：复杂令牌 `TTSWEB_TOKEN` 只在 agnes-service 进程里，经 `127.0.0.1` 发给 TtsWeb。不进页面，用户也不经手。
- 新增量很小：TtsWeb 侧**不需要为网页通道写任何代码**，只在令牌白名单里加一项。agnes-service 侧改约 30 行后端代码、加一个页签（约 80–120 行前端）、加一条路由别名、加一个配置键。另外**删除**一条已经写好但不再需要的旁路（`X-Ingest-Key`）。不新增服务、中间件、反代或账号体系。
- 此前审核指出的「/add 与主站共用 refresh Cookie 会互顶」，**在本形态下不成立**。那个问题的前提是两边登录两个不同的账号（ReaderNew 与 admin），而本设计里只有一个账号、一个页面、一个会话（见 §4.1）。

---

## 1. 核对过的事实（设计依据）

| # | 事实 | 出处 |
|---|---|---|
| F1 | 生产 8793 跑的还是第一期（`/health` 里没有 `ingest` 段，`POST /api/reading/ingest` 返回 404）。第二期代码只在工作区构建里（`bin/Release` 18:59），**没有 publish**。所以本设计改的是「未上线的第二期代码」，没有线上兼容包袱。 | 本机只读核对；`publish/` 16:34 |
| F2 | 第二期的 `POST /api/reading/ingest` 已经实现了「JWT 认证 → 准入 → 先落作业行 → 同步 `/share`（上限 8 s）→ 202 回 share_id 与 `/v3?s=` 短链 → 后台 worker 补译」，只是现在只对 `reading_ingest='agent'` 的账号开放。**用户建议里的「服务端节点」其实已经写好了，就是这个端点。** | `src/IngestService.cs:90-179` |
| F3 | 网页通道现有的写法是旧口径：TtsWeb 服务端带 `X-Ingest-Key` 头（`INGEST_WEB_KEY`），以服务账号 `INGEST_WEB_USER` 的身份提交，**只接受 share_id**（按旧设计，原文由 TtsWeb 自己存）。新形态下这条路没有调用方。 | `src/IngestService.cs:50-64,107-110`；`tests/p2_accept.py:164-261` |
| F4 | `users.reading_ingest`（`none/web/agent`）这个列、CHECK 约束、AdminApi 和管理页的下拉框、CLI 都已经做好，有审计。 | `auth_schema.sql:169-186`；`src/AdminApi.cs:38-53,338-395`；`index.html:1023-1076`；`src/UserCli.cs:198-219` |
| F5 | refresh Cookie：名为 `agnes_rt`，`Path=/api/auth`，`HttpOnly; SameSite=Strict`，在 https 下加 `Secure`。7 天滑动续期，会话家族上限 30 天。登录时会新建一个家族，**不会吊销该用户的其他会话**。轮换有 10 s 宽限，用来应对多标签页同时刷新。 | `src/AuthService.cs:34-46,429-430,457-518`；`Program.cs:344-349` |
| F6 | 管理页 `index.html`：access token 只放在内存里；页面加载时先 refresh 恢复会话；遇到 401 时单飞刷新后重试；提前 1 分钟自动续期。已经有手机 viewport 和页签结构（文本/图片/视频/我的账户/管理）。CSP 是 `connect-src 'self'`、`frame-ancestors 'none'`。 | `index.html:5,95-102,311-381,440-470`；`Program.cs:70-78` |
| F7 | 登录防护：同一账号连续失败 5 次锁 15 分钟；同一 IP 15 分钟内失败 20 次则节流；被锁 3 次封 IP（回环地址豁免封禁）。因为网关从回环转发，公网来访的 IP 一律是 127.0.0.1（P2R-19）。 | `src/AuthService.cs:47-51,293-315,362-439` |
| F8 | CSRF 模型：服务端从不响应 CORS；带 Cookie 的端点要求 `Content-Type: application/json`；业务端点只认 `Authorization: Bearer`（来自内存，不来自 Cookie）。 | `Program.cs:116,326-331`；`DESIGN.md` |
| F9 | TtsWeb 令牌：`tokens.json` 只存哈希（`{name:{sha256,scope,can_act_for}}`），明文放在 `tokens/<name>.txt`（0600）。目前**还没有 `agnes-service` 条目**。`resolve_added_by` 只允许代填 `can_act_for` 里列出的名字。令牌在启动时加载，改了要重启。 | `tts-web/auth.py`；`tokens.json`（只读条目名，没有读值） |
| F10 | TtsWeb `/share`：按规范化后的原文哈希去重，命中就返回已有的 id（幂等）。原文上限 5000 字，与 agnes 的 `MaxText=5000` 一致。在宽限期（`grace_until=2026-12-02`）内可以匿名写。它返回的 url 是相对路径 `/?s=`。 | `tts-web/tts_web.py:53,545-597`；`config.json` |
| F11 | agnes-service 侧还没有「TtsWeb 公网地址」配置。现在 202 返回的 `url` 是相对路径 `/v3?s=…`，在 8793 页面上直接用会指到 8793 自己。 | `src/IngestService.cs:177-178`；grep 无 `PUBLIC_URL` |
| F12 | 8791 和 8793 是同一台机器、同一个 OS 用户，共用 PG（db77database）。TtsWeb 读播放数据**不依赖** 8793。 | `PHASE2-REVIEW.md` §2.2；`agnes-service.service` |

---

## 2. 候选方法比较

评判顺序：先看简单可靠、复用现有资产，再看用户负担和安全强度。

| 案 | 做法 | 新增组件 / 改动量 | 用户操作负担 | 安全强度 | 主要失败模式 | 结论 |
|---|---|---|---|---|---|---|
| **R：8793 页签 + 现有 ingest 端点放开 web 通道（推荐）** | 8793 管理页加「添加文章」页签和 `/add` 别名；JWT 提交原文；agnes 用 `TTSWEB_TOKEN` 代调 `/share` 并补译 | **无新组件**。后端约 30 行（Caller 分支、web 通道收 text），删掉 `X-Ingest-Key` 分支；前端一个页签；一个配置键；TtsWeb 只改 `tokens.json` 一项 | 与现在用管理页一样：密码管理器自动填充，会话 7～30 天，平时打开即用 | 口令强度由密码管理器保证；有账号锁定；令牌只在服务端；不开 CORS；同源 Cookie | 8793 挂了就不能保存（播放不受影响）；公网 IP 全是回环，别人可以把账号锁 15 分钟（与现有管理页相同，属于 DoS，不会泄露） | **推荐** |
| U：用户建议的字面形态，另起一个「服务端节点」 | 独立前端页加独立后端节点，节点验 JWT 后代调 `/share`，再另行触发 ingest | 多一个进程或服务、一份部署，要配 JWT 验签密钥或回调 8793 内省；保存和补译分在两个进程里，要做补偿 | 同 R | 同 R，另外**对称 JWT 密钥要扩散**到节点（HS256 拿到就能签 admin，见 PHASE2-REVIEW S.2 D1a） | 「已经 share、补译没建上」这种裂脑；多一个进程要看护 | 淘汰：R 就是它的等价实现，节点由 8793 自己兼任 |
| D2+C：8791 上的独立 /add，经 TtsWeb 同源反代到 8793（上一轮审核的推荐） | TtsWeb 加约 60–80 行反代（5 条路由），agnes 加专用 save 端点 | TtsWeb 反代、反代日志脱敏、Cookie 落在 8791；agnes 新端点 | 要在 8791 另存一条密码（保险库按源匹配），会话与主站分开 | 中：8791 播放页**没有 CSP、有 `v-html`**，任何一处 XSS 都能借同源反代拿到会话 | 反代是新的故障点；TtsWeb 本应只管播放，反代把认证面引了进来 | 淘汰：比 R 多一个反代组件，换来的只是地址栏在 8791，而分离原则恰恰不要求这一点 |
| A：8791 页面跨源直调 8793（CORS） | 8793 开 CORS 并允许带凭据 | CORS 中间件全套，外加一组独立 Cookie 名的认证路由 | 同 D2+C | 低：改变现有 CSRF 模型，依赖 PSL 现状 | Cookie 互顶，PSL 一变就悄悄失效 | 淘汰（审核已判为 blocker） |
| P：TtsWeb 上做 Passkey（WebAuthn） | /add 页用指纹或面容登录 | 在 TtsWeb 新建一套凭据注册、存储、校验和会话（新库、新表、新会话 Cookie） | 最低（点一下指纹） | 高 | 换手机或清浏览器就要重新注册，还要有恢复路径；新建了一套账号体系 | 淘汰：违背「不建新账号体系」，复杂度最高。如果以后真想用 Passkey，应该加在 8793 的登录上，与本设计不冲突 |
| T：TOTP 六位码（TtsWeb 校验） | 每次保存输一次认证器 App 里的动态码 | TtsWeb 存 TOTP 种子、校验、防重放、节流 | 每次都要切到认证器 App 抄 6 位数，手机上来回切 App | 中高 | 种子丢失；时钟漂移 | 淘汰：新机制，而且每次保存都要多一步 |
| K：设备配对长期 Cookie（TtsWeb） | 先一次性配对（扫码或一次性链接），之后在 8791 /add 下发长期设备 Cookie | 配对流程、设备清单与吊销、会话校验，全部新写在 TtsWeb | 低（配对后免输） | 中高 | 配对链接泄露；吊销要做管理面 | 淘汰：这等于在 TtsWeb 上重造一遍 8793 已有的 refresh Cookie 机制 |
| ①②（已被用户否决） | 页面带 TtsWeb 写令牌，或用户自选保存口令 | — | — | — | — | 不再考虑 |

**为什么选 R 而不是更复杂的方案**：用户提出的矛盾是「人记不住长口令、机器令牌不能进页面」。8793 现有的「登录一次 → httpOnly refresh Cookie 维持会话 → 内存里的短效 JWT」本身就是这个矛盾的标准解法，而且已经上线，用户每天都在用。剩下要做的只是让这套会话能触发「服务端持令牌存原文」，而这个服务端动作（F2）也已经写好了。其他方案要么重造会话机制（P/T/K），要么多一个转发组件（U、D2+C），换来的东西 R 都已经有了。

---

## 3. 推荐方案的形态

```
手机 / 电脑浏览器
  │  https://8793-<id>.instabox.tech/add          （收藏或加到主屏；也可以从管理页点「添加文章」页签）
  │  ① 打开页面：用 refresh Cookie（agnes_rt，同源）恢复会话 → 内存里拿到 JWT（30 min）
  │     没有会话时就地出登录框（密码管理器自动填充），登录走现成的锁定和节流
  │  ② 填标题和原文 → POST /api/reading/ingest   Authorization: Bearer <JWT>   JSON {title,text,lang}
  ▼
agnes-service（8793，同一进程）
  │  JwtBearer 验签 → RequireUser 回库核对（is_active / token_version / role，10 s 缓存）
  │  → reading_ingest == 'web'？否则 403 ingest_forbidden（不扣额度）
  │  → 校验入参 → LimitService.Admit(kind=ingest)（429 当场返回）
  │  → 先落 ingest_jobs 一行（含 source_text）→ 同步 POST http://127.0.0.1:8791/share
  │        Authorization: Bearer $TTSWEB_TOKEN，{text,title,lang,by:"web",added_via:"web"}（上限 8 s）
  ▼
TtsWeb（8791）
  │  令牌 agnes-service → can_act_for 里有 web → 存进 reading.articles（partial）→ {id}
  ▲
agnes-service
  │  回填 share_id → 入队 worker → 202 {job_id, share_id, url:<8791 公网>/v3?s=<id>}
  ▼
浏览器：显示短链（点开在 8791 播放页，马上能听原文），轮询 GET /api/reading/jobs/{id} 看补译进度
  （后台：worker 依次 preview → enrich → fixes 核对 → commit replace → ready；失败按 15m/1h/4h 重试，仍不行转 L2）
```

---

## 4. 逐项落实（任务书问题 1–7）

### 4.1 页面落点与 refresh Cookie

**结论：页面放在 8793 的现有单页 `index.html` 里，做成新页签「添加文章」；另加路由别名 `GET /add`，返回同一个 `index.html`，前端看到 `location.pathname === "/add"` 就在登录后直接打开这个页签。不另起 `add.html`。**

理由：
- 登录、恢复会话、401 单飞刷新、提前续期、登录错误文案（含锁定剩余时间）这些逻辑 `index.html` 都已经有了（F6），页签直接复用，**一行认证代码都不用新写**。如果另起独立页面，就要复制一遍这套逻辑（约 150 行），以后两处还要一起维护。
- 页面已经适配手机（有 viewport、单列卡片）。`/add` 可以收藏或加到手机主屏，打开就是保存表单，符合「独立入口」的使用感受。
- 符合分离原则：8791 播放页一行都不改，不加按钮，也不加链接。保存页与播放页在不同的服务、不同的源。

**「共用 refresh Cookie 会互顶登录」在本形态下不成立：**
- 那个风险的前提（PHASE2-REVIEW §2.1-2）是 /add 登录 ReaderNew、主站登录 admin，**两个不同账号写同一个主机级 Cookie**，后登录的覆盖前一个。
- 本设计里，「添加文章」就是主站的一个页签，用的是**同一个账号、同一个会话、同一个 Cookie**。没有第二个身份可以被顶掉。在 `/add` 登录就等于登录了主站，反过来也一样，这正是想要的「天然携带登录会话」。
- 剩下一个小边角，与本设计无关、现在就存在：两个浏览器标签页（例如一个开管理页、一个开 `/add`）恰好在同一时刻刷新，后到的那个会收到 `already rotated` 401，而 `/api/auth/refresh` 在 401 时会**清掉 Cookie**（`Program.cs:135`），导致另一个标签下次刷新时被登出。触发窗口很小（只有两个标签同时刷新时才会撞上），后果只是要重新登录一次。实施时**可选**顺手修：`already rotated` 这种情况下不清 Cookie（见 §7 第 9 条），这样两个标签页并存时也不会互相登出。

### 4.2 保存动作的执行点与凭据

- **执行点**：agnes-service 进程，即 F2 里现成的 `IngestApi.Submit` → `TtsWebClient.Share`。
- **凭据**：
  - 浏览器 → 8793：`Authorization: Bearer <access JWT>`。JWT 由登录或 refresh 换来，只放在 JS 内存里，30 分钟有效；refresh 令牌是 httpOnly Cookie，JS 读不到。
  - 8793 → 8791：`Authorization: Bearer $TTSWEB_TOKEN`。值放在 agnes-service 的 `.env` 里，键名 `TTSWEB_TOKEN`，权限 0600，已在日志脱敏名单（`src/Upstream.cs`）。在 TtsWeb 侧登记为 `tokens.json` 的 `agnes-service` 条目（只存哈希），明文备份在 `tts-web/tokens/agnes-service.txt`（0600）。只经 `127.0.0.1` 传输。
- **校验方式**：8793 本地验 JWT（HS256，`JWT_SECRET` 只在 8793），再回库核对 `is_active`、`token_version`、`role`、`reading_ingest`（`RequireUser`，10 s 缓存）。改口令、停用账号、撤销 `reading_ingest` 后，最长 10 s 生效。TtsWeb 不碰 JWT，也不拿 JWT 密钥。
- **为什么不进页面、不经手**：页面只拿得到自己账号的短效 JWT。`TTSWEB_TOKEN` 从不出现在任何 HTTP 响应里，页面代码里没有它，用户也不需要知道它。
- **短链**：TtsWeb 在 8791 公网上，而页面在 8793，所以 202 响应里要给**绝对地址**。新增配置键 `TTSWEB_PUBLIC_URL`（例如 8791 的公网根地址，只记名称），响应的 `url` 拼成 `<TTSWEB_PUBLIC_URL>/v3?s=<id>`。没配的时候退回相对路径，保持 agent 通道的现行行为。

### 4.3 归属记账（定值）

| 字段 | 定值 | 说明 |
|---|---|---|
| reading.articles `added_by` | **`web`** | 与定稿 §1.2 和 §1.5 的旧口径**一致，不改**。reading 库只记「经网页通道」，不记 agnes 的账号名，这样就不把 agnes 的账号体系映射进 TtsWeb（守 §1.2「不做跨体系映射」），以后加减账号也不用改 TtsWeb 配置。 |
| reading.articles `added_via` | **`web`** | 不变 |
| 「谁点的保存」 | 由 agnes 记：`ingest_jobs.user_id/username`、`call_log`（kind=`ingest`，detail 写 `ch=web;by=web`） | 登录账号名有据可查，只是不写进 reading 库 |
| `ingest_jobs.channel` | `web` | 沿用；继续沿用 15m/1h/4h 的延迟重试策略（`NotReady` 按 channel 分支） |
| TtsWeb 令牌 | `agnes-service`：`scope=write`，`can_act_for = {A001, A002, A003, web}` | **比 DF-11 多一个 `web`**。原来写着「网页通道不经此令牌」，现在要经过它，所以加上 |

### 4.4 补译触发

- **结论：不需要单独「触发」。保存和补译是同一个作业**：`Submit` 先写作业行（含原文），再同步 `/share`，然后入队，由 worker 接着做 preview、enrich、commit。旧口径里「TtsWeb 存完再经 localhost 调编排端点」那一跳没有了，「保存成功但触发失败」这一类裂脑**在结构上就不存在**了。
- 失败处理（全部是现有代码的行为，F2）：

| 情况 | 结果 |
|---|---|
| 准入失败（403 或 429） | 什么都没存。页面保留草稿并提示原因 |
| 同步 `/share` 失败或超时（TtsWeb 或 PG 暂时不可用） | 作业行已经落库并保存了原文；202 里 `share_id=null`；worker 按 T 态退避（60s/5m/15m/1h），最长重试 24 h。页面显示「已收下，TtsWeb 暂不可用，稍后自动入库」，并继续轮询作业 |
| share 已成功、回填 share_id 时数据库出错 | worker 重新执行 `/share`。TtsWeb 按原文哈希去重，返回同一个 id（F10），是幂等的 |
| enrich 不完整或失败 | 文章保持 partial，照常可以朗读。按 15m/1h/4h 整篇重做，仍不成功就转 L2（A001 巡检时用 agy 处理） |
| agnes-service 重启 | `RecoverRunning` 把 running 状态的作业改回 retry，到期后重新入队 |

- 页面上的状态：直接用 `GET /api/reading/jobs/{id}`（已有，按属主过滤）。作业的 `status/stage/result.article_status` 已经够区分「处理中、已完成、译文待补」。页签里可以顺带列出 `GET /api/reading/jobs?mine=1` 的最近几条（已有）。

### 4.5 准入与防护

- **谁可以保存：`reading_ingest='web'` 的账号**，也就是用户自己日常用的那个账号。由 operator 在管理页的下拉框里开通（已有，有审计）。**不加列，不改表结构。**
  - 不采用「任意登录用户都能存」：库里还有 MuseAgent、ReaderNew 等账号，不应该因为其中任何一个口令泄露就能往 reading 库写文章。开关已经现成，用上它不增加任何复杂度。
  - `reading_ingest` 只有一个值。用户账号设为 `web`，MuseAgent1/2 保持 `agent`，互不影响。
  - ReaderNew（id 31）不再参与网页通道，维持定稿「保留不动」。
- **web 通道的入参收窄**（在同一个端点里按通道分支）：只收 `title`（必填，≤120）、`text`（必填，≤5000）、`lang`（ja/zh/en，默认 ja）。**不接受 `share_id` 和 `rate`**，`added_by`、`added_via` 由服务端写死成 `web`。这样网页凭据碰不到 replace 或指定文章之类的编排能力。
- **额度**：kind=`ingest`，一篇准入一次，按账号现有的 `rate_per_minute`、`daily_quota` 计（默认 10 次/分钟、200 次/天），单人使用够用。日额度用完时直接 429，什么都不存，草稿留在页面上（不采用审核 S.4「额度用完也先存原文」的变体，因为单人用不会碰到这个上限，没必要加分支）。
- **CSRF**：不需要新做。保存端点只认 `Authorization: Bearer`，令牌来自 JS 内存，浏览器不会自动附带，跨站请求拿不到它。带 Cookie 的认证端点已经有 JSON Content-Type 门槛、`SameSite=Strict`，并且服务端不响应 CORS（F8）。本设计**不开 CORS，不改 CSRF 模型**。
- **登录防护：直接复用，不新造**（F7）。账号锁定、IP 节流、IP 封禁、登录审计都由 `/api/auth/login` 提供。已知限制（P2R-19，管理页现在也有）：公网来访都显示为 127.0.0.1，别人输错几次口令就能把账号锁 15 分钟，或者触发全站登录节流。这只是让人暂时进不来，**不会导致口令泄露或越权**。对单人工具接受这个风险，文档里写明即可；页面会如实显示锁定的剩余时间（`loginErrors` 和 `retry_after` 已经实现）。
- **XSS 面**：页签里所有回显（标题、错误、trail、短链）都用 `textContent` 或 `el()` 生成，不用 `innerHTML`。沿用 `index.html` 现有的 CSP（`connect-src 'self'`、`frame-ancestors 'none'`）。8791 播放页没有 CSP 的问题与本链路无关：会话在 8793 源上，8791 的 XSS 拿不到它。这也是这个方案相对 D2+C 的安全优势。

### 4.6 降级行为

| 故障 | /add 能否保存 | 播放与阅读 |
|---|---|---|
| 8793（agnes-service）不可用 | **不能**。页面打不开或报错，用户稍后再试；页面内的草稿可以存进 localStorage，保存成功后清除（见 §7 第 5 条） | **不受影响**。8791 独立读库，不依赖 8793（F12） |
| 8791（TtsWeb）不可用 | **能收下**：作业带原文落库，返回 202 且 `share_id=null`，TtsWeb 恢复后 24 h 内自动入库；只是当场拿不到短链 | 播放不可用（与本设计无关） |
| PG 不可用 | 不能（登录和作业表都在 PG） | TtsWeb 按现有逻辑回落 SQLite |
| Gemini 和 Agnes 都不可用 | 能保存，拿到短链，文章是 partial | 能听原文；译文由重试或 L2 补上 |

agent 通道不经过 8793 时，可以照旧用自己的 TtsWeb 令牌直接调 `/share`，不受上面任何一项影响。

### 4.7 与定稿的差异清单（供 operator 回写）

**作废**

| 定稿位置 | 作废内容 | 改为 |
|---|---|---|
| §1.5 标题与第 1 段 | 「入口与保存全都在 8791（TtsWeb）上实现」「浏览器不与 8793 打任何交道」 | 入口和保存都在 8793：管理页的「添加文章」页签，加 `/add` 别名；浏览器只与 8793 交互，8791 不为网页通道新增任何浏览器接口 |
| §1.5 用户旅程第 1 步 | 「播放页点『添加文章』按钮」 | 按分离原则，**播放页不加按钮，也不加链接**；入口是收藏或主屏上的 `/add`，或者管理页的页签 |
| §1.5 第 2、3 步 | TtsWeb 服务端保存入口；保存口令 | 登录 8793 账号（会话复用）；JWT 提交到 `POST /api/reading/ingest`（web 通道），由 agnes 代调 `/share` |
| §1.5 第 4 步 | TtsWeb 经 localhost 触发编排；「编排暂不可用不报错丢文」由巡检补触发 | 保存和补译是同一个作业，不需要单独触发（§4.4） |
| §1.5 第 5 步 | 「状态由 TtsWeb 查 reading 库」 | 状态来自 `GET /api/reading/jobs/{id}` |
| §1.5「权限」段 | 网页通道的保存口令、服务端比对、全局节流 | 8793 JWT，账号要求 `reading_ingest='web'`；复用登录锁定 |
| §1.5「保存口令的生成与管理」整段 | `tokens.json` 的 `web` 条目、getpass 设置脚本、口令轮换与节流 | 整段删除。**`tokens.json` 不新增 `web` 条目**；`web` 只作为 `agnes-service` 令牌 `can_act_for` 里的一个名字 |
| §1.5「分工」段 | 按钮、/add、保存、补译触发全归 TtsWeb | 全归 agnes-service。TtsWeb 侧只剩 §5 的契约项和令牌登记 |
| §2.1 第 2 条 | 「网页通道由 TtsWeb 服务端经本机 localhost 触发」 | 网页通道是浏览器同源调用，用 JWT，账号 `reading_ingest='web'`，只收 `title/text/lang` |
| §2.1 第 4 条 | 「`web` 值本期预留不用」 | `web` 值本期启用，授予用户本人的账号 |
| §2.6 | 「不做粘贴入口」 | agnes-service 侧做「添加文章」页签和 `/add` 别名 |
| §4 DF-8 | 播放页按钮加 `/add` 粘贴页 | 8793 的 `/add`（页签），播放页不设入口 |
| §4 DF-9 | 网页通道全在 8791，沿用 TtsWeb 写令牌口径 | 网页通道走 8793 JWT，由 agnes 用服务令牌代调 `/share` |
| §4 DF-11 | `can_act_for={A001,A002,A003}`，「网页通道不经此令牌」 | `can_act_for={A001,A002,A003,web}` |
| §4「播放页版本：按钮只上 v3」 | — | 不再适用（播放页没有按钮）。只保留「短链指向 `/v3?s=`」 |
| §5「TtsWeb 侧实现（网页通道入口）」与「web 保存口令设置」两条 | v3 按钮、/add 页、服务端保存入口、口令脚本 | 整条删除 |
| 现有代码（未上线） | `X-Ingest-Key` 与 `INGEST_WEB_KEY`、`INGEST_WEB_USER` 服务账号、「web 通道只收 share_id」 | 删除（§7 第 1 条） |

**保留（不变）**

- §1.1 执行者梯队、L2 巡检（A001 每小时一次、只在 08–24 时）；§1.2 的 agent 通道（MuseAgent JWT、muse_id）；`added_via` 列及 `agent/web/legacy` 取值。
- §1.3 share-first、replace 补译、短链永不变、share_id 作锁键、VERSION_CONFLICT 视为收敛。
- §1.4 异步作业（202 + job_id）；§2.1 作业落库与启动恢复、P2R-2 准入语义、kind=`ingest`；§2.2–§2.5 L1 降级、fixes 核对、L2 标记与自动对账，**全部不动**。
- §1.5 第 6、7 步（降级到 L2；非日文只存原文，加假名占比兜底）、「标题必填」。
- §5 其余契约项：`added_via` 的 DDL 与 `/share`、commit 参数；tr_via 的 CHECK 放宽；`readings_check`；`/v3/t` 加 status、`/v3/articles` 纳入 partial（这样新存的文章在播放页列表里看得到）；`grace_until` 提前到上线当天（P2R-18）；ingest.md 补充；Mock TtsWeb。

---

## 5. 接口契约草案

### 5.1 `POST /api/reading/ingest`（web 通道，扩展现有端点）

请求（同源，带 `Authorization: Bearer <access JWT>`，`Content-Type: application/json`）：

```json
{ "title": "北海道の旅", "text": "…日文原文…", "lang": "ja" }
```

通道判定（在准入**之前**，401 和 403 都不扣额度）：

| 账号 `reading_ingest` | 通道 | 允许的字段 | added_by |
|---|---|---|---|
| `agent`（且有 muse_id） | agent | text 或 share_id、title、lang、rate（现状） | muse_id |
| `web` | web | **title（必填）、text（必填）、lang**；其余字段 400 `unknown_field` | `web` |
| `none` | — | 403 `ingest_forbidden` | — |
| 匿名或令牌失效 | — | 401 | — |

响应：

| 状态 | 体 | 页面行为 |
|---|---|---|
| 202 | `{job_id, share_id, url, job}`；`url` = `<TTSWEB_PUBLIC_URL>/v3?s=<share_id>`（`share_id` 可能为 null） | 显示短链（可点、可复制）；share_id 为 null 时提示「已收下，稍后自动入库」；开始轮询 |
| 200 | `{duplicate:true, job}`（同一篇文章已有进行中的作业） | 显示那个作业的短链和状态 |
| 400 | `invalid_request / invalid_text / invalid_lang / unknown_field`（`title_required` 新增） | 就地提示，草稿保留 |
| 401 | `unauthorized` | 走现有的「单飞刷新 → 重试 → 弹登录框」，**不清空草稿** |
| 403 | `ingest_forbidden` | 「此账号未开通保存文章」 |
| 415 | Content-Type 不对 | —（不会出现） |
| 429 | `rate_limited / daily_quota…`（现有 limitCodes） | 显示限额文案 |

### 5.2 已有、直接复用的接口

- `GET /api/reading/jobs/{id}`：按属主过滤（admin 可看全部），页面每 3–5 秒轮询一次，到终态或进入 `retry/l2` 就停。
- `GET /api/reading/jobs?mine=1&channel=web&limit=10`：页签里的「最近保存」列表。
- `POST /api/auth/login|refresh|logout`、`GET /api/auth/me`：完全不变。

### 5.3 `GET /add`（新路由别名）

返回的内容、响应头、CSP 与 `GET /` 完全相同（复用 `ServeIndex`）。

### 5.4 agnes-service → TtsWeb（不变，只是 by 取值）

`POST http://127.0.0.1:8791/share`，`Authorization: Bearer $TTSWEB_TOKEN`，请求体 `{text, lang, title, by:"web", added_via:"web"}`，返回 `{id, url}`。TtsWeb 侧要求 `agnes-service` 令牌的 `can_act_for` 包含 `web`。

---

## 6. 归属与准入定值（汇总）

| 项 | 定值 |
|---|---|
| 可以保存的账号 | `reading_ingest='web'` 的账号，即用户本人日常账号（上线时由 operator 在管理页开通） |
| 登录凭据 | 用户现有的 8793 口令（密码管理器自动填充） |
| 会话 | 现有 `agnes_rt` refresh Cookie：7 天滑动续期，家族上限 30 天；access JWT 30 分钟，只放内存 |
| 服务凭据 | agnes `.env` 的 `TTSWEB_TOKEN` ↔ TtsWeb `tokens.json` 的 `agnes-service`（`write`，`can_act_for={A001,A002,A003,web}`），明文备份在 `tts-web/tokens/agnes-service.txt` |
| reading `added_by` / `added_via` | `web` / `web` |
| agnes 记账 | `ingest_jobs.channel=web`、`username=<登录名>`；call_log kind=`ingest`，一篇准入一次 |
| 新增配置键 | `TTSWEB_PUBLIC_URL`（8791 公网根地址；改了要重启，见定稿 §1.6） |
| 删除的配置键 | `INGEST_WEB_KEY`、`INGEST_WEB_USER`（生产 `.env` 里本来就没有） |

---

## 7. 实施要点清单

**agnes-service（在开发实例 8795 上做，随第二期一起 publish）**

1. `IngestApi.Caller`：删掉 `X-Ingest-Key` 分支（以及 `WebUser` 和 `ServiceUser` 在这里的用途，`AuthService.ServiceUser` 没有别处引用的话一并删掉）。JWT 用户按 `reading_ingest` 分流：`agent` 走现状；`web` 返回 `(u, "web", "web")`；其他值返回 403。
2. `IngestApi.Submit` 的 web 分支：改为**必须有 text 和 title**，拒绝 `share_id` 和 `rate`（`unknown_field`），标题为空时返回 400 `title_required`。去掉「web 只收 share_id」的旧校验。
3. 202 响应和 `IngestStore.View` 里的 `url`：配置了 `TTSWEB_PUBLIC_URL` 时拼成绝对地址，没配时保持相对 `/v3?s=`。`.env.example` 加这个键，并写明「改了要重启」。
4. `Program.cs`：加 `app.MapMethods("/add", GET/HEAD, ServeIndex)`。
5. `index.html`：
   - 加「添加文章」页签：标题、语言（默认 ja）、原文文本框（显示字数，上限 5000）、保存按钮、结果区（短链可点、可复制，作业状态）、「最近保存」列表。
   - `location.pathname === "/add"` 时，登录后默认选中这个页签。
   - 草稿存 localStorage（正文不算机密），保存成功后清除；401 弹登录框时不清空表单。
   - 所有回显都用 `textContent`。`TABS` 数组和 `selectTab` 一起改。
   - 加错误文案：`ingest_forbidden`、`title_required`。
6. 测试：`tests/p2_accept.py` 的 C 段（`X-Ingest-Key`）改写为「web 账号用 JWT 提交 text」。补几条用例：`none` 返回 403；web 账号带 `share_id` 返回 400；缺 title 返回 400；agent 账号行为不变；TtsWeb 故障时 202 且 `share_id=null`，之后 worker 补上；同文重复提交返回 200 duplicate。`tests/ingest_*.env` 里删掉 `INGEST_WEB_KEY`。
7. `/health` 的 `ingest.web_trigger` 改成 `ingest.public_url_configured`（布尔）。
8. README、DESIGN 里写明网页通道新的链路，以及 P2R-19（回环 IP）这个已知限制。
9. （可选、小改）`/api/auth/refresh` 遇到「already rotated」（10 s 宽限内）时**不清 Cookie**；前端遇到这个返回码时，等 1–2 秒再刷新一次。这样一个标签开管理页、一个标签开 `/add` 时，不会因为恰好同时刷新而互相登出。

**TtsWeb（朗读项目，第 0 步）**

10. 签发 `agnes-service` 写令牌（用现有的 `make_tokens.py` 流程）：`tokens.json` 只登记哈希，`can_act_for={A001,A002,A003,web}`；明文写入 `tokens/agnes-service.txt`（0600），再由 operator 写进 agnes `.env` 的 `TTSWEB_TOKEN`；两边都要重启。值不进任何文档、聊天或记忆。
11. 其余契约项照定稿 §5 原样做（`added_via`、tr_via CHECK、`readings_check`、`/v3/t` 的 status、`/v3/articles` 纳入 partial、`grace_until` 提前）。**网页通道相关的 /add 页、服务端保存入口、口令脚本、v3 按钮一概不做。**

**上线与验收（追加到定稿 §6）**

12. 上线时用管理页给用户本人的账号开通 `reading_ingest=web`（有审计）。
13. 验收：
    - 手机浏览器打开 `/add`，自动恢复会话或密码管理器自动填充登录，粘贴保存后 ≤3 秒拿到 8791 的绝对短链，点开能听原文；作业最终到 ready，或按设计进入 retry/L2。
    - 用 `reading_ingest=none` 的账号保存，返回 403。
    - 8793 的任何响应都不带 `Access-Control-Allow-*`。
    - 页面源码和网络面板里搜不到 TtsWeb 令牌。
    - 8791 播放页的文件没有改动（diff 为空）。
    - 停掉 8791 再保存，返回 202 且 `share_id=null`；8791 恢复后作业自动补上短链。
    - 在管理页和 `/add` 两个标签页里分别操作，不会互相登出。
    - reading.articles 里新文章的 `added_by=web`、`added_via=web`。

---

## 8. 余留风险（已接受或已知）

1. **账号可以被他人临时锁定**（P2R-19）：属于可用性问题，不是安全漏洞，管理页现在也有。如果以后核实网关会传 `X-Forwarded-For`，开 `TRUST_PROXY=1` 就能缓解。
2. **8793 是保存的单点**：单人工具可以接受。播放不受影响，agent 通道也不受影响。
3. **管理页 CSP 有 `script-src 'unsafe-inline'`**：这是现状。「添加文章」页签不引入新的内联脚本来源，也不用 innerHTML，不扩大攻击面。
4. **用户日常账号如果是 admin**，保存页就和管理权限在同一个会话里。这与用户现在每天用管理页的风险面相同，本设计没有扩大它。如果用户希望隔离，也可以另建一个普通 user 账号只开 `web`，但这样会回到「两个账号、两套保险库条目、同一个 Cookie 互顶」的老问题，**不推荐**。
