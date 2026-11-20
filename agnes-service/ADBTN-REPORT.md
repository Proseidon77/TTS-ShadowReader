# /add 保存按钮宽度修正 — 报告（2026-11-20）

## 改动（一行摘要）
`index.html` 样式区末尾加一条只作用于 `#ad-go` 的规则 `#ad-go { width: auto; min-width: 220px; padding: 12px 40px; }`，前面加一行带日期的注释。全局 `.go`、文本框、表单结构、JS、文案都没动。
`diff index.html.bak-20261120-adbtn index.html` 只有这 2 行新增。

## 上线
- 服务每次请求都从磁盘读 `publish/index.html`（`Program.cs` 里 `ServeIndex` → `SendFileAsync`），所以只把源码拷到了 `publish/index.html`。**没有重启服务。**
- 备份：`index.html.bak-20261120-adbtn`（改动前的原版，和改动前的 publish 版逐字节相同）。
- 回滚：把备份拷回 `index.html` 和 `publish/index.html`。

## 验证
- `GET http://127.0.0.1:8793/health` → **200**。
- `GET /add` 返回的 HTML 里有新规则，`#ad-text` 也还在。
- **注意：没从公网拉取。** 本次运行中 `curl` 和 instabox 查端口工具都要审批，拿不到；上面两项是在本机用 python urllib 打的 127.0.0.1:8793。公网那份需要 operator 再拉一次确认。
- 无头 Chromium（1440×1000），用临时账号 `zz_adbtn_f56f`（user，reading_ingest=web，随机密码没打印）在 /add 登录后量了一下：
  - `#ad-go`：宽 220px、高 48px，靠左，margin-top 12px，文字居中（"保存"）。容器宽 1363px，按钮没有拉满整行。
  - `#ad-text`：1363×220，能看到。
  - 按钮顶边到文本框底边 19px，就是 12px margin 加上行内块的基线留白，和改之前一样。
  - 其他 `.go` 按钮（l-go/t-go/i-go/v-go/acc-go/nu-go）计算出的宽度仍是 100%，没变。
  - 截图后临时账号已删除（users / refresh_tokens / login_audit）。
- 截图：**`shots-adbtn.png`**
- 截图脚本：`.probe/adbtn_shot.py`（通过 CDP 驱动一个已经在跑的无头 Chromium，只用标准库，没有子进程包装 chromium）。

## 未提交 git（按约定由 operator 统一推送）。
