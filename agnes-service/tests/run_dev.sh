#!/usr/bin/env bash
# Dev/test runner (foreground, port 8793). Uses the project .env (create it with --init-env).
#   tests/run_dev.sh          real config (no Agnes key yet -> generation 503 after limiter admission)
#   MOCK=1 tests/run_dev.sh   also builds+starts tests/MockAgnes on 127.0.0.1:18793 and overlays
#                             tests/mock.env (AGNES_BASE_URL -> mock, dummy key only sent to the mock)
# This is exactly how the TASK3/TASK4 limiter/concurrency/refund tests were run (as separate commands).
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET=${DOTNET:-/data/dotnet-sdk/dotnet}
[ -f publish/AgnesService.dll ] || "$DOTNET" publish -c Release -o publish
ARGS=(--env-file .env)
if [ "${MOCK:-0}" = 1 ]; then
  "$DOTNET" build tests/MockAgnes -c Release -o tests/MockAgnes/out >/dev/null
  "$DOTNET" tests/MockAgnes/out/MockAgnes.dll & MOCK_PID=$!
  trap 'kill $MOCK_PID 2>/dev/null || true' EXIT
  ARGS+=(--env-file tests/mock.env)
fi
"$DOTNET" publish/AgnesService.dll "${ARGS[@]}"
