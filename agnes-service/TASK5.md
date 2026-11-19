# TASK5: Admin backend (API + frontend) for account management, modelled on dbapi

The service from TASK3+TASK4 is built, deployed and running (systemd `agnes-service`, port 8793, publish/ output). The user now wants what was deliberately left out before: **a web admin backend to manage and configure accounts**, and it must **follow how dbapi does it**.

You still cannot read `/data/src/db-api` from this machine (denied in earlier rounds), so the operator has extracted dbapi's admin design from its mirror and it is reproduced here verbatim in substance. Follow it; do not guess a different shape.

## How dbapi does admin (the reference to mirror)

- Endpoints, ALL behind an Admin authorization policy (role claim = admin):
  - `GET /admin/users` → list of UserDto
  - `POST /admin/users` {username, password, role} → 201 + UserDto; validation errors are structured: `invalid_username` (3–32 chars, letters/digits/`_ . -`), `invalid_role`, `weak_password` (a password-strength check), missing-field 400s that name the field
  - `PATCH /admin/users/{id}` {role?, isDisabled?} → partial update; empty patch → 400 `no_changes`
  - `POST /admin/users/{id}/reset-password` {newPassword} → also invalidates that user's existing sessions
  - `DELETE /admin/users/{id}` → delete account
- UserDto: id, username, role, isDisabled, createdAt, lastLoginAt.
- **Self-protection (exact dbapi behaviour)**: PATCH on yourself → 400 `cannot_modify_self` ("cannot change your own role or disable yourself — guarantees at least one admin remains"); reset-password on yourself → rejected, message points to "my account" change-password (which verifies the current password); DELETE yourself → 400 `cannot_modify_self`.
- **Audit**: every admin action is written by an AuditService (actor, action, target, detail — never passwords); audit viewing is admin-only.
- **Frontend (dbapi's UsersView)**: a "用户管理" page: error banner on top; a table with columns username / role / status / created / last login / actions; row actions: enable-disable toggle button, "重置口令" button (disabled for your own row, with a tooltip saying to use my-account instead), delete (danger, disabled for self); a create-user form (username, password, role select); reset-password is a modal dialog with a confirmation field and a show/hide password toggle; success toasts name the affected user ("已重置 X 的口令（其现有会话已失效）").

## What to build here (Agnes service, .NET 10, same project)

1. **Admin API** under `/api/admin/`, Admin policy (JWT role claim `admin`; non-admin → 403, unauthenticated → 401):
   - `GET /api/admin/users` → list; each entry: id, username, role, is_active, created_at, last_login_at, locked_until (if currently locked), limit overrides (the four TASK4 values, null = default), effective limits, today's usage (total + video) from usage_daily.
   - `POST /api/admin/users` {username, password, role, limits?} → 201; same validation style as dbapi (structured error codes, username rule identical to the CLI's, role user|admin, a documented password-strength rule — mirror dbapi's approach of a `weak_password` rejection; state the concrete rule you implement).
   - `PATCH /api/admin/users/{id}` {role?, is_active?, limits?} → partial update with dbapi semantics: empty patch → 400 `no_changes`; self role/status change → 400 `cannot_modify_self`. Limits semantics must be unambiguous: for each of the four limit fields, absent = leave unchanged, a number = set override, explicit JSON null = clear the override back to default — implement presence detection accordingly and document it in DESIGN.md/README.
   - `POST /api/admin/users/{id}/reset-password` {newPassword} → sets the hash, bumps token_version (existing sessions die, as in dbapi), unlocks the account; self → rejected with the my-account pointer.
   - `DELETE /api/admin/users/{id}` → delete the account (and its refresh tokens / usage rows — handle FKs explicitly); self → 400 `cannot_modify_self`. Also guard: deleting or demoting or disabling the LAST remaining admin must be refused (dbapi's stated guarantee) even when the actor is a different admin.
   - `POST /api/auth/change-password` {currentPassword, newPassword} (authenticated, any role) → verifies current password, applies strength rule, bumps own token_version and re-issues tokens (dbapi's "my account" path that self-reset points to).
   - IP blocks: `GET /api/admin/ip-blocks` and `DELETE /api/admin/ip-blocks/{ip}` (unblock), mirroring the CLI's --unblock-ip.
   - **Audit**: add an `admin_audit` table to `agnes_auth` via an idempotent migration appended to auth_schema.sql (actor_user_id, actor_username, action, target_user_id, target_username, detail json/text without secrets, created_at). Write one row for every admin mutation (create/patch/reset/delete/unblock) and for change-password (actor = self). `GET /api/admin/audit?limit=100` returns recent rows, admin-only.
2. **Frontend** (same single index.html, vanilla JS as now):
   - A 「管理」 tab/section rendered ONLY when `/api/auth/me` reports role=admin (for non-admins it must not appear at all; the API's 403 is the real guard, hiding is cosmetic — keep that distinction in code comments/docs).
   - Mirror UsersView's layout pattern in our own style: error banner area; users table (username / role / status incl. locked / created / last login / limits (effective, with override marker) / today usage / actions); create-user form (username, password, role, the four limit fields optional); per-row actions: enable/disable toggle, edit-limits (inline or small dialog — your choice, keep it simple), reset password (dialog with confirmation field + show/hide toggle, disabled for own row with the reason as title text), delete (danger, confirm dialog, disabled for own row).
   - A "我的账户" change-password form (current + new + confirm) in the account area for all logged-in users.
   - After any mutation, refresh the table and show a success message naming the affected user, like dbapi does.
3. **Docs**: update DESIGN.md (admin API, self-protection rules, audit) and README.md (admin usage; note the CLI still works and both paths share the same tables).

## Testing (required, real outputs in the report)

- The production service is RUNNING on 8793 from systemd — do not disturb it. Run your test instance on **port 8794** (PORT env / --env-file as you built earlier) against the same database.
- Real accounts may already exist (the operator was creating an admin account, e.g. TestUser001) — NEVER delete, disable or reset any account you did not create in this round. Use clearly named throwaways (e.g. `admtest_*`) and clean up only those.
- Demonstrate with actual requests: non-admin token → 403 on every /api/admin/* route; admin list/create (201); PATCH limits on a throwaway → its /api/limits reflects the new effective values immediately; PATCH with explicit null → override cleared; empty PATCH → 400 no_changes; self PATCH → 400 cannot_modify_self; self reset → rejected; reset of throwaway → its old access token now 401; disable throwaway → its login fails; change-password round-trip works; delete throwaway → gone from list; audit rows exist for these actions (show a couple, redacting nothing but passwords — there must be none in detail); last-admin guard: explain how you tested it without endangering the real admin account (e.g. reason from code + a scenario with two throwaway admins where one demotes/deletes the other down to one, then the final one is protected).
- `dotnet build` 0 errors; republish into publish/ at the end (the operator will restart the systemd unit). Frontend JS still cannot be node-checked in your environment — review carefully and say so.

## Report

Follow the usual pattern: endpoints added, self-protection and last-admin behaviour, audit table status, frontend changes, actual test outputs (status codes), accounts created and cleaned up, anything denied, and the operator step remaining (restart service).
