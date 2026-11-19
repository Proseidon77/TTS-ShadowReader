# Amendment (TASK2): JWT authentication for the Agnes service API — supersedes the auth parts of TASK.md

The user added this requirement while you were working: **the service's API must require JWT authentication**, modelled on the existing database API ("dbapi") already running on this machine, and the auth data must be stored in a **new PostgreSQL schema** created for this service.

Where TASK.md mentioned a simple optional `SERVICE_TOKEN` / `X-Access-Token`, that idea is REPLACED by this JWT design. Keep everything else in TASK.md.

## Learn from dbapi first

- dbapi source is on this machine at `/data/src/db-api` (ASP.NET Core 10 + Npgsql + Vue). Read how it does auth before designing: JWT Bearer access tokens + refresh-token cookie rotation, PBKDF2 password hashing, login lockout (5 failed attempts → 15 min lock; repeated locks from one IP → IP ban row). Its live database is PostgreSQL database `db77database` on localhost.
- Mirror the *approach* (token shapes, claims, expiry style, hashing, lockout spirit), adapted to this Python service. Do not copy code blindly; note in DESIGN.md what you took from dbapi and what you simplified and why.

## New schema for auth data

- Create a NEW schema in `db77database` dedicated to this service, suggested name `agnes_auth` (if you pick another name, justify it). It must hold at least the users table (id, username unique, password_hash, role, is_active, created_at, last_login_at) and, if your design uses server-side refresh tokens / lockout counters, the tables for those (refresh tokens with rotation/revocation, failed-login tracking).
- Ship the DDL as an idempotent SQL file in the project (e.g. `auth_schema.sql`) that creates the schema and tables if absent. Do NOT run it against the live database yourself if permissions deny it — leave execution to the operator, and say so in your report. Never touch dbapi's own tables or the existing `drama` / `jpdb` schemas.
- Passwords: PBKDF2 hashing compatible in spirit with dbapi (salted, iterated); never store or log plaintext. No default/seed account with a weak password: provide a `create_user.py` CLI (operator runs it locally on this machine, prompts for password without echo, inserts the hashed user) so the first account is created deliberately.

## JWT design (implement, stdlib-first)

- Access token: JWT signed HS256 with a secret from env `JWT_SECRET` in the same `.env` (never hardcoded; if missing, the service must refuse to issue tokens and say why, while /health still works). Short expiry in the same spirit as dbapi (state the value you chose, e.g. 30–60 min).
- Refresh: mirror dbapi's rotation approach (httpOnly cookie refresh token, rotated on use, revocable server-side) OR, if you judge a simpler refresh-in-body design better for this single-page frontend, explain the trade-off in DESIGN.md and implement that — but rotation + revocation must be addressed, not ignored.
- Python stdlib has no PostgreSQL driver and no JWT library: implement JWT sign/verify with `hmac`/`hashlib`/`base64` (that part is easy). For PostgreSQL access, check whether `psycopg`/`psycopg2` is importable; if not, write the code against psycopg (v3 style), add it to a `requirements.txt`, and put "operator: pip install psycopg[binary]" in the install steps — do not fall back to storing users in a flat file or SQLite, the user explicitly wants a database schema.
- Connection settings come from env (e.g. `AGNES_DB_DSN` in `.env`), never hardcoded credentials.

## Endpoint / frontend changes

- Public: `GET /` (frontend), `GET /health`, `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout`.
- JWT required (Authorization: Bearer): `POST /api/text`, `POST /api/image`, `POST /api/video`, `GET /api/video/status/<id>` if it exists, and `GET /api/models` (unless you argue otherwise in DESIGN.md). Missing/invalid/expired token → 401 JSON.
- Frontend: add a login screen/form (username + password) shown when no valid token; store the access token the way your design specifies, attach it to generation calls, handle 401 by trying refresh once then returning to login. Keep it simple and in Chinese.
- Update DESIGN.md, README.md, smoke script (smoke must now cover: unauthenticated generation call → 401; login with a test user → token; authenticated call passes the auth layer — real Agnes generation still only when AGNES_API_KEY is configured), and the systemd unit / install steps (env vars, requirements, schema DDL step) accordingly.

## Report additions

In your final report, add: the schema name and tables created/proposed, whether you could run the DDL or it is left to the operator, which dbapi auth details you mirrored vs simplified, token lifetimes chosen, and the exact steps the operator must run to (1) install the PG driver, (2) apply the schema, (3) create the first user.
