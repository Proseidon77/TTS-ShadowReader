# TASK4: Per-account API limits (rate limiting + daily quota) — add to TASK3

The user added: **each account must have the usual API limit settings — e.g. how many requests per day, how many per minute, etc.** This applies to the .NET 10 service you are building under TASK3. Design the concrete numbers yourself (sensible defaults, stated in DESIGN.md), but the mechanisms below are required.

## Required mechanisms

1. **Per-account limits on the generation endpoints** (`/api/text`, `/api/image`, `/api/video` — status polling and `/api/models` should not burn generation quota; say in DESIGN.md exactly which endpoints count):
   - **Per-minute rate**: max generation requests per account per minute (sliding or fixed window — pick one, document it). Suggested default: 10/minute.
   - **Per-day quota**: max generation requests per account per calendar day. Suggested default: 200/day. Day boundary in **Asia/Tokyo** time (the user is in JST); the reset moment must be explicit in code and docs, not server-local ambiguity.
   - **Concurrent generations per account**: cap simultaneous in-flight generations per account. Suggested default: 2 (the earlier design had a global cap of 6 — keep a global cap too if you judge it useful, and document both).
   - Optionally per-type daily sub-quotas (video is expensive/slow — e.g. a separate lower daily cap for video) if it can be done simply; if included, document defaults; if skipped, say why.
2. **Configurable per account, with system defaults**:
   - Store per-user overrides in the `agnes_auth` schema: either columns on `users` (e.g. `rate_per_minute`, `daily_quota`, `max_concurrent`, NULL = use default) or a small `user_limits` table — your choice, shipped as an idempotent migration SQL added to `auth_schema.sql` (the schema is already live; the migration must apply cleanly on top of the existing tables).
   - System defaults come from configuration/env (e.g. `LIMITS_DEFAULT_PER_MINUTE`, `LIMITS_DEFAULT_PER_DAY`, `LIMITS_DEFAULT_CONCURRENT`) so the operator can change them without recompiling.
   - The create-user tooling gains options to set a new account's limits (e.g. `--daily-quota 500 --rate-per-minute 20`); changing an existing account's limits is done via documented SQL (an admin API is NOT required in this round — say so in README).
3. **Enforcement behaviour**:
   - Over any limit → **HTTP 429** with a JSON body that says which limit was hit (`error`: `rate_limited` / `daily_quota_exceeded` / `concurrency_limit`), the limit value, how many remain, and when it resets; include a `Retry-After` header where it makes sense (per-minute and concurrency; for daily quota give the reset timestamp in the body).
   - Counting must be race-safe under concurrent requests from the same account.
   - **Persistence**: daily counters must survive a service restart — store them in PostgreSQL (e.g. a `usage_daily` table in `agnes_auth`: user, date (JST), endpoint type, count). Per-minute windows and concurrency may be in-memory (single-instance service), but say so in DESIGN.md.
   - Failed upstream (Agnes) calls: decide and document whether they count against quota (suggestion: a request that Agnes rejects with 4xx does not count; one that produces a result or fails 5xx/timeout does — or simpler: count at admission and document that choice explicitly).
   - Login/auth endpoints are NOT part of this quota (they already have the lockout/throttle from TASK2/TASK3) — keep the two systems separate and say so.
4. **Visibility**:
   - `GET /api/auth/me` (or a small `GET /api/limits`) returns the account's effective limits and today's usage so far (used / remaining per type if sub-quotas exist).
   - Frontend: show today's remaining quota somewhere simple (e.g. header after login), and when a generation call returns 429, display the server's message (which limit, when it resets) instead of a generic error.
5. **Tests**: in your smoke work, demonstrate the limiter with a throwaway account set to a tiny daily quota (e.g. 1) and a tiny per-minute rate: first calls pass the limiter (they may 503 on the missing Agnes key — that still proves admission), the call over quota returns 429 with the right body; clean up the throwaway account and its counters afterwards. Report actual outputs.

## Report additions

Add to your final report: the limit model (which endpoints count, window type, day boundary), the defaults you chose and where they can be changed (env + per-user override), the migration SQL status (applied or left to operator), where counters live and what survives restart, and your 429 test evidence.
