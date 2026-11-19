#!/usr/bin/env python3
"""TASK6 phase 2: web channel + TtsWeb contract end-to-end against the DEV TtsWeb copy (tts-redesign/impl/phase2, :8796,
isolated DB agnes_p2dev) — never 8791 / db77database. LLMs are still the mock (scenario:json_ok).

  python3 tests/p2_stack.py start mock8795.env ingest_mock.env ingest_ttswebdev.env ingest_tune.env   (or: ttsweb + restart)
  python3 tests/p2_stack.py ttsweb
  python3 tests/p2_ttswebdev.py
"""
import json
import os
import secrets
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = "/data/workspace/agnes-service"
DOTNET = "/data/dotnet-sdk/dotnet"
DLL = ROOT + "/bin/Release/net10.0/AgnesService.dll"
BASE, TTS = "http://127.0.0.1:8795", "http://127.0.0.1:8796"
DB = "agnes_p2dev"
SEC = "/data/workspace/tts-redesign/impl/phase2/dev_secrets/"
ENV = ["--env-file", ROOT + "/.env", "--env-file", ROOT + "/tests/devdb8795.env"]
SUF = secrets.token_hex(2)
PASS = FAIL = 0
USERS = []


def check(name, ok, info=""):
    global PASS, FAIL
    PASS, FAIL = (PASS + 1, FAIL) if ok else (PASS, FAIL + 1)
    print(("  ok    " if ok else "  FAIL  ") + name + (("  -> " + str(info)[:500]) if info and not ok else ""), flush=True)


def http(method, url, body=None, token=None, timeout=30):
    req = urllib.request.Request(url, data=None if body is None else json.dumps(body, ensure_ascii=False).encode(), method=method)
    if body is not None:
        req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            code, raw = r.status, r.read()
    except urllib.error.HTTPError as e:
        code, raw = e.code, e.read()
    try:
        return code, json.loads(raw) if raw else None
    except ValueError:
        return code, {"_raw": raw[:200]}


def psql(sql, *p):
    args = ["psql", "-d", DB, "-v", "ON_ERROR_STOP=1", "-Atq"] + sum((["-v", f"v{i}={x}"] for i, x in enumerate(p, 1)), [])
    return subprocess.run(args, input=sql, capture_output=True, text=True, check=True).stdout.strip()


def mkuser(name, extra=()):
    pw = secrets.token_urlsafe(18)
    out = subprocess.run([DOTNET, DLL, *ENV, "--create-user", name, "user", *extra, "--password-stdin"], input=pw + "\n",
                         capture_output=True, text=True, cwd=ROOT)
    assert out.returncode == 0, out.stderr[-300:]
    USERS.append(name)
    c, js = http("POST", BASE + "/api/auth/login", {"username": name, "password": pw})
    assert c == 200, (c, js)
    return js["access_token"]


def wait_job(tok, jid, secs=60):
    t0 = time.time()
    while time.time() - t0 < secs:
        c, js = http("GET", f"{BASE}/api/reading/jobs/{jid}", token=tok)
        if c == 200 and js["job"]["status"] in ("done", "l2", "failed", "cancelled"):
            return js["job"]
        time.sleep(0.5)
    return js["job"]


def article(sid):
    row = psql("SELECT added_by || '|' || added_via || '|' || status || '|' || current_version FROM reading.articles WHERE share_id = :'v1';", sid)
    return row.split("|") if row else None


def stack(*a):
    subprocess.run(["python3", "tests/p2_stack.py", *a], cwd=ROOT, capture_output=True, text=True)


def main():
    reader = open(SEC + "reader.txt").read().strip()
    a001 = open(SEC + "A001.txt").read().strip()
    web = mkuser(f"p2t_dweb_{SUF}", ["--reading-ingest", "web"])
    none = mkuser(f"p2t_dnone_{SUF}")
    print("== dev TtsWeb contract (§5)")
    c, js = http("POST", TTS + "/share", {"text": "匿名の記事。"})
    check("anonymous /share after grace -> 401", c == 401, (c, js))
    c, js = http("POST", TTS + "/share", {"text": "令牌の記事です。", "added_via": "bogus"}, a001)
    check("/share added_via invalid -> 400", c == 400, (c, js))
    c, js = http("POST", TTS + "/share", {"text": f"A001の記事{SUF}です。", "by": "web"}, a001)
    check("A001 token may not act for 'web' (no can_act_for)", c in (401, 403), (c, js))
    c, js = http("POST", TTS + "/share", {"text": f"A001の記事{SUF}です。"}, a001)
    check("/share with token: added_via defaults to agent", c == 200 and article(js["id"])[:2] == ["A001", "agent"], (c, js))
    partial_sid = js.get("id")
    c, lst = http("GET", TTS + "/v3/articles")
    items = lst if isinstance(lst, list) else (lst or {}).get("articles", [])
    hit = next((x for x in items if x.get("share_id") == partial_sid), None)
    check("/v3/articles lists a partial article with status", hit is not None and hit.get("status") == "partial", hit)
    c, js = http("POST", TTS + "/api/v1/ingest/readings_check",
                 {"items": [{"sentence": "東京へ行く。", "tok": 0, "s": "東京", "r": "とうきょう"},
                            {"sentence": "東京へ行く。", "tok": 0, "s": "東京", "r": "ひがしきょう"}]}, reader)
    res = (js or {}).get("results") or [{}, {}]
    check("readings_check: nbest supports 東京=とうきょう, not ひがしきょう", c == 200 and res[0].get("supported") is True
          and res[1].get("supported") is False and "とうきょう" in res[0].get("candidates", []), js)

    print("== web channel (8795 JWT) -> agnes-service token -> dev TtsWeb")
    c, js = http("POST", BASE + "/api/reading/ingest", {"title": "x", "text": "記事。"}, none)
    check("reading_ingest=none -> 403 ingest_forbidden", c == 403 and js["code"] == "ingest_forbidden", js)
    t0 = time.time()
    c, js = http("POST", BASE + "/api/reading/ingest", {"title": f"網の記事{SUF}", "text": f"scenario:json_ok 今日は{SUF}の東京へ行った。駅は人でいっぱいだった。"}, web)
    ms = int((time.time() - t0) * 1000)
    check(f"web save 202 + share_id from real /share ({ms} ms)", c == 202 and js["share_id"], js)
    sid = js.get("share_id")
    a = article(sid) if sid else None
    check("reading.articles right after save: added_by=web, added_via=web, partial", a and a[:3] == ["web", "web", "partial"], a)
    c, t = http("GET", f"{TTS}/v3/t/{sid}")
    check("/v3/t carries status (partial)", c == 200 and t.get("status") == "partial", (c, t and {k: t.get(k) for k in ("status", "title")}))
    job = wait_job(web, js["job_id"])
    a = article(sid)
    check("job done -> article ready via replace (v2), added_by/added_via untouched", job["status"] == "done"
          and a and a[0] == "web" and a[1] == "web" and a[2] == "ready" and int(a[3]) >= 2, (job.get("status"), job.get("trail"), a))
    via = psql("SELECT string_agg(DISTINCT tr_via, ',') FROM reading.sentences WHERE article_id = :'v1' AND version = :v2;", sid, a[3] if a else "0")
    check("sentences tr_via = gemini-api (CHECK relaxed)", via == "gemini-api", via)

    print("== TtsWeb down at save time (stop 8796) -> 202 share_id=null -> worker fills it in after restart")
    os.kill(int(open(ROOT + "/.probe/p2_tts.pid").read()), signal.SIGTERM)
    time.sleep(2)
    c, js = http("POST", BASE + "/api/reading/ingest", {"title": f"停止中{SUF}", "text": f"scenario:json_ok 停止中に{SUF}保存した記事。"}, web)
    check("8796 down: 202 + share_id null", c == 202 and js["share_id"] is None, js)
    stack("ttsweb")
    job = wait_job(web, js["job_id"], 90)
    a = article(job["share_id"]) if job.get("share_id") else None
    check("after 8796 is back the job shares + completes (web/web/ready)", job["status"] == "done" and a and a[:3] == ["web", "web", "ready"], (job, a))


try:
    main()
finally:
    for n in USERS:
        psql("DELETE FROM agnes_auth.ingest_jobs WHERE username = :'v1';", n)
        psql("DELETE FROM agnes_auth.users WHERE username = :'v1';", n)
        psql("DELETE FROM agnes_auth.login_audit WHERE username = :'v1';", n)
    print(f"== {PASS} passed, {FAIL} failed")
sys.exit(0 if FAIL == 0 else 1)
