#!/usr/bin/env bash
# Test instance on 127.0.0.1:8794 from the build output (bin/Release), same .env / same database as
# production. Never touches the systemd service on 8793 or publish/.  (TASK5 admin tests)
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET=${DOTNET:-/data/dotnet-sdk/dotnet}
mkdir -p .probe
export PORT=8794 HOST=127.0.0.1
exec "$DOTNET" bin/Release/net10.0/AgnesService.dll --env-file .env >> .probe/agnes-8794.log 2>&1
