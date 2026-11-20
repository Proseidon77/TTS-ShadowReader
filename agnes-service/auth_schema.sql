-- agnes-service authentication schema (idempotent: safe to run repeatedly).
-- Apply:   psql -d db77database -v ON_ERROR_STOP=1 -f auth_schema.sql
-- Creates ONLY schema agnes_auth and objects inside it. It does not touch public,
-- drama, jpdb or anything belonging to dbapi (whose tables live in database "dbapi").
-- Layout mirrors dbapi's app_user / refresh_token / ip_block tables (see DESIGN.md).

BEGIN;

CREATE SCHEMA IF NOT EXISTS agnes_auth;
COMMENT ON SCHEMA agnes_auth IS 'Auth data for agnes-service (port 8793): users, refresh tokens, lockout';

-- Users. Passwords: pbkdf2-sha512$<iterations>$<salt b64>$<hash b64> (dbapi format).
CREATE TABLE IF NOT EXISTS agnes_auth.users (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username       text        NOT NULL CHECK (username ~ '^[a-zA-Z0-9_.-]{3,32}$'),
    password_hash  text        NOT NULL CHECK (password_hash LIKE 'pbkdf2-sha512$%'),
    role           text        NOT NULL DEFAULT 'user' CHECK (role IN ('user', 'admin')),
    is_active      boolean     NOT NULL DEFAULT true,
    token_version  integer     NOT NULL DEFAULT 0,      -- in every JWT ("ver"); bump = revoke all
    failed_logins  integer     NOT NULL DEFAULT 0,
    locked_until   timestamptz,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    last_login_at  timestamptz
);
-- case-insensitive uniqueness ("Alice" and "alice" are the same account)
CREATE UNIQUE INDEX IF NOT EXISTS users_username_lower_key ON agnes_auth.users (lower(username));

-- Refresh tokens: only SHA-256 of the cookie value is stored. One login = one family;
-- each refresh rotates (old row: used_at/revoked_at/replaced_by set). Presenting a
-- rotated token again = reuse -> whole family revoked.
CREATE TABLE IF NOT EXISTS agnes_auth.refresh_tokens (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id            bigint      NOT NULL REFERENCES agnes_auth.users (id) ON DELETE CASCADE,
    family_id          uuid        NOT NULL,
    token_hash         bytea       NOT NULL UNIQUE,
    issued_at          timestamptz NOT NULL DEFAULT now(),
    expires_at         timestamptz NOT NULL,
    family_expires_at  timestamptz NOT NULL,             -- absolute session lifetime
    used_at            timestamptz,
    revoked_at         timestamptz,
    revoke_reason      text,                             -- rotated|logout|reuse_detected|expired|user_inactive|admin
    replaced_by        uuid,
    user_agent         text,
    ip                 inet
);
CREATE INDEX IF NOT EXISTS refresh_tokens_user_idx   ON agnes_auth.refresh_tokens (user_id);
CREATE INDEX IF NOT EXISTS refresh_tokens_family_idx ON agnes_auth.refresh_tokens (family_id);

-- IP lockout counter / ban (dbapi ip_block + optional expiry).
CREATE TABLE IF NOT EXISTS agnes_auth.ip_block (
    ip             inet        PRIMARY KEY,
    lockout_count  integer     NOT NULL DEFAULT 0,
    blocked_at     timestamptz,                          -- NULL = not banned
    blocked_until  timestamptz,                          -- NULL with blocked_at set = permanent
    updated_at     timestamptz NOT NULL DEFAULT now()
);

-- Login audit trail; also used for per-IP failed-attempt throttling.
CREATE TABLE IF NOT EXISTS agnes_auth.login_audit (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    at          timestamptz NOT NULL DEFAULT now(),
    user_id     bigint      REFERENCES agnes_auth.users (id) ON DELETE SET NULL,
    username    text,
    ip          inet,
    success     boolean     NOT NULL,
    reason      text        NOT NULL,                    -- ok|bad_password|unknown_user|locked|...
    user_agent  text
);
CREATE INDEX IF NOT EXISTS login_audit_ip_at_idx ON agnes_auth.login_audit (ip, at);
CREATE INDEX IF NOT EXISTS login_audit_at_idx    ON agnes_auth.login_audit (at);

-- ---------------------------------------------------------------------------
-- TASK4 migration (2026-11-18): per-account API limits. Idempotent; applies cleanly on top of
-- the tables above (ADD COLUMN IF NOT EXISTS / CREATE TABLE IF NOT EXISTS).
-- Per-user overrides: NULL = use the system default from env (LIMITS_DEFAULT_*); 0 = none allowed.
ALTER TABLE agnes_auth.users ADD COLUMN IF NOT EXISTS rate_per_minute   integer CHECK (rate_per_minute   >= 0);
ALTER TABLE agnes_auth.users ADD COLUMN IF NOT EXISTS daily_quota       integer CHECK (daily_quota       >= 0);
ALTER TABLE agnes_auth.users ADD COLUMN IF NOT EXISTS video_daily_quota integer CHECK (video_daily_quota >= 0);
ALTER TABLE agnes_auth.users ADD COLUMN IF NOT EXISTS max_concurrent    integer CHECK (max_concurrent    >= 0);
COMMENT ON COLUMN agnes_auth.users.rate_per_minute   IS 'generation requests per sliding 60 s; NULL = LIMITS_DEFAULT_PER_MINUTE';
COMMENT ON COLUMN agnes_auth.users.daily_quota       IS 'generation requests (all kinds) per JST day; NULL = LIMITS_DEFAULT_PER_DAY';
COMMENT ON COLUMN agnes_auth.users.video_daily_quota IS 'video generations per JST day (also count toward daily_quota); NULL = LIMITS_DEFAULT_VIDEO_PER_DAY';
COMMENT ON COLUMN agnes_auth.users.max_concurrent    IS 'generation calls in flight at once; NULL = LIMITS_DEFAULT_CONCURRENT';

-- Daily usage counters. day = calendar date in Asia/Tokyo (computed by the service, passed in),
-- so the reset is 00:00 JST regardless of server/DB time zone.
CREATE TABLE IF NOT EXISTS agnes_auth.usage_daily (
    user_id     bigint      NOT NULL REFERENCES agnes_auth.users (id) ON DELETE CASCADE,
    day         date        NOT NULL,
    kind        text        NOT NULL CHECK (kind IN ('text', 'image', 'video', 'analyze', 'ingest')),
    count      integer     NOT NULL DEFAULT 0 CHECK (count >= 0),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, day, kind)
);
COMMENT ON TABLE agnes_auth.usage_daily IS 'Per-account generation counts per JST day (agnes-service limits)';

-- ---------------------------------------------------------------------------
-- TASK5 migration (2026-11-18): admin audit trail (dbapi AuditService style). Idempotent.
-- One row per admin mutation (create/update/reset_password/delete user, unblock_ip) and per
-- self-service change_password. `detail` holds only whitelisted, non-secret fields (never
-- passwords or hashes). No FKs to users on purpose: rows must survive the deletion of the
-- actor or target account, so the usernames are stored alongside the ids.
CREATE TABLE IF NOT EXISTS agnes_auth.admin_audit (
    id               bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at       timestamptz NOT NULL DEFAULT now(),
    actor_user_id    bigint,
    actor_username   text        NOT NULL,
    actor_ip         inet,
    action           text        NOT NULL,   -- create_user|update_user|reset_password|delete_user|unblock_ip|change_password
    target_user_id   bigint,
    target_username  text,
    detail           jsonb       NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX IF NOT EXISTS admin_audit_created_idx ON agnes_auth.admin_audit (created_at);
COMMENT ON TABLE agnes_auth.admin_audit IS 'Admin actions on accounts (agnes-service admin API); never contains passwords';

-- ---- call log (operator addition): one row per admitted or limiter-rejected generation call ----
CREATE TABLE IF NOT EXISTS agnes_auth.call_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at      timestamptz NOT NULL DEFAULT now(),
    user_id         bigint      NOT NULL REFERENCES agnes_auth.users(id) ON DELETE CASCADE,
    username        text        NOT NULL,
    kind            text        NOT NULL CHECK (kind IN ('text', 'image', 'video', 'analyze', 'ingest')),
    model           text,
    outcome         text        NOT NULL,
    http_status     integer     NOT NULL,
    upstream_status integer,
    duration_ms     integer     NOT NULL DEFAULT 0,
    prompt_preview  text,
    detail          text
);
CREATE INDEX IF NOT EXISTS call_log_created_idx ON agnes_auth.call_log (created_at DESC);
CREATE INDEX IF NOT EXISTS call_log_user_created_idx ON agnes_auth.call_log (user_id, created_at DESC);
COMMENT ON TABLE agnes_auth.call_log IS 'Generation calls for the admin call-log view; prompt_preview truncated to 120 chars';

-- ---------------------------------------------------------------------------
-- TASK6 phase 1 migration (2026-11-19): kind 'analyze' (POST /api/analyze). Idempotent.
-- Widens the kind CHECK of usage_daily and call_log (values only, backward compatible); the
-- constraint names usage_daily_kind_check / call_log_kind_check are kept. Re-running is a no-op
-- once both definitions already contain 'analyze'. Apply this before deploying the code.
DO $$
DECLARE
    t text;
    def text;
BEGIN
    FOREACH t IN ARRAY ARRAY['usage_daily', 'call_log'] LOOP
        SELECT pg_get_constraintdef(c.oid) INTO def
          FROM pg_constraint c
         WHERE c.conrelid = format('agnes_auth.%I', t)::regclass AND c.conname = t || '_kind_check';
        IF def IS NULL OR position('''analyze''' IN def) = 0 THEN
            IF def IS NOT NULL THEN
                EXECUTE format('ALTER TABLE agnes_auth.%I DROP CONSTRAINT %I', t, t || '_kind_check');
            END IF;
            EXECUTE format('ALTER TABLE agnes_auth.%I ADD CONSTRAINT %I CHECK (kind IN (''text'', ''image'', ''video'', ''analyze''))',
                           t, t || '_kind_check');
        END IF;
    END LOOP;
END
$$;

-- ---------------------------------------------------------------------------
-- TASK6 phase 2 migration (2026-11-19): reading ingest orchestration. Idempotent; apply before the code.
--  * users.reading_ingest  none|web|agent (default none) — who may call POST /api/reading/ingest.
--    Grants are made through the admin API or the CLI (audited); this script never UPDATEs accounts.
--  * users.muse_id         ^A\d{3}$ — agent account → Muse id used as added_by (agent channel).
--  * kind 'ingest' in the usage_daily / call_log CHECKs (same DO-block pattern as phase 1).
--  * agnes_auth.ingest_jobs — persistent ingest jobs (survive restarts; delayed retries; L2 list).
ALTER TABLE agnes_auth.users ADD COLUMN IF NOT EXISTS reading_ingest text NOT NULL DEFAULT 'none';
ALTER TABLE agnes_auth.users ADD COLUMN IF NOT EXISTS muse_id text;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'agnes_auth.users'::regclass
                   AND conname = 'users_reading_ingest_check') THEN
        ALTER TABLE agnes_auth.users ADD CONSTRAINT users_reading_ingest_check
            CHECK (reading_ingest IN ('none', 'web', 'agent'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'agnes_auth.users'::regclass
                   AND conname = 'users_muse_id_check') THEN
        ALTER TABLE agnes_auth.users ADD CONSTRAINT users_muse_id_check CHECK (muse_id ~ '^A[0-9]{3}$');
    END IF;
END
$$;
CREATE UNIQUE INDEX IF NOT EXISTS users_muse_id_key ON agnes_auth.users (muse_id) WHERE muse_id IS NOT NULL;
COMMENT ON COLUMN agnes_auth.users.reading_ingest IS 'POST /api/reading/ingest permission: none | web (own 8793 account, 添加文章 tab / /add; title+text only) | agent (MuseAgent JWT)';
COMMENT ON COLUMN agnes_auth.users.muse_id IS 'agent channel: Muse id (A001…) written as added_by; NULL = not an agent account';

DO $$
DECLARE
    t text;
    def text;
BEGIN
    FOREACH t IN ARRAY ARRAY['usage_daily', 'call_log'] LOOP
        SELECT pg_get_constraintdef(c.oid) INTO def
          FROM pg_constraint c
         WHERE c.conrelid = format('agnes_auth.%I', t)::regclass AND c.conname = t || '_kind_check';
        IF def IS NULL OR position('''ingest''' IN def) = 0 THEN
            IF def IS NOT NULL THEN
                EXECUTE format('ALTER TABLE agnes_auth.%I DROP CONSTRAINT %I', t, t || '_kind_check');
            END IF;
            EXECUTE format('ALTER TABLE agnes_auth.%I ADD CONSTRAINT %I CHECK (kind IN (''text'', ''image'', ''video'', ''analyze'', ''ingest''))',
                           t, t || '_kind_check');
        END IF;
    END LOOP;
END
$$;

CREATE TABLE IF NOT EXISTS agnes_auth.ingest_jobs (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         bigint      NOT NULL REFERENCES agnes_auth.users (id) ON DELETE CASCADE,
    username        text        NOT NULL,
    channel         text        NOT NULL CHECK (channel IN ('agent', 'web')),
    added_by        text,
    share_id        text,
    base_version    integer,
    title           text,
    lang            text        NOT NULL DEFAULT 'ja',
    rate            text,
    source_text     text,                                  -- kept while the share is pending (T state)
    stage           text        NOT NULL DEFAULT 'share'
                                CHECK (stage IN ('share', 'preview', 'enrich', 'commit', 'done', 'l2-pending', 'failed')),
    status          text        NOT NULL DEFAULT 'queued'
                                CHECK (status IN ('queued', 'running', 'retry', 'done', 'l2', 'failed', 'cancelled')),
    provider_model  text,
    chunks          jsonb,
    trail           text,
    error_code      text,
    l2_reason       text        CHECK (l2_reason IN ('api-down', 'quota', 'quality', 'deadline')),
    attempt         integer     NOT NULL DEFAULT 0,       -- delayed (whole-article) retries done
    t_attempts      integer     NOT NULL DEFAULT 0,       -- T-state (TtsWeb unavailable) retries in a row
    t_since         timestamptz,
    next_retry_at   timestamptz,
    result          jsonb,                                 -- article status/version, fx counts, tr_via …
    call_log_id     bigint,
    l2_closed_at    timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    finished_at     timestamptz,
    expires_at      timestamptz
);
-- "same article only once": at most one active job per share_id
CREATE UNIQUE INDEX IF NOT EXISTS ingest_jobs_share_active_uq ON agnes_auth.ingest_jobs (share_id)
    WHERE status IN ('queued', 'running', 'retry');
CREATE INDEX IF NOT EXISTS ingest_jobs_retry_idx ON agnes_auth.ingest_jobs (next_retry_at) WHERE status = 'retry';
CREATE INDEX IF NOT EXISTS ingest_jobs_status_idx ON agnes_auth.ingest_jobs (status, created_at DESC);
CREATE INDEX IF NOT EXISTS ingest_jobs_user_idx ON agnes_auth.ingest_jobs (user_id, created_at DESC);
COMMENT ON TABLE agnes_auth.ingest_jobs IS 'Reading ingest jobs (POST /api/reading/ingest): share-first, L1 fallback, L2 hand-off marks';

-- 2026-11-20 delete / edit (DELETE-EDIT-REPORT.md): text edits of an existing article run as ingest jobs too.
-- op='edit': share_id + source_text (the new normalized original) + base_version → new version on the same share_id.
-- Idempotent and additive (old code never reads the column; default keeps existing rows 'ingest').
ALTER TABLE agnes_auth.ingest_jobs ADD COLUMN IF NOT EXISTS op text NOT NULL DEFAULT 'ingest';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'agnes_auth.ingest_jobs'::regclass
                   AND conname = 'ingest_jobs_op_check') THEN
        ALTER TABLE agnes_auth.ingest_jobs ADD CONSTRAINT ingest_jobs_op_check CHECK (op IN ('ingest', 'edit'));
    END IF;
END
$$;
COMMENT ON COLUMN agnes_auth.ingest_jobs.op IS 'ingest = new article / share_id job; edit = text edit of an existing article (new version, same share_id)';

-- Optional least-privilege role: if a role named agnes_service exists (create it
-- yourself, see README), give it exactly what the service needs on this schema.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'agnes_service') THEN
        GRANT USAGE ON SCHEMA agnes_auth TO agnes_service;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA agnes_auth TO agnes_service;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA agnes_auth TO agnes_service;
    END IF;
END
$$;

COMMIT;
