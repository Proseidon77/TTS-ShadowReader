#!/usr/bin/env bash
# agnes-service smoke test (curl). Safe to run against the live service: it never spends
# Agnes quota unless SMOKE_TEXT=1, and never burns generation quota (only a 400-validation call).
#
#   ./smoke.sh                                         public + 401 checks
#   SMOKE_USER=u SMOKE_PASSWORD='…' ./smoke.sh          + login → /me, /models, /limits, refresh, logout
#   SMOKE_TEXT=1 SMOKE_USER=… SMOKE_PASSWORD=… ./smoke.sh   + one real /api/text call (costs quota)
#   BASE=http://127.0.0.1:8793 (default)
# The password is passed via the environment and JSON-encoded by python3 (never on a command line).
set -uo pipefail
BASE=${BASE:-http://127.0.0.1:8793}
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
PASS=0; FAIL=0

ok()   { PASS=$((PASS+1)); printf '  ok    %s\n' "$*"; }
bad()  { FAIL=$((FAIL+1)); printf '  FAIL  %s\n' "$*"; }
# req METHOD PATH [curl args...] -> sets CODE, body in $TMP/body, headers in $TMP/hdr
req()  { local m=$1 p=$2; shift 2
         CODE=$(curl -s -o "$TMP/body" -D "$TMP/hdr" -w '%{http_code}' -X "$m" "$BASE$p" "$@") || CODE=000; }
expect() { local want=$1 what=$2
           if [ "$CODE" = "$want" ]; then ok "$what -> $CODE"; else bad "$what -> $CODE (want $want): $(head -c 300 "$TMP/body")"; fi; }
jget() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1]));
for k in sys.argv[2].split("."): d=d[k] if isinstance(d,dict) else d[int(k)]
print(d if not isinstance(d,(dict,list)) else json.dumps(d))' "$TMP/body" "$1" 2>/dev/null; }

echo "== public ($BASE)"
req GET /health;                     expect 200 "GET /health"
[ "$(jget status)" = ok ] && ok "health status=ok (key_configured=$(jget key_configured), jwt_secret=$(jget auth.jwt_secret), db.reachable=$(jget auth.db.reachable), limits_ready=$(jget auth.db.limits_ready))" || bad "health body: $(head -c 300 "$TMP/body")"
req GET /;                           expect 200 "GET / (frontend)"
grep -q '<html' "$TMP/body" && ok "/ is HTML ($(wc -c <"$TMP/body") bytes)" || bad "/ not HTML"
grep -qi '^content-security-policy:' "$TMP/hdr" && ok "/ has CSP header" || bad "/ missing CSP"
req GET /nope;                       expect 404 "GET /nope"

echo "== unauthenticated -> 401"
J=(-H 'Content-Type: application/json')
req POST /api/text  "${J[@]}" -d '{"prompt":"hi"}';  expect 401 "POST /api/text (no token)"
grep -qi '^www-authenticate: bearer' "$TMP/hdr" && ok "WWW-Authenticate: Bearer present" || bad "no WWW-Authenticate"
req POST /api/image "${J[@]}" -d '{"prompt":"hi"}';  expect 401 "POST /api/image (no token)"
req POST /api/video "${J[@]}" -d '{"prompt":"hi"}';  expect 401 "POST /api/video (no token)"
req GET  /api/models;                                 expect 401 "GET /api/models (no token)"
req GET  /api/limits;                                 expect 401 "GET /api/limits (no token)"
req GET  /api/auth/me;                                expect 401 "GET /api/auth/me (no token)"
req GET  "/api/video/status/video_x?model=agnes-video-2.5-flash"; expect 401 "GET /api/video/status (no token)"
req GET  /api/auth/me -H 'Authorization: Bearer garbage.token.here'; expect 401 "garbage token"
FORGED='eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiIxIiwidmVyIjowLCJ0eXAiOiJhY2Nlc3MiLCJpc3MiOiJhZ25lcy1zZXJ2aWNlIiwiYXVkIjoiYWduZXMtc2VydmljZSIsImV4cCI6NDEwMjQ0NDgwMH0.'
req GET  /api/auth/me -H "Authorization: Bearer $FORGED";     expect 401 "alg=none forged token"
req POST /api/auth/login -d 'username=a&password=b';           expect 415 "login without JSON content-type"
req POST /api/auth/refresh "${J[@]}" -d '{}';                  expect 401 "refresh without cookie"

if [ -n "${SMOKE_USER:-}" ] && [ -n "${SMOKE_PASSWORD:-}" ]; then
  echo "== authenticated flow as $SMOKE_USER"
  python3 -c 'import json,os; print(json.dumps({"username":os.environ["SMOKE_USER"],"password":os.environ["SMOKE_PASSWORD"]}))' > "$TMP/login.json"
  req POST /api/auth/login "${J[@]}" --data-binary @"$TMP/login.json" -c "$TMP/jar"
  expect 200 "login"
  TOKEN=$(jget access_token)
  grep -qi '^set-cookie: agnes_rt=.*HttpOnly' "$TMP/hdr" && ok "refresh cookie is HttpOnly" || bad "no HttpOnly refresh cookie"
  A=(-H "Authorization: Bearer $TOKEN")
  req GET /api/auth/me "${A[@]}";       expect 200 "GET /api/auth/me"
  echo "        me: $(head -c 400 "$TMP/body")"
  req GET /api/models "${A[@]}";        expect 200 "GET /api/models (source=$(jget source))"
  req GET /api/limits "${A[@]}";        expect 200 "GET /api/limits"
  echo "        limits: $(jget limits) usage.remaining: $(jget usage.remaining)"
  req POST /api/text "${A[@]}" "${J[@]}" -d '{}'; expect 400 "POST /api/text {} (auth passed, validation 400, not counted)"
  if [ "${SMOKE_TEXT:-0}" = 1 ]; then
    req POST /api/text "${A[@]}" "${J[@]}" -d '{"prompt":"用一句话打个招呼"}'
    echo "        /api/text -> $CODE $(head -c 300 "$TMP/body")"
  fi
  cp "$TMP/jar" "$TMP/jar.old"
  req POST /api/auth/refresh "${J[@]}" -d '{}' -b "$TMP/jar" -c "$TMP/jar"; expect 200 "refresh (rotates cookie)"
  NEWTOKEN=$(jget access_token)
  req GET /api/auth/me -H "Authorization: Bearer $NEWTOKEN"; expect 200 "new access token works"
  req POST /api/auth/refresh "${J[@]}" -d '{}' -b "$TMP/jar.old"; expect 401 "old (rotated) refresh cookie rejected"
  req POST /api/auth/logout "${J[@]}" -d '{}' -b "$TMP/jar";        expect 200 "logout ($(head -c 100 "$TMP/body"))"
  req POST /api/auth/refresh "${J[@]}" -d '{}' -b "$TMP/jar";       expect 401 "refresh after logout"
else
  echo "== (set SMOKE_USER / SMOKE_PASSWORD for the login round-trip)"
fi

echo "== $PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
