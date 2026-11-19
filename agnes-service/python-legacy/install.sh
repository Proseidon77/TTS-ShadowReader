#!/usr/bin/env bash
# Install and start agnes-service as a systemd unit. Run as the operator (uses sudo).
#   1. .env exists, mode 600, JWT_SECRET generated if empty (never printed)
#   2. PostgreSQL driver psycopg (apt python3-psycopg, else project venv .venv)
#   3. auth schema agnes_auth applied (idempotent)
#   4. unit installed, enabled, (re)started, /health checked
# Afterwards create the first account:  python3 create_user.py <name> --role admin
set -euo pipefail

DIR=/data/workspace/agnes-service
UNIT=agnes-service.service
DB=${AGNES_DB_NAME:-db77database}
cd "$DIR"

# ---- 1. .env
if [ ! -f .env ]; then
  cp .env.example .env
  echo "Created $DIR/.env from .env.example."
fi
chmod 600 .env
if ! grep -Eq '^JWT_SECRET=.{32,}' .env; then
  # generated and written inside Python: the secret never appears on a command line
  python3 - <<'PY'
import secrets
p = ".env"
s = secrets.token_urlsafe(48)
lines = open(p, encoding="utf-8").read().splitlines()
if any(l.startswith("JWT_SECRET=") for l in lines):
    lines = ["JWT_SECRET=" + s if l.startswith("JWT_SECRET=") else l for l in lines]
else:
    lines.append("JWT_SECRET=" + s)
open(p, "w", encoding="utf-8").write("\n".join(lines) + "\n")
PY
  echo "Generated JWT_SECRET in .env (not shown)."
fi
grep -q '^AGNES_DB_DSN=.' .env || printf 'AGNES_DB_DSN=dbname=%s\n' "$DB" >> .env

# ---- 2. PostgreSQL driver
PY=/usr/bin/python3
if ! $PY -c 'import psycopg' 2>/dev/null; then
  echo "psycopg not importable; trying: sudo apt-get install -y python3-psycopg"
  sudo apt-get install -y python3-psycopg || true
fi
if ! $PY -c 'import psycopg' 2>/dev/null; then
  echo "Falling back to a project venv (.venv) with requirements.txt"
  python3 -m venv "$DIR/.venv"
  "$DIR/.venv/bin/pip" install -q -r "$DIR/requirements.txt"
  PY="$DIR/.venv/bin/python"
fi
$PY -c 'import psycopg; print("psycopg", psycopg.__version__, "via", __import__("sys").executable)'

for f in common.py auth.py server.py create_user.py smoke.py; do $PY -m py_compile "$f"; done

# ---- 3. schema (idempotent; touches only schema agnes_auth)
psql -d "$DB" -v ON_ERROR_STOP=1 -q -f "$DIR/auth_schema.sql"
echo "Schema agnes_auth applied to $DB."

# ---- 4. systemd
tmp=$(mktemp)
sed "s#^ExecStart=.*#ExecStart=$PY $DIR/server.py#" "$DIR/$UNIT" > "$tmp"
sudo install -m 644 "$tmp" "/etc/systemd/system/$UNIT"
rm -f "$tmp"
sudo systemctl daemon-reload
sudo systemctl enable "$UNIT"
sudo systemctl restart "$UNIT"
sleep 2
sudo systemctl --no-pager --lines=10 status "$UNIT" || true

echo
echo "Health check:"
curl -fsS http://127.0.0.1:8793/health && echo
echo
echo "Next: create the first account (prompts for password, no echo):"
echo "  cd $DIR && $PY create_user.py <username> --role admin"
