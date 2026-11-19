#!/usr/bin/env bash
# Build, configure and start agnes-service (.NET 10) as a systemd unit. Run as the operator.
#   1. dotnet publish -c Release -o publish            (no sudo)
#   2. .env: created from .env.example, mode 600, JWT_SECRET generated (never printed),
#      AGNES_DB_CONNECTION defaulted to local peer auth  (no sudo; done by the app: --init-env)
#   3. schema agnes_auth applied incl. TASK4 limits migration (idempotent; psql, no sudo)
#   4. unit installed, enabled, (re)started                (sudo)
#   5. /health checked
# Afterwards create the first account:
#   /data/dotnet-sdk/dotnet publish/AgnesService.dll --create-user <username> admin
set -euo pipefail

DIR=/data/workspace/agnes-service
UNIT=agnes-service.service
DOTNET=${DOTNET:-/data/dotnet-sdk/dotnet}
DB=${AGNES_DB_NAME:-db77database}
cd "$DIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# ---- 1. build
"$DOTNET" publish "$DIR/AgnesService.csproj" -c Release -o "$DIR/publish"
test -f "$DIR/publish/index.html" || { echo "publish/index.html missing" >&2; exit 1; }

# ---- 2. .env (secret generated inside the app, never on a command line or in output)
"$DOTNET" "$DIR/publish/AgnesService.dll" --init-env

# ---- 3. schema (idempotent; touches only schema agnes_auth)
psql -d "$DB" -v ON_ERROR_STOP=1 -q -f "$DIR/auth_schema.sql"
echo "Schema agnes_auth (incl. usage_daily + per-user limit columns) applied to $DB."

# ---- 4. systemd (needs sudo)
sudo install -m 644 "$DIR/$UNIT" "/etc/systemd/system/$UNIT"
sudo systemctl daemon-reload
sudo systemctl enable "$UNIT"
sudo systemctl restart "$UNIT"

# ---- 5. health
for i in $(seq 1 20); do
  if curl -fsS http://127.0.0.1:8793/health >/dev/null 2>&1; then break; fi
  sleep 0.5
done
sudo systemctl --no-pager --lines=10 status "$UNIT" || true
echo
echo "Health check:"
curl -fsS http://127.0.0.1:8793/health && echo
echo
echo "Next:"
echo "  - first account (prompts for password twice, no echo):"
echo "      cd $DIR && $DOTNET publish/AgnesService.dll --create-user <username> admin"
echo "  - when the Agnes key arrives: put AGNES_API_KEY=… into $DIR/.env (picked up without restart)"
echo "  - smoke test: SMOKE_USER=<u> SMOKE_PASSWORD='…' $DIR/smoke.sh"
