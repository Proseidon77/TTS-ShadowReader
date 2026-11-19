#!/usr/bin/env python3
"""TASK6 phase 2 acceptance (Mock): dev instance 127.0.0.1:8795 on the isolated DB agnes_p2dev + MockAgnes (LLMs + fake TtsWeb).

  python3 tests/p2_stack.py start mock8795.env ingest_mock.env ingest_tune.env
  python3 tests/p2_stack.py accept2            (= python3 tests/p2_accept.py)

Creates throw-away accounts p2t_* in agnes_p2dev (CLI, passwords on stdin, never printed) and deletes them at the end.
tests/ingest_tune.env is rewritten by some cases (the instance re-reads env files on change, no restart needed).
"""
import json
import os
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = "/data/workspace/agnes-service"
DOTNET = "/data/dotnet-sdk/dotnet"
DLL = ROOT + "/bin/Release/net10.0/AgnesService.dll"
BASE = "http://127.0.0.1:8795"
MOCK = "http://127.0.0.1:18793"
DB = os.environ.get("AGNES_TEST_DB", "agnes_p2dev")
assert DB != "db77database", "phase-2 tests must not run on the production database"
ENV = ["--env-file", ROOT + "/.env", "--env-file", ROOT + "/tests/devdb8795.env", "--env-file", ROOT + "/tests/ingest_mock.env"]
TUNE = ROOT + "/tests/ingest_tune.env"
PUBLIC = "https://8791-p2test.example.invalid"   # TTSWEB_PUBLIC_URL used by case C (only a string, never fetched)
SUF = secrets.token_hex(2)
PASS = FAIL = 0
ONLY = sys.argv[1:]


def check(name, ok, info=""):
    global PASS, FAIL
    PASS, FAIL = (PASS + 1, FAIL) if ok else (PASS, FAIL + 1)
    print(("  ok    " if ok else "  FAIL  ") + name + (("  -> " + str(info)[:600]) if info and not ok else ""), flush=True)


def http(method, url, body=None, token=None, headers=None, timeout=60):
    data = None if body is None else json.dumps(body, ensure_ascii=False).encode()
    req = urllib.request.Request(url, data=data, method=method)
    if body is not None:
        req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            code, raw = r.status, r.read()
    except urllib.error.HTTPError as e:
        code, raw = e.code, e.read()
    try:
        js = json.loads(raw) if raw else None
    except ValueError:
        js = {"_raw": raw[:300].decode("utf-8", "replace")}
    return code, js


def call(method, path, body=None, token=None, headers=None):
    return http(method, BASE + path, body, token, headers)


def mock(method, path, body=None):
    return http(method, MOCK + path, body, headers={"Authorization": "Bearer mock-ttsweb-token-not-real"})


def psql(sql, *params):
    args = ["psql", "-d", DB, "-v", "ON_ERROR_STOP=1", "-Atq"]
    for i, p in enumerate(params, 1):
        args += ["-v", f"v{i}={p}"]
    return subprocess.run(args, input=sql, capture_output=True, text=True, check=True).stdout.strip()


def cli(*args, stdin=None):
    return subprocess.run([DOTNET, DLL, *ENV, *args], input=stdin, capture_output=True, text=True, cwd=ROOT)


USERS = []


def mkuser(name, role="user", extra=()):
    pw = secrets.token_urlsafe(18)
    out = cli("--create-user", name, role, *extra, "--password-stdin", stdin=pw + "\n")
    if out.returncode != 0:
        raise SystemExit(f"create-user {name} failed: {out.stderr[-300:]}")
    USERS.append(name)
    code, js = call("POST", "/api/auth/login", {"username": name, "password": pw})
    assert code == 200, (name, code, js)
    return js["access_token"], js["user"]["id"]


def cleanup():
    for n in USERS:
        psql("DELETE FROM agnes_auth.users WHERE username = :'v1';", n)
        psql("DELETE FROM agnes_auth.login_audit WHERE username = :'v1';", n)
    left = psql("SELECT count(*) FROM agnes_auth.users WHERE username LIKE 'p2t\\_%';")
    jobs = psql("SELECT count(*) FROM agnes_auth.ingest_jobs WHERE username LIKE 'p2t\\_%';")
    return left == "0" and jobs == "0"


def tune(**kv):
    with open(TUNE, "w") as fh:
        fh.write("# written by tests/p2_accept.py (hot-reloaded by the dev instance)\n")
        for k, v in kv.items():
            fh.write(f"{k}={v}\n")
    time.sleep(1.2)   # mtime resolution + next Settings.Get


TERMINAL = {"done", "l2", "failed", "cancelled"}


def wait_job(token, jid, statuses=TERMINAL, secs=60):
    t0 = time.time()
    js = None
    while time.time() - t0 < secs:
        c, js = call("GET", f"/api/reading/jobs/{jid}", token=token)
        if c == 200 and js["job"]["status"] in statuses:
            return js["job"]
        time.sleep(0.4)
    return js["job"] if js and "job" in js else js


def art(sid):
    _, st = mock("GET", "/mock/ttsweb/state")
    return next((a for a in st["articles"] if a["id"] == sid), None)


def usage(token):
    return call("GET", "/api/limits", token=token)[1]["usage"]["used"]


def calls(admin, uid):
    return call("GET", f"/api/admin/calls?kind=ingest&user_id={uid}&limit=50", token=admin)[1]["calls"]


def submit(token, body, headers=None):
    return call("POST", "/api/reading/ingest", body, token, headers)


def want(case):
    return not ONLY or case in ONLY


def restart_instance():
    subprocess.run(["python3", "tests/p2_stack.py", "restart", "mock8795.env", "ingest_mock.env", "ingest_tune.env"], cwd=ROOT,
                   capture_output=True, text=True)


JA = "今日は18年ぶりに東京へ行った。駅は人でいっぱいだった。"


def main():
    tune()
    mock("POST", "/mock/ttsweb/reset")
    admin, admin_id = mkuser(f"p2t_admin_{SUF}", "admin", ["--rate-per-minute", "500", "--daily-quota", "500"])
    agent, agent_id = mkuser(f"p2t_agent_{SUF}", "user", ["--reading-ingest", "agent", "--muse-id", "A9" + str(int(SUF, 16) % 90 + 10),
                                                            "--rate-per-minute", "500", "--daily-quota", "500"])
    muse = psql("SELECT muse_id FROM agnes_auth.users WHERE username = :'v1';", f"p2t_agent_{SUF}")
    plain, plain_id = mkuser(f"p2t_plain_{SUF}", "user")
    unmapped, _ = mkuser(f"p2t_unmap_{SUF}", "user", ["--reading-ingest", "agent"])
    # web channel = the user's own 8793 account with reading_ingest=web, JWT from the same-origin「添加文章」tab
    webu, webu_id = mkuser(f"p2t_web_{SUF}", "user", ["--reading-ingest", "web", "--rate-per-minute", "500", "--daily-quota", "500"])

    # ------------------------------------------------------------------ A. schema / auth / permissions
    if want("A"):
        print("== A. migration, permissions (before admission), admin + CLI")
        cols = psql("SELECT string_agg(column_name, ',' ORDER BY column_name) FROM information_schema.columns WHERE table_schema='agnes_auth' AND table_name='users' AND column_name IN ('reading_ingest','muse_id');")
        check("users.reading_ingest + muse_id exist", cols == "muse_id,reading_ingest", cols)
        idx = psql("SELECT indexdef FROM pg_indexes WHERE schemaname='agnes_auth' AND indexname='ingest_jobs_share_active_uq';")
        check("partial unique index on share_id for active jobs", "WHERE (status = ANY" in idx and "share_id" in idx, idx)
        kinds = psql("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname IN ('call_log_kind_check','usage_daily_kind_check') ORDER BY conname;")
        check("kind CHECKs contain 'ingest' (both)", kinds.count("'ingest'") == 2, kinds)
        c, _ = call("POST", "/api/reading/ingest", {"text": JA})
        check("anonymous -> 401", c == 401, c)
        c, js = submit(plain, {"text": JA})
        check("reading_ingest=none -> 403 ingest_forbidden", c == 403 and js["code"] == "ingest_forbidden", js)
        c, js = submit(unmapped, {"text": JA})
        check("agent without muse_id -> 403 ingest_agent_unmapped", c == 403 and js["code"] == "ingest_agent_unmapped", js)
        check("401/403 cost no quota", usage(plain)["ingest"] == 0)
        c, _ = submit(None, {"share_id": "0123456789"}, {"X-Ingest-Key": "mock-ingest-web-key-0123456789abcdef-not-real"})
        check("X-Ingest-Key bypass removed: no JWT -> 401", c == 401, c)
        c, js = submit(webu, {"title": "网页", "text": JA, "share_id": "0123456789"})
        check("web account + share_id -> 400 unknown_field", c == 400 and js["code"] == "unknown_field", js)
        c, js = submit(webu, {"title": "网页", "text": JA, "rate": "-10%"})
        check("web account + rate -> 400 unknown_field", c == 400 and js["code"] == "unknown_field", js)
        c, js = submit(webu, {"text": JA})
        check("web account without title -> 400 title_required", c == 400 and js["code"] == "title_required", js)
        c, js = submit(webu, {"title": "   ", "text": JA})
        check("web account blank title -> 400 title_required", c == 400 and js["code"] == "title_required", js)
        c, js = submit(webu, {"title": "网页"})
        check("web account without text -> 400 invalid_text", c == 400 and js["code"] == "invalid_text", js)
        check("web 400s cost no quota", usage(webu)["ingest"] == 0, usage(webu))
        c, js = submit(agent, {"text": JA, "share_id": "0123456789"})
        check("agent: text AND share_id -> 400", c == 400, js)
        c, js = submit(agent, {"text": "あ" * 5001})
        check("text > 5000 -> 400", c == 400, js)
        out = cli("--create-user", "A007", "--password-stdin", stdin="Zz9-" + secrets.token_urlsafe(12) + "\n")
        check("CLI refuses A00x usernames", out.returncode != 0 and "reserved" in out.stderr, out.stderr[-200:])
        c, js = call("POST", "/api/admin/users", {"username": "A123", "password": "Zz9-" + secrets.token_urlsafe(12)}, admin)
        check("admin API refuses A00x usernames (400 reserved_username)", c == 400 and js["code"] == "reserved_username", js)
        c, js = call("PATCH", f"/api/admin/users/{plain_id}", {"reading_ingest": "bogus"}, admin)
        check("PATCH reading_ingest invalid -> 400", c == 400 and js["code"] == "invalid_reading_ingest", js)
        c, js = call("PATCH", f"/api/admin/users/{plain_id}", {"reading_ingest": "agent", "muse_id": muse}, admin)
        check("PATCH muse_id already mapped -> 409 muse_id_taken", c == 409 and js["code"] == "muse_id_taken", js)
        c, js = call("PATCH", f"/api/admin/users/{plain_id}", {"reading_ingest": "web"}, admin)
        check("PATCH reading_ingest none->web ok", c == 200 and js["user"]["reading_ingest"] == "web", js)
        c, js = call("GET", "/api/admin/audit?limit=20", token=admin)
        row = next((a for a in js["audit"] if a["target_username"] == f"p2t_plain_{SUF}" and a["action"] == "update_user"), None)
        check("admin audit records reading_ingest from->to", row and row["detail"]["changes"]["reading_ingest"] == {"from": "none", "to": "web"}, row)
        out = cli("--set-reading-ingest", f"p2t_plain_{SUF}", "none")
        check("CLI --set-reading-ingest works + audited", out.returncode == 0 and "web -> none" in out.stdout, out.stdout + out.stderr)
        aud = psql("SELECT actor_username || '|' || (detail->'changes'->'reading_ingest')::text FROM agnes_auth.admin_audit WHERE target_username = :'v1' ORDER BY id DESC LIMIT 1;", f"p2t_plain_{SUF}")
        check("CLI audit row (actor cli:<os user>)", aud.startswith("cli:") and '"to": "none"' in aud, aud)
        c, js = call("GET", "/api/admin/users", token=admin)
        me = next(u for u in js["users"] if u["username"] == f"p2t_agent_{SUF}")
        check("admin user list shows reading_ingest + muse_id", me["reading_ingest"] == "agent" and me["muse_id"] == muse, me)
        c, js = call("GET", "/api/admin/calls?kind=ingest", token=admin)
        check("admin calls filter kind=ingest accepted", c == 200, js)
        with urllib.request.urlopen(BASE + "/", timeout=10) as r:
            html = r.read().decode()
        check("admin page: kind filter has ingest + jobs/L2 panes", '<option value="ingest">' in html and 'id="jobs-tbody"' in html and 'id="l2-tbody"' in html)
        c, h = call("GET", "/health")
        check("/health shows ingest + circuit + upstream_rpm", "ingest" in h and "circuit" in h["analyze"] and "upstream_rpm" in h["analyze"], h.get("ingest"))
        check("/health ingest.public_url_configured (bool), no web_trigger", h["ingest"].get("public_url_configured") is False
              and "web_trigger" not in h["ingest"], h.get("ingest"))
        with urllib.request.urlopen(BASE + "/add", timeout=10) as r:
            add_html, add_csp = r.read().decode(), r.headers.get("Content-Security-Policy")
        with urllib.request.urlopen(BASE + "/", timeout=10) as r:
            root_csp = r.headers.get("Content-Security-Policy")
        check("GET /add = same index.html + same CSP, has the 添加文章 tab", add_html == html and add_csp == root_csp
              and 'id="tab-add"' in add_html and 'data-tab="add"' in add_html)
        check("page source carries no TtsWeb token / ingest key", "TTSWEB_TOKEN" not in add_html and "X-Ingest-Key" not in add_html
              and "mock-ttsweb-token" not in add_html)
        req = urllib.request.Request(BASE + "/api/reading/ingest", method="OPTIONS",
                                     headers={"Origin": "https://evil.example", "Access-Control-Request-Method": "POST"})
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                hdrs = dict(r.headers)
        except urllib.error.HTTPError as e:
            hdrs = dict(e.headers)
        check("no CORS: preflight / cross-origin answers carry no Access-Control-Allow-*",
              not any(k.lower().startswith("access-control-allow") for k in hdrs), hdrs)
        c, me = call("GET", "/api/auth/me", token=webu)
        check("/api/auth/me reports reading_ingest (tab visibility)", me["user"]["reading_ingest"] == "web", me)

    # ------------------------------------------------------------------ B. agent channel happy path (L0)
    if want("B"):
        print("== B. agent text -> share-first -> preview -> enrich (gemini) -> fixes gate -> replace commit -> ready")
        before = usage(agent)["ingest"]
        t0 = time.time()
        c, js = submit(agent, {"text": "scenario:json_ok " + JA, "title": "B 测试"})
        ms = int((time.time() - t0) * 1000)
        check("202 + job_id + share_id + /v3 url (share-first, sync)", c == 202 and js["job_id"] and js["share_id"] and js["url"] == "/v3?s=" + js["share_id"], js)
        check(f"POST answered quickly ({ms} ms < 5000)", ms < 5000)
        job = wait_job(agent, js["job_id"])
        a = art(js["share_id"])
        check("job done, article ready", job["status"] == "done" and job["stage"] == "done" and job["result"]["article_status"] == "ready", job)
        check("added_by = muse id, added_via = agent", a["added_by"] == muse and a["added_via"] == "agent", a)
        v = a["versions"][-1]
        check("replace commit: executor agnes-service, tr_via gemini-api, enrich-v2, title/rate carried", v["executor"] == "agnes-service"
              and v["tr_via"] == "gemini-api" and v["prompt_version"] == "enrich-v2" and v["title"] == "B 测试" and v["rate"] == "-20%", v)
        check("per-sentence tr_via recorded", v["per_sentence_via"] and all(x == "gemini-api" for x in v["per_sentence_via"]), v)
        check("fill-in reading for 18 applied (readings)", any(r["s"] == "18" and r["r"] == "じゅうはち" for r in v["readings"]), v["readings"])
        check("job provider_model gemini:gemini-flash-lite-latest", job["provider_model"] == "gemini:gemini-flash-lite-latest", job["provider_model"])
        check("fx counts recorded in job", job["result"]["fx"].startswith("a"), job["result"])
        check("one admission (ingest +1)", usage(agent)["ingest"] == before + 1)
        rows = [r for r in calls(admin, agent_id) if js["share_id"] in (r["detail"] or "")]
        check("one call_log row, updated at the end (done:ready, model, fx=)", len(rows) == 1 and rows[0]["outcome"] == "done:ready"
              and rows[0]["model"] == "gemini:gemini-flash-lite-latest" and "fx=a" in rows[0]["detail"], rows)
        # already shared by the agent → share_id only; replace does not change added_by
        _, sh = mock("POST", "/share", {"text": "scenario:json_ok 明日は雨だ。", "by": "A003", "added_via": "agent"})
        c, js2 = submit(agent, {"share_id": sh["id"]})
        job2 = wait_job(agent, js2["job_id"])
        check("agent share_id-only job -> done, added_by untouched (A003)", job2["status"] == "done" and art(sh["id"])["added_by"] == "A003", job2)
        c, js3 = submit(agent, {"share_id": sh["id"]})
        job3 = wait_job(agent, js3["job_id"])
        check("same article already ready -> done already_ready (no enrich)", job3["status"] == "done" and job3["result"]["outcome"] == "already_ready", job3)
        c, js = call("GET", "/api/reading/jobs?limit=5", token=agent)
        check("job list (own)", c == 200 and all(j["username"] == f"p2t_agent_{SUF}" for j in js["jobs"]) and len(js["jobs"]) >= 3)
        c, _ = call("GET", f"/api/reading/jobs/{job2['id']}", token=plain)
        check("other user's job -> 404", c == 404, c)
        c, js = call("GET", "/api/reading/jobs?limit=500", token=admin)
        check("admin job list sees agent jobs", any(j["username"] == f"p2t_agent_{SUF}" for j in js["jobs"]))

    # ------------------------------------------------------------------ C. web channel
    if want("C"):
        print("== C. web channel (own account JWT, reading_ingest=web; title + text; agnes-service shares with its token)")
        tune(TTSWEB_PUBLIC_URL=PUBLIC + "/")
        before = usage(webu)["ingest"]
        t0 = time.time()
        c, js = submit(webu, {"title": "网页", "text": "scenario:json_ok 犬が3匹いる。", "lang": "ja"})
        ms = int((time.time() - t0) * 1000)
        check("web JWT submit -> 202 + share_id + absolute 8791 url (TTSWEB_PUBLIC_URL, trailing / trimmed)",
              c == 202 and js["share_id"] and js["url"] == PUBLIC + "/v3?s=" + js["share_id"] and js["job"]["url"] == js["url"], js)
        check(f"web save answered quickly ({ms} ms < 3000)", ms < 3000)
        job = wait_job(webu, js["job_id"])
        a = art(js["share_id"])
        check("web job done/ready; channel web, username = the account", job["status"] == "done" and job["result"]["article_status"] == "ready"
              and job["channel"] == "web" and job["username"] == f"p2t_web_{SUF}", job)
        check("reading article added_by = web, added_via = web (channel only)", a["added_by"] == "web" and a["added_via"] == "web", a)
        check("one admission on the web account", usage(webu)["ingest"] == before + 1, usage(webu))
        rows = [r for r in calls(admin, webu_id) if js["share_id"] in (r["detail"] or "")]
        check("call_log kind=ingest, detail ch=web;by=web, user = the account", len(rows) == 1 and "ch=web;by=web" in rows[0]["detail"]
              and rows[0]["username"] == f"p2t_web_{SUF}", rows)
        c, lj = call("GET", "/api/reading/jobs?mine=1&channel=web&limit=10", token=webu)
        check("「最近保存」list (mine=1&channel=web) shows it with the absolute url", c == 200 and lj["jobs"] and lj["jobs"][0]["id"] == js["job_id"]
              and lj["jobs"][0]["url"].startswith(PUBLIC), lj)
        # same text while its job is still active → 200 duplicate (share_id lock), no second admission
        tune(TTSWEB_PUBLIC_URL=PUBLIC, INGEST_TIMEOUT_GEMINI_LITE_S=20)
        body = {"title": "网页慢", "text": "scenario:gsleep:3 網の遅い記事です。"}
        before = usage(webu)["ingest"]
        c1, js1 = submit(webu, body)
        c2, js2 = submit(webu, body)
        check("web: same text re-submitted while active -> 200 duplicate (same job)", c1 == 202 and c2 == 200 and js2["duplicate"]
              and js2["job"]["id"] == js1["job_id"], (c1, js1, c2, js2))
        check("duplicate refunded (one admission)", usage(webu)["ingest"] == before + 1, usage(webu))
        wait_job(webu, js1["job_id"])
        # TtsWeb down at save time → 202 + share_id null (text kept in the job), the worker shares it later (T state)
        tune(TTSWEB_PUBLIC_URL=PUBLIC)
        mock("POST", "/mock/ttsweb/mode", {"mode": "503", "count": 2})
        c, js = submit(webu, {"title": "停電中の保存", "text": "scenario:json_ok 停電中に保存した記事。"})
        check("TtsWeb down: web save still 202, share_id = null, url = null", c == 202 and js["share_id"] is None and js["url"] is None, js)
        job = wait_job(webu, js["job_id"], secs=40)
        a = art(job["share_id"]) if job.get("share_id") else None
        check("worker shares it later (T back-off) -> done/ready, added_via web", job["status"] == "done" and a and a["added_via"] == "web"
              and job["url"] == PUBLIC + "/v3?s=" + job["share_id"], (job, a))
        mock("POST", "/mock/ttsweb/mode", {"mode": "normal"})
        tune()
        # agent channel unchanged: share_id-only still accepted, relative url without TTSWEB_PUBLIC_URL
        _, sh = mock("POST", "/share", {"text": "scenario:json_ok 代理の記事。", "by": "A001", "added_via": "agent"})
        c, js = submit(agent, {"share_id": sh["id"], "rate": "-10%"})
        job = wait_job(agent, js["job_id"])
        check("agent behaviour unchanged (share_id + rate accepted, relative url, added_by kept)", c == 202 and js["url"] == "/v3?s=" + sh["id"]
              and job["status"] == "done" and art(sh["id"])["added_by"] == "A001", (js, job))
        mock("POST", "/mock/ttsweb/mode", {"mode": "dup", "count": 1})
        c, js = submit(agent, {"text": "scenario:json_ok 既に完成した記事です。"})
        job = wait_job(agent, js["job_id"])
        check("dup: /share hits an already-ready article -> done already_ready", job["status"] == "done" and job["result"]["outcome"] == "already_ready", job)

    # ------------------------------------------------------------------ D. non-ja (D8)
    if want("D"):
        print("== D. non-Japanese: share only, no enrich")
        c, js = submit(agent, {"text": "我们今天去图书馆看书。", "lang": "zh"})
        job = wait_job(agent, js["job_id"])
        check("lang=zh -> done non_ja, article stays partial", job["result"]["outcome"] == "non_ja" and art(js["share_id"])["status"] == "partial", job)
        c, js = submit(agent, {"text": "我们明天去公园散步，天气很好。", "lang": "ja"})
        job = wait_job(agent, js["job_id"])
        check("Chinese text sent as ja -> kana-ratio fallback non_ja", job["result"]["outcome"] == "non_ja" and job["result"]["kana_ratio"] == 0, job)
        check("no LLM call for non-ja (no chunks)", job["chunks"] is None)

    # ------------------------------------------------------------------ E. fixes gate (§2.3)
    if want("E"):
        print("== E. fixes gate: fill-in applied, counter rewrite applied, dictionary check, others not applied")
        c, js = submit(agent, {"text": "scenario:rewrite 一本の鉛筆と東京の地図。"})
        job = wait_job(agent, js["job_id"])
        res = job["result"]
        app = {(x["s"], x["kind"]) for x in res["fixes"]["applied"]}
        check("一 (pos=num) fill, 本 counter, 東京 dict-supported -> applied", ("一", "fill") in app and ("本", "counter") in app and ("東京", "dict") in app, res["fixes"])
        rej = {x["s"] for x in res["fixes"]["rejected"]}
        check("鉛筆 / 地図 rewrites rejected by dictionary (not applied)", {"鉛筆", "地図"} <= rej, res["fixes"])
        v = art(js["share_id"])["versions"][-1]
        check("rejected rewrites not in commit readings", not any(r["s"] in ("鉛筆", "地図") for r in v["readings"]) and any(r["s"] == "本" and r["r"] == "ぽん" for r in v["readings"]), v["readings"])
        check("fx=a3/r2/s0", res["fx"] == "a3/r2/s0", res["fx"])
        mock("POST", "/mock/ttsweb/mode", {"mode": "rc503"})
        c, js = submit(agent, {"text": "scenario:rewrite 鉛筆と地図。"})
        job = wait_job(agent, js["job_id"])
        mock("POST", "/mock/ttsweb/mode", {"mode": "normal"})
        check("dictionary unavailable -> rewrites suspect, layer 1 only (fx=a0/r0/s2)", job["status"] == "done" and job["result"]["fx"] == "a0/r0/s2", job["result"])

    # ------------------------------------------------------------------ F. TtsWeb failure classes
    if want("F"):
        print("== F. VERSION_CONFLICT convergence, T state, auth, gone")
        mock("POST", "/mock/ttsweb/mode", {"mode": "409", "count": 1})
        c, js = submit(agent, {"text": "scenario:json_ok 競合の記事。"})
        job = wait_job(agent, js["job_id"])
        check("409 VERSION_CONFLICT -> done (converged, not failure)", job["status"] == "done" and job["result"]["outcome"] == "version_conflict_converged", job)
        _, sh = mock("POST", "/share", {"text": "scenario:json_ok 停電の記事。", "by": "A001"})
        mock("POST", "/mock/ttsweb/mode", {"mode": "503", "count": 3})
        c, js = submit(agent, {"share_id": sh["id"]})
        j1 = wait_job(agent, js["job_id"], {"retry"}, 10)
        check("TtsWeb 503 -> job retry (T state, ttsweb_unavailable)", j1["status"] == "retry" and j1["error_code"] == "ttsweb_unavailable" and j1["t_attempts"] >= 1, j1)
        job = wait_job(agent, js["job_id"], secs=40)
        check("TtsWeb back -> job completes (ready)", job["status"] == "done" and job["result"]["article_status"] == "ready", job)
        tune(INGEST_T_MAX_S=10)
        mock("POST", "/mock/ttsweb/mode", {"mode": "503"})
        c, js = submit(agent, {"text": "scenario:json_ok 長い停電の記事。"})
        check("POST during T state still 202 (share pending, text kept)", c == 202 and js["share_id"] is None, js)
        job = wait_job(agent, js["job_id"], secs=40)
        mock("POST", "/mock/ttsweb/mode", {"mode": "normal"})
        tune()
        check("T state beyond INGEST_T_MAX_S -> failed failed_ttsweb", job["status"] == "failed" and job["error_code"] == "failed_ttsweb", job)
        _, sh = mock("POST", "/share", {"text": "scenario:json_ok 鍵の記事。"})
        mock("POST", "/mock/ttsweb/mode", {"mode": "401"})
        c, js = submit(agent, {"share_id": sh["id"]})
        job = wait_job(agent, js["job_id"])
        mock("POST", "/mock/ttsweb/mode", {"mode": "normal"})
        check("TtsWeb AUTH_FAILED -> L2 api-down (ttsweb_auth_failed)", job["status"] == "l2" and job["l2_reason"] == "api-down" and job["error_code"] == "ttsweb_auth_failed", job)
        _, sh = mock("POST", "/share", {"text": "scenario:json_ok 削除の記事。"})
        mock("POST", f"/mock/ttsweb/delete/{sh['id']}")
        c, js = submit(agent, {"share_id": sh["id"]})
        job = wait_job(agent, js["job_id"])
        check("article deleted -> cancelled article_gone", job["status"] == "cancelled" and job["error_code"] == "article_gone", job)

    # ------------------------------------------------------------------ G. dedup lock on share_id
    if want("G"):
        print("== G. same article only once (share_id lock)")
        tune(INGEST_TIMEOUT_GEMINI_LITE_S=20)
        _, sh = mock("POST", "/share", {"text": "scenario:gsleep:3 遅い記事です。"})
        before = usage(agent)["ingest"]
        c1, js1 = submit(agent, {"share_id": sh["id"]})
        c2, js2 = submit(agent, {"share_id": sh["id"]})
        check("second submit while active -> 200 duplicate, same job", c1 == 202 and c2 == 200 and js2["duplicate"] and js2["job"]["id"] == js1["job_id"], (c1, c2, js2))
        check("duplicate costs no admission", usage(agent)["ingest"] == before + 1)
        dup = psql("SELECT count(*) FROM agnes_auth.ingest_jobs WHERE share_id = :'v1' AND status IN ('queued','running','retry');", sh["id"])
        check("at most one active job per share_id (DB)", dup in ("0", "1"), dup)
        job = wait_job(agent, js1["job_id"])
        tune()
        check("slow job finishes", job["status"] == "done", job)

    # ------------------------------------------------------------------ H. L1 ladder → L2, partial, web retries
    if want("H"):
        print("== H. L1 -> L2 (agent), partial commit, web delayed retries (whole-article redo)")
        restart_instance()   # fresh circuits
        c, js = submit(agent, {"text": "scenario:allquota 枠切れの記事。"})
        job = wait_job(agent, js["job_id"])
        check("all providers quota -> agent channel L2 quota", job["status"] == "l2" and job["stage"] == "l2-pending" and job["l2_reason"] == "quota", job)
        restart_instance()
        c, js = submit(agent, {"text": "scenario:bad_always 品質の悪い記事。"})
        job = wait_job(agent, js["job_id"])
        check("validation never passes -> L2 quality", job["status"] == "l2" and job["l2_reason"] == "quality", job)
        tune(INGEST_CHUNK_SENTS_GEMINI=2, INGEST_CHUNK_SENTS_AGNES=2)
        text = "一つ目の文。二つ目の文。scenario:bad_always 三つ目の文。四つ目の文。"
        c, js = submit(agent, {"text": text})
        job = wait_job(agent, js["job_id"])
        a = art(js["share_id"])
        check("one chunk fails -> partial committed (allow_partial), then L2", job["status"] == "l2" and a["status"] == "partial" and a["version"] == 2
              and job["result"]["translated"] == 2 and job["result"]["article_status"] == "partial", (job, a))
        check("L2 job base_version = the partial version (agy builds on it)", job["base_version"] == 2, job["base_version"])
        tune()
        restart_instance()
        before = usage_of(admin, webu_id)
        c, js = submit(webu, {"title": "网页重试", "text": "scenario:bad_always ネット経由の記事。"})
        sh = {"id": js["share_id"]}
        j1 = wait_job(admin, js["job_id"], {"retry"}, 20)
        check("web channel: L1 fail -> delayed retry scheduled (l1_incomplete)", j1["status"] == "retry" and j1["error_code"] == "l1_incomplete", j1)
        job = wait_job(admin, js["job_id"], secs=60)
        check("web: 3 delayed retries then L2 quality", job["status"] == "l2" and job["attempt"] == 3 and job["l2_reason"] == "quality", job)
        check("system retries are not charged (one admission)", usage_of(admin, webu_id) == before + 1, (before, usage_of(admin, webu_id)))
        rows = [r for r in calls(admin, webu_id) if sh["id"] in (r["detail"] or "")]
        check("still one call_log row (retry=3 in detail)", len(rows) == 1 and rows[0]["outcome"] == "l2:quality" and "retry=3" in rows[0]["detail"], rows)

    # ------------------------------------------------------------------ I. L2 list + reconciliation
    if want("I"):
        print("== I. L2 to-do list + automatic reconciliation")
        restart_instance()
        for t in ("scenario:allquota L2の記事一。", "scenario:allquota L2の記事二。"):
            c, js = submit(agent, {"text": t})
            wait_job(agent, js["job_id"])
        restart_instance()
        c, js = call("GET", "/api/reading/l2", token=plain)
        check("L2 list forbidden for plain users", c == 403, c)
        c, js = call("GET", "/api/reading/l2", token=agent)
        pend = js["pending"]
        check("L2 list for agent account: pending items with reason + agy-gemini", c == 200 and len(pend) >= 1 and all(p["suggested_executor"] == "agy-gemini" for p in pend), js)
        target = pend[0]
        c, rj = call("POST", f"/api/reading/l2/{target['job_id']}/report", {"note": "agy started"}, agent)
        check("optional report back-fill stores a note", c == 200 and rj["job"]["result"]["l2_reports"][0]["note"] == "agy started", rj)
        mock("POST", f"/mock/ttsweb/complete/{target['share_id']}")
        c, js = call("GET", "/api/reading/l2", token=agent)
        check("after agy commit: reconciled on list pull", js["reconciled_now"] >= 1 and all(p["job_id"] != target["job_id"] for p in js["pending"]), js["reconciled_now"])
        c, j = call("GET", f"/api/reading/jobs/{target['job_id']}", token=admin)
        check("closed job: done + l2_closed_at + new version / tr_via agy-gemini", j["job"]["status"] == "done" and j["job"]["l2_closed_at"]
              and j["job"]["result"]["l2"]["tr_via"] == "agy-gemini", j["job"])
        # background reconciliation (scanner, INGEST_RECONCILE_S=10)
        c, js = call("GET", "/api/reading/l2", token=agent)
        if js["pending"]:
            t2 = js["pending"][0]
            mock("POST", f"/mock/ttsweb/complete/{t2['share_id']}")
            j = wait_job(admin, t2["job_id"], {"done"}, 25)
            check("scanner reconciles without a list pull", j["status"] == "done" and j["result"]["l2"]["closed_by"] == "reconcile", j)

    # ------------------------------------------------------------------ J. circuits, provider switch re-chunk
    if want("J"):
        print("== J. circuit breakers + re-chunk on provider switch")
        restart_instance()
        text = "".join(f"scenario:g429 文{i}です。" if i == 0 else f"文{i}です。" for i in range(10))
        c, js = submit(agent, {"text": text})
        job = wait_job(agent, js["job_id"])
        chunks = job["chunks"]
        check("gemini 429 -> agnes; 10 sentences re-chunked to agnes size 8 (+2)", job["status"] == "done" and any("rechunk" in t for ch in chunks for t in ch["trail"])
              and sorted(ch["seq"] for ch in chunks if ch["result"] == "ok") == ["0-7", "8-9"], chunks)
        c, h = call("GET", "/health")
        circ = h["analyze"]["circuit"]
        check("gemini lite+flash open (rate, retryDelay 7 s)", circ.get("gemini:gemini-flash-lite-latest", {}).get("reason") == "rate", circ)
        c, js = submit(agent, {"text": "scenario:json_ok 次の記事。"})
        job = wait_job(agent, js["job_id"])
        check("next article skips open gemini (open-rate) and is done by agnes", job["status"] == "done" and "open-rate" in job["trail"]
              and job["provider_model"].startswith("agnes:"), job["trail"])
        a = art(js["share_id"])
        check("tr_via agnes-api", a["versions"][-1]["tr_via"] == "agnes-api", a["versions"][-1])
        restart_instance()
        for i in range(3):
            c, js = submit(agent, {"text": f"scenario:gbad 品質{i}の記事。"})
            job = wait_job(agent, js["job_id"])
        c, h = call("GET", "/health")
        check("E7: 3 articles failing validation on gemini -> gemini open (quality)", h["analyze"]["circuit"].get("gemini:gemini-flash-lite-latest", {}).get("reason") is None
              and any(v["reason"] == "quality" for k, v in h["analyze"]["circuit"].items() if k == "gemini"), h["analyze"]["circuit"])
        restart_instance()

    # ------------------------------------------------------------------ K. restart recovery
    if want("K"):
        print("== K. job survives a restart (persisted; running -> retry -> done)")
        tune(INGEST_TIMEOUT_GEMINI_LITE_S=30)
        c, js = submit(agent, {"text": "scenario:gsleep:6 再起動の記事。"})
        wait_job(agent, js["job_id"], {"running"}, 10)
        time.sleep(1.5)
        restart_instance()
        job = wait_job(agent, js["job_id"], secs=60)
        check("job finished after graceful restart (worker set it back to retry)", job["status"] == "done" and job["result"]["article_status"] == "ready", job)
        c, js = submit(agent, {"text": "scenario:gsleep:6 強制終了の記事。"})
        wait_job(agent, js["job_id"], {"running"}, 10)
        time.sleep(1.5)
        subprocess.run(["python3", "tests/p2_stack.py", "crash", "mock8795.env", "ingest_mock.env", "ingest_tune.env"], cwd=ROOT, capture_output=True)
        job = wait_job(agent, js["job_id"], secs=60)
        tune()
        log = open(ROOT + "/.probe/agnes-8795.log", encoding="utf-8", errors="replace").read()
        check("SIGKILL mid-job: startup recovery running -> retry -> done", job["status"] == "done" and "interrupted job(s) set back to retry" in log, job)

    # ------------------------------------------------------------------ L. upstream bucket + admission
    if want("L"):
        print("== L. upstream token bucket (interactive fast 429 + refund) and ingest daily quota")
        tune(UPSTREAM_RPM_AGNES=2)
        codes = [call("POST", "/api/text", {"prompt": f"hi {i}"}, admin)[0] for i in range(3)]
        c, js = call("POST", "/api/text", {"prompt": "hi 4"}, admin)
        tune()
        check("3rd+ interactive Agnes call in the minute -> 429 upstream_throttled", codes[:2] == [200, 200] and codes[2] == 429 and c == 429 and js["code"] == "upstream_throttled", (codes, js))
        u = usage(admin)
        check("throttled calls refunded (text used == 2)", u["text"] == 2, u)
        psql("UPDATE agnes_auth.users SET daily_quota = 0 WHERE username = :'v1';", f"p2t_agent_{SUF}")
        time.sleep(0.2)
        c, js = submit(agent, {"text": "scenario:json_ok 上限の記事。"})
        psql("UPDATE agnes_auth.users SET daily_quota = 500 WHERE username = :'v1';", f"p2t_agent_{SUF}")
        check("daily quota exhausted -> 429 at POST (no job)", c == 429 and js["code"] == "daily_quota_exceeded", js)
        rej = [r for r in calls(admin, agent_id) if r["outcome"] == "daily_quota_exceeded"]
        check("rejected admission logged (kind=ingest)", len(rej) >= 1)

    if want("M"):
        case_m()


def raw_post(path, body, cookie=None):
    """POST returning (status, json, Set-Cookie header) — for the cookie-based auth endpoints."""
    req = urllib.request.Request(BASE + path, data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json", **({"Cookie": cookie} if cookie else {})})
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            code, raw, sc = r.status, r.read(), r.headers.get("Set-Cookie")
    except urllib.error.HTTPError as e:
        code, raw, sc = e.code, e.read(), e.headers.get("Set-Cookie")
    return code, (json.loads(raw) if raw else None), sc


def rt_of(set_cookie):
    return set_cookie.split(";", 1)[0] if set_cookie and set_cookie.startswith("agnes_rt=") else None


def case_m():
    """Design §7-9 (optional fix): two tabs (管理页 + /add) refreshing the same session at once must not log out."""
    print("== M. refresh 'already rotated' within the grace keeps the cookie (two tabs)")
    name = f"p2t_tabs_{SUF}"
    pw = secrets.token_urlsafe(18)
    out = cli("--create-user", name, "user", "--password-stdin", stdin=pw + "\n")
    USERS.append(name)
    c, _, sc = raw_post("/api/auth/login", {"username": name, "password": pw})
    a = rt_of(sc)
    check("login sets agnes_rt", out.returncode == 0 and c == 200 and a, (c, sc))
    c1, _, sc1 = raw_post("/api/auth/refresh", {}, a)          # tab 1 rotates A → B
    b = rt_of(sc1)
    c2, js2, sc2 = raw_post("/api/auth/refresh", {}, a)        # tab 2, same moment, still holds A
    check("tab 2 within grace -> 401 refresh_rotated", c1 == 200 and b and c2 == 401 and js2["code"] == "refresh_rotated", (c1, c2, js2))
    check("... and the cookie is NOT cleared (no Set-Cookie)", sc2 is None, sc2)
    c3, js3, _ = raw_post("/api/auth/refresh", {}, b)          # the page retries with the jar's current cookie
    check("retry with the current cookie -> 200 (nobody logged out)", c3 == 200 and js3.get("access_token"), (c3, js3))
    c4, js4, sc4 = raw_post("/api/auth/refresh", {}, "agnes_rt=garbage-garbage")
    check("dead cookie still cleared (401 invalid_refresh_token + empty agnes_rt)", c4 == 401 and js4["code"] == "invalid_refresh_token"
          and rt_of(sc4) == "agnes_rt=", (c4, js4, sc4))


def usage_of(admin, uid):
    c, js = call("GET", "/api/admin/users", token=admin)
    u = next(x for x in js["users"] if x["id"] == uid)
    return int(psql("SELECT coalesce(sum(count),0) FROM agnes_auth.usage_daily WHERE user_id = :v1 AND kind = 'ingest';", str(uid)))


try:
    main()
finally:
    tune()
    mock("POST", "/mock/ttsweb/mode", {"mode": "normal"})
    check("test accounts + their jobs cleaned up", cleanup())
    print(f"== {PASS} passed, {FAIL} failed")
sys.exit(0 if FAIL == 0 else 1)
