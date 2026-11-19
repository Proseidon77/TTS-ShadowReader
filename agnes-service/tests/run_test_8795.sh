#!/usr/bin/env bash
# TASK6 dev instance on 127.0.0.1:8795 from the build output (bin/Release); same .env / same database as
# production, never touches the systemd service on 8793 or publish/. 8794 belongs to the reading project.
#   tests/run_test_8795.sh          real upstreams from .env (real Agnes; gemini not ready while its key is empty)
#   MOCK=1 tests/run_test_8795.sh   agnes + gemini both pointed at tests/MockAgnes (start it first)
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET=${DOTNET:-/data/dotnet-sdk/dotnet}
mkdir -p .probe
ARGS=(--env-file .env --env-file tests/port8795.env)
[ "${MOCK:-0}" = 1 ] && ARGS+=(--env-file tests/mock8795.env)
exec "$DOTNET" bin/Release/net10.0/AgnesService.dll "${ARGS[@]}" >> .probe/agnes-8795.log 2>&1
