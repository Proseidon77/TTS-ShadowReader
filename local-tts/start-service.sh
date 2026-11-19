#!/usr/bin/env bash
# muse-tts 服务的安装/恢复脚本。
# VM 换代后 /etc 下的 systemd 单元会丢失、venv 也可能不在，跑本脚本一次恢复。
# 代理变量经 systemctl import-environment 导入 systemd 管理器内存，
# 不把带凭据的代理地址写进任何文件。
set -euo pipefail
cd "$(dirname "$0")"

# 1) venv 缺失则重建并装依赖
if [ ! -x venv/bin/python ]; then
  python3 -m venv venv
  venv/bin/pip install -q -r requirements.txt
fi

# 2) 安装/更新 systemd 单元
install -m 644 systemd/muse-tts.service /etc/systemd/system/muse-tts.service
systemctl daemon-reload

# 3) 导入代理等环境变量到 systemd 管理器（仅内存）
for v in HTTPS_PROXY https_proxy HTTP_PROXY http_proxy ALL_PROXY all_proxy NO_PROXY no_proxy; do
  if [ -n "${!v:-}" ]; then
    systemctl import-environment "$v" || true
  fi
done

systemctl enable muse-tts >/dev/null 2>&1 || true
systemctl restart muse-tts
sleep 1

# 4) 健康检查
echo "--- systemctl status ---"
systemctl --no-pager --full status muse-tts | head -10 || true
echo "--- /health ---"
curl -s --max-time 5 http://127.0.0.1:8790/health && echo
