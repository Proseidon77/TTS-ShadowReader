"""Authentication for agnes-service: JWT (HS256, stdlib), PBKDF2 passwords, and
PostgreSQL-backed users / refresh tokens / lockout in schema ``agnes_auth``.

Modelled on the dbapi service on this machine (see DESIGN.md "Auth"):
  * access token  = short-lived HS256 JWT, sent as ``Authorization: Bearer``;
    carries ``ver`` (users.token_version) so bumping the version kills all tokens.
  * refresh token = random opaque value in an httpOnly cookie, stored only as a
    SHA-256 hash; rotated on every use; reuse of a rotated token revokes the
    whole token family; absolute family lifetime caps "remember me".
  * passwords     = ``pbkdf2-sha512$210000$<salt b64>$<hash b64>`` (same format as dbapi).
  * lockout       = 5 failed logins -> account locked 15 min; repeated lockouts
    from one IP -> ``ip_block`` row (IP banned).

Only stdlib is used, except the PostgreSQL driver psycopg (v3), imported lazily so
the service still starts (and /health works) when it is not installed.
"""

import base64
import hashlib
import hmac
import ipaddress
import json
import logging
import re
import secrets
import threading
import time
import uuid

from common import ApiError, int_setting, setting

try:                                    # pip install "psycopg[binary]"
    import psycopg
except ImportError:                     # pragma: no cover - depends on host
    psycopg = None

log = logging.getLogger("agnes-service.auth")

SCHEMA = "agnes_auth"                   # fixed (not configurable) - used in SQL text
ISSUER = "agnes-service"
AUDIENCE = "agnes-service"
COOKIE_NAME = "agnes_rt"
COOKIE_PATH = "/api/auth"
JWT_LEEWAY = 30                         # seconds of clock skew tolerated on exp/nbf
MIN_SECRET_LEN = 32

PBKDF2_SCHEME = "pbkdf2-sha512"
PBKDF2_ITERATIONS = 210000              # same as dbapi / OWASP recommendation for SHA-512
PBKDF2_SALT_LEN = 16
PBKDF2_HASH_LEN = 64

USERNAME_RE = re.compile(r"^[a-zA-Z0-9_.-]{3,32}$")   # same rule as dbapi's app_user
ROLES = ("user", "admin")
MIN_PASSWORD_LEN = 10
MAX_PASSWORD_LEN = 1024

ROTATION_GRACE_S = 10    # a just-rotated token presented again within this window is
                         # treated as a benign race (two tabs), not as theft


def access_ttl():
    return max(5, min(int_setting("JWT_ACCESS_MINUTES", 30), 24 * 60)) * 60


def refresh_ttl():
    return max(1, min(int_setting("REFRESH_TOKEN_DAYS", 7), 90)) * 86400


def family_ttl():
    return max(1, min(int_setting("REFRESH_FAMILY_DAYS", 30), 365)) * 86400


def max_failed_logins():
    return max(1, int_setting("LOGIN_MAX_FAILURES", 5))


def lockout_minutes():
    return max(1, int_setting("LOGIN_LOCKOUT_MINUTES", 15))


def ip_ban_lockouts():
    return max(1, int_setting("IP_BAN_AFTER_LOCKOUTS", 3))


def ip_ban_hours():
    return max(0, int_setting("IP_BAN_HOURS", 24))       # 0 = permanent (dbapi style)


def ip_max_failures():
    return max(1, int_setting("IP_MAX_FAILURES", 20))      # per 15 min, any username


# --------------------------------------------------------------------------- passwords

def _b64(b):
    return base64.b64encode(b).decode("ascii")


def hash_password(password, iterations=PBKDF2_ITERATIONS):
    salt = secrets.token_bytes(PBKDF2_SALT_LEN)
    dk = hashlib.pbkdf2_hmac("sha512", password.encode("utf-8"), salt, iterations,
                             PBKDF2_HASH_LEN)
    return "%s$%d$%s$%s" % (PBKDF2_SCHEME, iterations, _b64(salt), _b64(dk))


def verify_password(password, stored):
    try:
        scheme, iters, salt_b64, hash_b64 = stored.split("$")
        iters = int(iters)
        salt = base64.b64decode(salt_b64, validate=True)
        expected = base64.b64decode(hash_b64, validate=True)
    except (ValueError, AttributeError):
        return False
    if scheme != PBKDF2_SCHEME or not 1000 <= iters <= 5000000 or not expected:
        return False
    dk = hashlib.pbkdf2_hmac("sha512", password.encode("utf-8"), salt, iters, len(expected))
    return hmac.compare_digest(dk, expected)


_dummy = {}


def _dummy_hash():
    """Hash used to spend the same time on unknown usernames (no user enumeration by timing)."""
    if "h" not in _dummy:
        _dummy["h"] = hash_password(secrets.token_urlsafe(16))
    return _dummy["h"]


def check_password_policy(password):
    if len(password) < MIN_PASSWORD_LEN:
        return "password must be at least %d characters" % MIN_PASSWORD_LEN
    if len(password) > MAX_PASSWORD_LEN:
        return "password too long"
    if len(set(password)) < 4:
        return "password is too simple"
    return None


# --------------------------------------------------------------------------- JWT (HS256)

def _b64url(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode("ascii")


def _b64url_dec(s):
    s = s.encode("ascii") if isinstance(s, str) else s
    return base64.urlsafe_b64decode(s + b"=" * (-len(s) % 4))


def jwt_secret_status():
    s = setting("JWT_SECRET")
    if not s:
        return "missing"
    if len(s) < MIN_SECRET_LEN:
        return "too_short"
    return "ok"


def _require_secret():
    st = jwt_secret_status()
    if st == "missing":
        raise ApiError(503, "JWT_SECRET not configured - the service cannot issue or "
                       "verify tokens", code="jwt_secret_missing")
    if st == "too_short":
        raise ApiError(503, "JWT_SECRET is too short (need >= %d chars)" % MIN_SECRET_LEN,
                       code="jwt_secret_missing")
    return setting("JWT_SECRET").encode("utf-8")


def jwt_encode(claims, secret):
    header = {"alg": "HS256", "typ": "JWT"}
    signing_input = (_b64url(json.dumps(header, separators=(",", ":")).encode()) + "." +
                     _b64url(json.dumps(claims, separators=(",", ":")).encode()))
    sig = hmac.new(secret, signing_input.encode("ascii"), hashlib.sha256).digest()
    return signing_input + "." + _b64url(sig)


def jwt_decode(token, secret, now=None):
    """Verify signature + standard claims. Raises ValueError with a short reason."""
    parts = token.split(".")
    if len(parts) != 3:
        raise ValueError("malformed token")
    try:
        header = json.loads(_b64url_dec(parts[0]))
        claims = json.loads(_b64url_dec(parts[1]))
        sig = _b64url_dec(parts[2])
    except (ValueError, UnicodeDecodeError, TypeError):
        raise ValueError("malformed token")
    if not isinstance(header, dict) or header.get("alg") != "HS256":
        raise ValueError("unsupported token algorithm")       # blocks alg=none etc.
    expected = hmac.new(secret, (parts[0] + "." + parts[1]).encode("ascii"),
                        hashlib.sha256).digest()
    if not hmac.compare_digest(sig, expected):
        raise ValueError("bad signature")
    if not isinstance(claims, dict):
        raise ValueError("malformed token")
    now = time.time() if now is None else now
    exp = claims.get("exp")
    if not isinstance(exp, (int, float)) or now > exp + JWT_LEEWAY:
        raise ValueError("token expired")
    nbf = claims.get("nbf")
    if isinstance(nbf, (int, float)) and now + JWT_LEEWAY < nbf:
        raise ValueError("token not yet valid")
    if claims.get("iss") != ISSUER:
        raise ValueError("wrong issuer")
    aud = claims.get("aud")
    if aud != AUDIENCE and not (isinstance(aud, list) and AUDIENCE in aud):
        raise ValueError("wrong audience")
    if claims.get("typ") != "access":
        raise ValueError("not an access token")
    return claims


def issue_access_token(user_id, username, role, token_version):
    secret = _require_secret()
    now = int(time.time())
    ttl = access_ttl()
    claims = {"iss": ISSUER, "aud": AUDIENCE, "sub": str(user_id), "name": username,
              "role": role, "ver": token_version, "typ": "access",
              "iat": now, "nbf": now, "exp": now + ttl, "jti": uuid.uuid4().hex}
    return jwt_encode(claims, secret), ttl


# --------------------------------------------------------------------------- database

def db_dsn():
    return setting("AGNES_DB_DSN")


def connect():
    """New connection per call (low traffic; unix-socket connects are ~ms)."""
    if psycopg is None:
        raise ApiError(503, "PostgreSQL driver not installed (operator: pip install "
                       "'psycopg[binary]')", code="db_unavailable")
    dsn = db_dsn()
    if not dsn:
        raise ApiError(503, "AGNES_DB_DSN not configured", code="db_unavailable")
    try:
        return psycopg.connect(dsn, connect_timeout=5)
    except psycopg.Error as e:
        log.warning("auth db connect failed: %s", type(e).__name__)
        raise ApiError(503, "auth database unavailable", code="db_unavailable")


_db_status_cache = {"t": 0.0, "v": None}


def db_status():
    """For /health: driver / dsn / reachable / schema_ready (cached 30 s)."""
    st = {"driver": psycopg is not None, "dsn_configured": bool(db_dsn()),
          "reachable": None, "schema_ready": None}
    if not (st["driver"] and st["dsn_configured"]):
        return st
    if _db_status_cache["v"] and time.time() - _db_status_cache["t"] < 30:
        return _db_status_cache["v"]
    try:
        with psycopg.connect(db_dsn(), connect_timeout=3) as conn:
            st["reachable"] = True
            row = conn.execute("SELECT to_regclass(%s) IS NOT NULL AND "
                               "to_regclass(%s) IS NOT NULL AND to_regclass(%s) IS NOT NULL",
                               (SCHEMA + ".users", SCHEMA + ".refresh_tokens",
                                SCHEMA + ".ip_block")).fetchone()
            st["schema_ready"] = bool(row and row[0])
    except Exception as e:
        log.warning("auth db health check failed: %s", type(e).__name__)
        st["reachable"] = False
    _db_status_cache.update(t=time.time(), v=st)
    return st


def _sha256(token):
    return hashlib.sha256(token.encode("ascii")).digest()


def _audit(conn, user_id, username, ip, success, reason, user_agent):
    conn.execute(
        "INSERT INTO agnes_auth.login_audit (user_id, username, ip, success, reason, user_agent) "
        "VALUES (%s, %s, %s::inet, %s, %s, %s)",
        (user_id, (username or "")[:64], ip, success, reason, (user_agent or "")[:300]))


def _ip_exempt(ip):
    try:
        return ipaddress.ip_address(ip).is_loopback
    except ValueError:
        return True


def _ip_blocked(conn, ip):
    row = conn.execute(
        "SELECT blocked_until FROM agnes_auth.ip_block WHERE ip = %s::inet "
        "AND blocked_at IS NOT NULL AND (blocked_until IS NULL OR blocked_until > now())",
        (ip,)).fetchone()
    return row is not None


def _register_ip_lockout(conn, ip):
    """Count a lockout event against an IP; ban the IP after N events within 24 h."""
    row = conn.execute(
        "INSERT INTO agnes_auth.ip_block (ip, lockout_count, updated_at) "
        "VALUES (%s::inet, 1, now()) ON CONFLICT (ip) DO UPDATE SET "
        "lockout_count = CASE WHEN agnes_auth.ip_block.updated_at < now() - interval '24 hours' "
        "THEN 1 ELSE agnes_auth.ip_block.lockout_count + 1 END, updated_at = now() "
        "RETURNING lockout_count", (ip,)).fetchone()
    if row and row[0] >= ip_ban_lockouts():
        if _ip_exempt(ip):
            log.warning("ip %s reached ban threshold but is loopback - not banned", ip)
            return
        hours = ip_ban_hours()
        conn.execute(
            "UPDATE agnes_auth.ip_block SET blocked_at = now(), lockout_count = 0, "
            "blocked_until = CASE WHEN %s::int > 0 THEN now() + make_interval(hours => %s::int) END, "
            "updated_at = now() WHERE ip = %s::inet", (hours, hours, ip))
        log.warning("ip %s banned after repeated lockouts", ip)


def _recent_ip_failures(conn, ip):
    row = conn.execute(
        "SELECT count(*) FROM agnes_auth.login_audit WHERE ip = %s::inet AND NOT success "
        "AND at > now() - interval '15 minutes'", (ip,)).fetchone()
    return row[0] if row else 0


def _new_refresh_token(conn, user_id, family_id, family_expires_at, ip, user_agent):
    """Insert a refresh token; returns (plain_token, row_id, max_age_seconds)."""
    plain = secrets.token_urlsafe(48)
    row = conn.execute(
        "INSERT INTO agnes_auth.refresh_tokens (user_id, family_id, token_hash, expires_at, "
        "family_expires_at, user_agent, ip) VALUES (%s, %s, %s, "
        "LEAST(now() + make_interval(secs => %s::double precision), %s), %s, %s, %s::inet) "
        "RETURNING id, extract(epoch FROM (expires_at - now()))::int",
        (user_id, family_id, _sha256(plain), refresh_ttl(), family_expires_at,
         family_expires_at, (user_agent or "")[:300], ip)).fetchone()
    return plain, row[0], max(0, row[1])


def _user_view(user_id, username, role):
    return {"id": user_id, "username": username, "role": role}


def login(username, password, ip, user_agent):
    """Returns dict with access token + user, plus refresh cookie value and max-age."""
    _require_secret()
    username = (username or "").strip() if isinstance(username, str) else ""
    password = password if isinstance(password, str) else ""
    if not username or not password:
        raise ApiError(400, "username and password are required", code="bad_request")
    if len(password) > MAX_PASSWORD_LEN or len(username) > 64:
        raise ApiError(400, "username or password too long", code="bad_request")

    err = None
    result = None
    with connect() as conn:
        if _ip_blocked(conn, ip):
            _audit(conn, None, username, ip, False, "ip_blocked", user_agent)
            err = ApiError(403, "too many failed logins from this IP; it is blocked",
                           code="ip_blocked")
        elif _recent_ip_failures(conn, ip) >= ip_max_failures():
            _audit(conn, None, username, ip, False, "ip_throttled", user_agent)
            err = ApiError(429, "too many failed logins from this IP, try again later",
                           code="ip_throttled", headers=[("Retry-After", "900")])
        else:
            row = None
            if USERNAME_RE.match(username):
                row = conn.execute(
                    "SELECT id, username, password_hash, role, is_active, token_version, "
                    "locked_until, locked_until IS NOT NULL AND locked_until > now(), "
                    "extract(epoch FROM (locked_until - now()))::int "
                    "FROM agnes_auth.users WHERE lower(username) = lower(%s) FOR UPDATE",
                    (username,)).fetchone()
            if row is None:
                verify_password(password, _dummy_hash())
                _audit(conn, None, username, ip, False, "unknown_user", user_agent)
                err = ApiError(401, "invalid username or password", code="invalid_credentials")
            else:
                uid, uname, phash, role, active, ver, _lu, locked, locked_secs = row
                if locked:
                    _audit(conn, uid, uname, ip, False, "locked", user_agent)
                    err = ApiError(423, "account locked after too many failed logins",
                                   code="locked", headers=[("Retry-After", str(locked_secs))],
                                   extra={"retry_after": locked_secs})
                elif not verify_password(password, phash):
                    n = conn.execute(
                        "UPDATE agnes_auth.users SET failed_logins = failed_logins + 1, "
                        "updated_at = now() WHERE id = %s RETURNING failed_logins",
                        (uid,)).fetchone()[0]
                    if n >= max_failed_logins():
                        mins = lockout_minutes()
                        conn.execute(
                            "UPDATE agnes_auth.users SET failed_logins = 0, locked_until = "
                            "now() + make_interval(mins => %s::int) WHERE id = %s", (mins, uid))
                        _audit(conn, uid, uname, ip, False, "bad_password_locked", user_agent)
                        _register_ip_lockout(conn, ip)
                        err = ApiError(423, "account locked after too many failed logins",
                                       code="locked", headers=[("Retry-After", str(mins * 60))],
                                       extra={"retry_after": mins * 60})
                    else:
                        _audit(conn, uid, uname, ip, False, "bad_password", user_agent)
                        err = ApiError(401, "invalid username or password",
                                       code="invalid_credentials")
                elif not active:
                    _audit(conn, uid, uname, ip, False, "inactive", user_agent)
                    err = ApiError(403, "account disabled", code="disabled")
                else:
                    conn.execute(
                        "UPDATE agnes_auth.users SET failed_logins = 0, locked_until = NULL, "
                        "last_login_at = now(), updated_at = now() WHERE id = %s", (uid,))
                    fam_exp = conn.execute(
                        "SELECT now() + make_interval(secs => %s::double precision)",
                        (family_ttl(),)).fetchone()[0]
                    plain, _rid, max_age = _new_refresh_token(conn, uid, uuid.uuid4(), fam_exp,
                                                              ip, user_agent)
                    # housekeeping: drop this user's long-dead refresh tokens
                    conn.execute(
                        "DELETE FROM agnes_auth.refresh_tokens WHERE user_id = %s AND "
                        "family_expires_at < now() - interval '30 days'", (uid,))
                    _audit(conn, uid, uname, ip, True, "ok", user_agent)
                    token, ttl = issue_access_token(uid, uname, role, ver)
                    result = ({"access_token": token, "token_type": "Bearer",
                               "expires_in": ttl, "user": _user_view(uid, uname, role)},
                              plain, max_age)
        if err is not None and err.status in (401, 423) and \
                _recent_ip_failures(conn, ip) == ip_max_failures():
            _register_ip_lockout(conn, ip)       # IP just crossed the spray threshold
    # the with-block committed: failure counters/audit rows persist even on error
    if err is not None:
        log.info("login failed for ip %s: %s", ip, err.code)
        raise err
    _forget_user_state()
    return result


def refresh(plain, ip, user_agent):
    """Rotate a refresh token. Returns (body, new_plain, max_age)."""
    _require_secret()
    if not plain or len(plain) > 200:
        raise ApiError(401, "no refresh token", code="no_refresh_token")
    err = None
    result = None
    with connect() as conn:
        row = conn.execute(
            "SELECT rt.id, rt.user_id, rt.family_id, rt.family_expires_at, "
            "rt.expires_at <= now() OR rt.family_expires_at <= now(), "
            "rt.revoked_at IS NOT NULL, rt.used_at IS NOT NULL AND "
            "rt.used_at > now() - make_interval(secs => %s::double precision), "
            "u.username, u.role, u.is_active, u.token_version "
            "FROM agnes_auth.refresh_tokens rt JOIN agnes_auth.users u ON u.id = rt.user_id "
            "WHERE rt.token_hash = %s FOR UPDATE OF rt",
            (ROTATION_GRACE_S, _sha256(plain))).fetchone()
        if row is None:
            err = ApiError(401, "invalid refresh token", code="invalid_refresh_token")
        else:
            rid, uid, fam, fam_exp, expired, revoked, just_rotated, uname, role, active, ver = row
            if revoked:
                if just_rotated:
                    # concurrent refresh from another tab: don't punish, just refuse
                    err = ApiError(401, "refresh token already rotated",
                                   code="invalid_refresh_token")
                else:
                    n = conn.execute(
                        "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), "
                        "revoke_reason = 'reuse_detected' WHERE family_id = %s "
                        "AND revoked_at IS NULL", (fam,)).rowcount
                    _audit(conn, uid, uname, ip, False, "refresh_reuse", user_agent)
                    log.warning("refresh token reuse detected for user %s (revoked %d)", uid, n)
                    err = ApiError(401, "refresh token reuse detected; session revoked",
                                   code="invalid_refresh_token")
            elif expired or not active:
                conn.execute(
                    "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), revoke_reason = %s "
                    "WHERE family_id = %s AND revoked_at IS NULL",
                    ("expired" if expired else "user_inactive", fam))
                err = ApiError(401, "session expired" if expired else "account disabled",
                               code="invalid_refresh_token")
            else:
                new_plain, new_id, max_age = _new_refresh_token(conn, uid, fam, fam_exp, ip,
                                                                user_agent)
                conn.execute(
                    "UPDATE agnes_auth.refresh_tokens SET used_at = now(), revoked_at = now(), "
                    "revoke_reason = 'rotated', replaced_by = %s WHERE id = %s", (new_id, rid))
                token, ttl = issue_access_token(uid, uname, role, ver)
                result = ({"access_token": token, "token_type": "Bearer", "expires_in": ttl,
                           "user": _user_view(uid, uname, role)}, new_plain, max_age)
    if err is not None:
        raise err
    return result


def logout(plain):
    """Revoke the session (refresh-token family) the cookie belongs to. Never raises."""
    if not plain or len(plain) > 200:
        return False
    try:
        with connect() as conn:
            n = conn.execute(
                "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), revoke_reason = 'logout' "
                "WHERE revoked_at IS NULL AND family_id = (SELECT family_id FROM "
                "agnes_auth.refresh_tokens WHERE token_hash = %s)", (_sha256(plain),)).rowcount
        return n > 0
    except ApiError:
        return False
    except Exception as e:
        log.warning("logout revoke failed: %s", type(e).__name__)
        return False


# Per-user state cache for access-token checks (is_active / token_version / role),
# so a disabled user or a bumped token_version takes effect within USER_CACHE_S.
USER_CACHE_S = 10
_user_cache = {}
_user_cache_lock = threading.Lock()


def _forget_user_state():
    with _user_cache_lock:
        _user_cache.clear()


def _user_state(user_id):
    now = time.time()
    with _user_cache_lock:
        hit = _user_cache.get(user_id)
        if hit and now - hit[0] < USER_CACHE_S:
            return hit[1]
    try:
        with connect() as conn:
            row = conn.execute("SELECT is_active, token_version, role, username FROM "
                               "agnes_auth.users WHERE id = %s", (user_id,)).fetchone()
    except ApiError:
        raise
    except Exception as e:
        log.warning("auth db query failed: %s", type(e).__name__)
        raise ApiError(503, "auth database unavailable", code="db_unavailable")
    with _user_cache_lock:
        if len(_user_cache) > 1000:
            _user_cache.clear()
        _user_cache[user_id] = (now, row)
    return row


def _unauthorized(msg, error="invalid_token"):
    hdr = 'Bearer realm="agnes-service"'
    if error:
        hdr += ', error="%s"' % error
    return ApiError(401, msg, code="unauthorized", headers=[("WWW-Authenticate", hdr)])


def authenticate(authorization_header):
    """Validate 'Authorization: Bearer <jwt>'. Returns the claims dict or raises 401/503."""
    h = (authorization_header or "").strip()
    if not h.lower().startswith("bearer ") or not h[7:].strip():
        raise _unauthorized("authentication required (Authorization: Bearer <token>)",
                            error=None)
    secret = _require_secret()
    try:
        claims = jwt_decode(h[7:].strip(), secret)
    except ValueError as e:
        raise _unauthorized("invalid token: %s" % e)
    try:
        uid = int(claims.get("sub"))
    except (TypeError, ValueError):
        raise _unauthorized("invalid token: bad subject")
    state = _user_state(uid)
    if state is None:
        raise _unauthorized("invalid token: unknown user")
    active, ver, role, _uname = state
    if not active:
        raise _unauthorized("account disabled")
    if claims.get("ver") != ver:
        raise _unauthorized("token revoked")
    if role not in ROLES:
        raise ApiError(403, "role not allowed", code="forbidden")
    claims["role"] = role            # DB is the source of truth for the role
    return claims
