#!/usr/bin/env python3
"""Smoke test for agnes-service (stdlib only).

Usage: python3 smoke.py [BASE_URL]               (default http://127.0.0.1:8793)
Env:   SMOKE_USER / SMOKE_PASSWORD  an existing test account (create it with
                                    create_user.py). Without them the login part is skipped.
       SMOKE_IMAGE=1                also run one real image generation (costs quota)

Checks:
  * /health, /, 404
  * unauthenticated calls -> 401 (generation, models, video status, /api/auth/me),
    garbage / forged / expired bearer tokens -> 401, wrong password -> 401
  * login -> access token + httpOnly refresh cookie; authenticated calls pass the auth
    layer (/api/auth/me, /api/models, /api/text {} -> 400, no key -> 503)
  * refresh rotation: new token + new cookie; old cookie rejected; logout revokes cookie
  * one real text generation only when /health says key_configured
Exit code 0 = all checks passed.
"""

import base64
import hashlib
import hmac
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8793").rstrip("/")
USER = os.environ.get("SMOKE_USER", "")
PASSWORD = os.environ.get("SMOKE_PASSWORD", "")
failures = 0


def req(method, path, body=None, token=None, cookie=None, timeout=400, raw_auth=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    if raw_auth:
        headers["Authorization"] = raw_auth
    if cookie:
        headers["Cookie"] = "agnes_rt=" + cookie
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(r, timeout=timeout) as resp:
            return resp.status, resp.read(), resp.headers
    except urllib.error.HTTPError as e:
        return e.code, e.read(), e.headers


def js(body):
    try:
        return json.loads(body)
    except Exception:
        return {}


def check(name, ok, detail=""):
    global failures
    print(("PASS " if ok else "FAIL ") + name + (" - " + detail if detail else ""))
    if not ok:
        failures += 1


def refresh_cookie(headers):
    """Return (value, set_cookie_header) of the agnes_rt cookie from Set-Cookie headers."""
    for h in headers.get_all("Set-Cookie") or []:
        if h.startswith("agnes_rt="):
            return h.split(";", 1)[0][len("agnes_rt="):], h
    return None, ""


def b64url(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def fake_jwt(secret, exp_offset):
    now = int(time.time())
    claims = {"iss": "agnes-service", "aud": "agnes-service", "sub": "1", "name": "x",
              "role": "user", "ver": 0, "typ": "access", "iat": now - 7200,
              "nbf": now - 7200, "exp": now + exp_offset}
    si = b64url(b'{"alg":"HS256","typ":"JWT"}') + "." + b64url(json.dumps(claims).encode())
    return si + "." + b64url(hmac.new(secret, si.encode(), hashlib.sha256).digest())


def main():
    st, body, _ = req("GET", "/health")
    health = js(body) if st == 200 else {}
    check("GET /health", st == 200 and health.get("status") == "ok", body.decode()[:300])
    key = bool(health.get("key_configured"))
    a = health.get("auth") or {}
    print("     auth status: jwt_secret=%s db=%s" % (a.get("jwt_secret"), a.get("db")))

    st, body, hdr = req("GET", "/")
    check("GET / serves HTML", st == 200 and "text/html" in hdr.get("Content-Type", "")
          and b"Agnes" in body, "%d bytes" % len(body))

    st, _, _ = req("GET", "/nope")
    check("unknown path -> 404", st == 404)

    # ---- unauthenticated access must be refused
    for method, path, b in (("POST", "/api/text", {"prompt": "hi"}),
                            ("POST", "/api/image", {"prompt": "hi"}),
                            ("POST", "/api/video", {"prompt": "hi"}),
                            ("GET", "/api/video/status/video_x", None),
                            ("GET", "/api/models", None),
                            ("GET", "/api/auth/me", None)):
        st, body, hdr = req(method, path, b)
        check("%s %s without token -> 401" % (method, path),
              st == 401 and "Bearer" in (hdr.get("WWW-Authenticate") or ""), body.decode()[:120])

    st, body, _ = req("POST", "/api/text", {"prompt": "hi"}, raw_auth="Bearer not.a.jwt")
    # 401 normally; 503 if the server has no JWT_SECRET (it then cannot verify anything)
    check("garbage bearer token -> 401", st == 401 or (st == 503 and a.get("jwt_secret") != "ok"),
          "%d %s" % (st, body.decode()[:120]))
    st, body, _ = req("POST", "/api/text", {"prompt": "hi"},
                      token=fake_jwt(b"wrong-secret-wrong-secret-wrong-secret!", 3600))
    check("forged token (wrong secret) -> 401",
          st == 401 or (st == 503 and a.get("jwt_secret") != "ok"), "%d" % st)

    secret = os.environ.get("JWT_SECRET") or _env_value("JWT_SECRET")
    if secret:
        st, body, _ = req("POST", "/api/text", {"prompt": "hi"},
                          token=fake_jwt(secret.encode(), -3600))
        check("expired token (right secret) -> 401", st == 401 and b"expired" in body,
              body.decode()[:120])
    else:
        print("SKIP expired-token check (JWT_SECRET not readable here)")

    st, body, _ = req("POST", "/api/auth/login",
                      {"username": "smoke_no_such_user", "password": "wrong-password-123"})
    check("login unknown user -> 401", st == 401, "%d %s" % (st, body.decode()[:120]))

    if not (USER and PASSWORD):
        print("SKIP login/refresh/authenticated checks (set SMOKE_USER and SMOKE_PASSWORD; "
              "create the account with create_user.py)")
        return finish()

    # ---- login
    st, body, hdr = req("POST", "/api/auth/login", {"username": USER, "password": PASSWORD})
    d = js(body)
    tok = d.get("access_token")
    c1, set_cookie = refresh_cookie(hdr)
    check("login -> 200 + access_token", st == 200 and bool(tok),
          "%d expires_in=%s %s" % (st, d.get("expires_in"), "" if tok else body.decode()[:200]))
    check("login sets httpOnly SameSite refresh cookie",
          bool(c1) and "HttpOnly" in set_cookie and "SameSite=Strict" in set_cookie
          and "Path=/api/auth" in set_cookie, set_cookie.split(";", 1)[-1].strip())
    if not tok:
        return finish()
    parts = tok.split(".")
    claims = js(base64.urlsafe_b64decode(parts[1] + "=" * (-len(parts[1]) % 4)))
    check("JWT has HS256 claims sub/role/ver/exp",
          all(k in claims for k in ("sub", "role", "ver", "exp", "iss", "aud")),
          "role=%s ttl=%ss" % (claims.get("role"), claims.get("exp", 0) - claims.get("iat", 0)))

    # ---- authenticated calls pass the auth layer
    st, body, _ = req("GET", "/api/auth/me", token=tok)
    check("GET /api/auth/me with token -> 200", st == 200 and js(body).get("user"),
          body.decode()[:120])
    st, body, _ = req("GET", "/api/models", token=tok)
    m = js(body)
    check("GET /api/models with token -> 200",
          st == 200 and all((m.get("models") or {}).get(k) for k in ("text", "image", "video")),
          "source=%s" % m.get("source"))
    st, body, _ = req("POST", "/api/text", {}, token=tok)
    check("POST /api/text {} with token -> 400 (passed auth, failed validation)", st == 400,
          body.decode()[:120])

    if not key:
        st, body, _ = req("POST", "/api/text", {"prompt": "hi"}, token=tok)
        check("POST /api/text with token, no key -> 503",
              st == 503 and js(body).get("error") == "AGNES_API_KEY not configured",
              body.decode()[:120])
        print("SKIP real generation (AGNES_API_KEY not configured)")
    else:
        t0 = time.time()
        st, body, _ = req("POST", "/api/text",
                          {"prompt": "用一句话回答：1+1等于几？", "model": "agnes-2.5-flash"},
                          token=tok)
        d = js(body)
        check("POST /api/text real generation", st == 200 and bool(d.get("text")),
              "%.1fs: %s" % (time.time() - t0, (d.get("text") or body.decode())[:120]))
        if os.environ.get("SMOKE_IMAGE") == "1":
            t0 = time.time()
            st, body, _ = req("POST", "/api/image", {"prompt": "a red apple on a white table"},
                              token=tok)
            d = js(body)
            check("POST /api/image real generation", st == 200 and bool(d.get("url") or d.get("b64")),
                  "%.1fs: %s" % (time.time() - t0, (d.get("url") or body.decode())[:120]))

    # ---- refresh rotation / revocation
    st, body, _ = req("POST", "/api/auth/refresh", {})
    check("refresh without cookie -> 401", st == 401)
    st, body, hdr = req("POST", "/api/auth/refresh", {}, cookie=c1)
    c2, _ = refresh_cookie(hdr)
    tok2 = js(body).get("access_token")
    check("refresh with cookie -> 200, new token + rotated cookie",
          st == 200 and bool(tok2) and bool(c2) and c2 != c1, "%d" % st)
    st, _, _ = req("POST", "/api/auth/refresh", {}, cookie=c1)
    check("old (rotated) refresh cookie -> 401", st == 401, "%d" % st)
    if tok2:
        st, _, _ = req("GET", "/api/auth/me", token=tok2)
        check("refreshed access token works", st == 200, "%d" % st)
    st, body, hdr = req("POST", "/api/auth/logout", {}, cookie=c2)
    _, clear = refresh_cookie(hdr)
    check("logout -> 200 + cookie cleared", st == 200 and "Max-Age=0" in clear,
          body.decode()[:120])
    st, _, _ = req("POST", "/api/auth/refresh", {}, cookie=c2)
    check("refresh after logout -> 401", st == 401, "%d" % st)
    return finish()


def _env_value(name):
    try:
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".env"),
                  encoding="utf-8") as fh:
            for line in fh:
                if line.strip().startswith(name + "="):
                    return line.split("=", 1)[1].strip().strip('"').strip("'")
    except OSError:
        pass
    return ""


def finish():
    print("\n%s (%d failure%s)" % ("OK" if not failures else "FAILED", failures,
                                   "" if failures == 1 else "s"))
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
