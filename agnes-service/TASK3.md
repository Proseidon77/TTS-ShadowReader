# TASK3: Rewrite the backend in .NET 10 (ASP.NET Core) — frontend and agnes_auth schema are reused

The user has decided: **drop the Python backend, rebuild it with the .NET 10 environment already on this machine**. This supersedes the stack choice in TASK.md/TASK2.md. Everything else (endpoints, JWT auth design, schema, video research) carries over.

Context you already produced in this directory (`/data/workspace/agnes-service/`): DESIGN.md (full auth + API design from the Python round), server.py/auth.py/common.py (Python implementation — now reference only), index.html (frontend, REUSE), auth_schema.sql (schema DDL, already applied to db77database: schema `agnes_auth` with users / refresh_tokens / ip_block / login_audit), research/ (Agnes video API findings), README.md, smoke.py, install.sh, agnes-service.service.

## What to do

1. **Move the Python backend aside**: put server.py, auth.py, common.py, create_user.py, check_env.py, requirements.txt, smoke.py into a `python-legacy/` subfolder (kept for reference, not deleted). index.html, auth_schema.sql, research/, DESIGN.md stay at top level and remain the live design sources.
2. **New ASP.NET Core 10 project** in this directory (e.g. `AgnesService.csproj` + `Program.cs`, minimal-API style is fine; add files/folders as needed):
   - SDK is at `/data/dotnet-sdk/dotnet` (10.0.404, ASP.NET Core 10.0.15). Your allow-list includes dotnet — this time you ARE expected to `dotnet build` and actually run/test the app yourself; report real outputs.
   - NuGet: prefer packages already in the local cache from the dbapi project (Npgsql, Microsoft.AspNetCore.Authentication.JwtBearer). If restore needs the network and fails, say so in the report and structure the code so the operator can restore later; do not silently switch libraries.
   - Serve on port **8793**, bind 0.0.0.0. Serve the existing `index.html` at `GET /` (as static file from the project dir / publish dir — make sure it ships with the publish output).
   - Endpoints with the SAME paths and JSON shapes as the Python design (so the frontend needs at most tiny fixes — if you must change a shape, update index.html to match and note it):
     - Public: `GET /`, `GET /health`, `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout`
     - Bearer JWT required: `POST /api/text`, `POST /api/image`, `POST /api/video`, `GET /api/video/status/<id>`, `GET /api/models`, `GET /api/auth/me`
   - Agnes upstream: same behaviour as designed — chat completions (300s timeout), image generations with `extra_body.response_format` (360s), video async create `POST /v1/videos` + poll `GET /agnesapi?video_id=…&model_name=…` per research/ findings (confirmed-from-docs vs assumed stays documented). No AGNES_API_KEY → generation endpoints 503 with the clear error; service still starts.
   - **JWT auth against the existing `agnes_auth` schema** in PostgreSQL database `db77database` (Npgsql; connection string from env, e.g. `AGNES_DB_CONNECTION`, in the same `.env` — no hardcoded credentials; local peer connection as OS user `user` is how the Python round connected):
     - Password verify MUST accept the existing stored format `pbkdf2-sha512$210000$salt$hash` documented in DESIGN.md (that is dbapi's format; if you can read `/data/src/db-api` this time, confirm against its code — in the previous round reading it was denied, so do not depend on it).
     - Access JWT HS256, secret from env `JWT_SECRET`, 30 min, includes the `token_version` claim with the same semantics as DESIGN.md; refresh token httpOnly cookie, stored SHA-256 hashed, rotated on every use, reuse kills the session (10s grace nuance from DESIGN.md may be kept or dropped — document which); 7-day refresh / 30-day absolute session; lockout 5 fails → 15 min account lock, 3 locks per IP → 24h IP ban, loopback never banned; the same failed-login throttle as DESIGN.md.
     - If JWT_SECRET missing: no tokens issued/accepted, clear 503 on auth endpoints, /health still OK — same as before.
   - **create-user tooling**: provide an operator path to create the first user with the same PBKDF2 format — e.g. running the app with `--create-user <username> <role>` (password read from console without echo) or a tiny companion console project. Document the exact command in README. No default account.
3. **Deployment artefacts, updated for .NET**:
   - `agnes-service.service`: systemd unit running the published app (follow how dbapi's unit/deploy works if visible; otherwise a straightforward `ExecStart=/data/dotnet-sdk/dotnet /data/workspace/agnes-service/publish/AgnesService.dll` with WorkingDirectory, EnvironmentFile=.env, Restart=on-failure).
   - `install.sh`: publish (`dotnet publish -c Release -o publish`), install/enable/start the unit, then curl /health. Sudo steps may be denied for you — script them anyway and report which parts the operator must run.
   - `smoke.sh` (curl-based): /health, /, unauthenticated generation → 401, and when SMOKE_USER/SMOKE_PASSWORD env are given: login → token → /api/models and /api/auth/me pass the auth layer.
4. **Update DESIGN.md and README.md** for the .NET stack (keep the auth/video sections, adjust stack, build, run, and operator steps). Note in DESIGN.md that the Python implementation is archived under `python-legacy/`.
5. **Test for real this time** (dotnet is allow-listed): `dotnet build` must pass; run the published/built app briefly on 8793 and curl: /health, / (frontend HTML), one unauthenticated /api/text → 401, and if you can create a throwaway user in agnes_auth (the schema is live; clean it up afterwards like the Python round did with rolled-back/removed test data), a full login → /api/auth/me round-trip. Report actual outputs, and list every command that was still denied.

## Rules (unchanged)

- Work only inside `/data/workspace/agnes-service/` (reading /data/src/db-api is allowed if the tools permit). Do not disturb the running services on 8080/8791/8792 or any database schema other than `agnes_auth`.
- The Agnes API key is not on this machine: never hardcode or invent one; it arrives later via `.env` (AGNES_API_KEY) through the operator.
- Final report: .NET project layout, build result, runtime smoke results (actual), what the frontend needed changed (if anything), auth compatibility notes vs the stored hash format, operator steps remaining (systemd install, first user, key), and any denied commands.
