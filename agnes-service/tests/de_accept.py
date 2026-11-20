#!/usr/bin/env python3
"""2026-11-20 delete / edit acceptance (DELETE-EDIT-REPORT.md).

  dev  (default): agnes-service :8795 (DB agnes_p2dev, mock LLMs) + dev TtsWeb :8796 (tts-redesign/impl/deledit)
        python3 tests/p2_stack.py ttsweb
        python3 tests/p2_stack.py start mock8795.env ingest_mock.env ingest_ttswebdev.env ingest_tune.env
        python3 tests/de_accept.py
  prod: python3 tests/de_accept.py prod      8793 + 8791 + db77database, real LLMs; one-off test accounts (deleted at the
        end) and test articles (soft-deleted at the end; share_ids printed). Reads the reading schema with SELECT only.

Account passwords are random, used once and never printed. No token values are printed.
"""
import json
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request

PROD = len(sys.argv) > 1 and sys.argv[1] == "prod"
ROOT = "/data/workspace/agnes-service"
DOTNET = "/data/dotnet-sdk/dotnet"
if PROD:
    BASE, TTS, DB = "http://127.0.0.1:8793", "http://127.0.0.1:8791", "db77database"
    DLL, ENV = ROOT + "/publish/AgnesService.dll", ["--env-file", ROOT + "/.env"]
    JOB_WAIT = 480
else:
    BASE, TTS, DB = "http://127.0.0.1:8795", "http://127.0.0.1:8796", "agnes_p2dev"
    DLL, ENV = ROOT + "/bin/Release/net10.0/AgnesService.dll", ["--env-file", ROOT + "/.env", "--env-file", ROOT + "/tests/devdb8795.env"]
    JOB_WAIT = 90
SUF = secrets.token_hex(2)
PASS = FAIL = 0
USERS, SIDS = [], []
FINAL = ("done", "l2", "failed", "cancelled")


def check(name, ok, info=""):
    global PASS, FAIL
    PASS, FAIL = (PASS + 1, FAIL) if ok else (PASS, FAIL + 1)
    print(("  ok    " if ok else "  FAIL  ") + name + (("  -> " + str(info)[:600]) if info and not ok else ""), flush=True)


def http(method, url, body=None, token=None, headers=None):
    req = urllib.request.Request(url, data=None if body is None else json.dumps(body, ensure_ascii=False).encode(), method=method)
    if body is not None:
        req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            code, raw, hdrs = r.status, r.read(), dict(r.headers)
    except urllib.error.HTTPError as e:
        code, raw, hdrs = e.code, e.read(), dict(e.headers)
    try:
        js = json.loads(raw) if raw else None
    except ValueError:
        js = {"_raw": raw[:200]}
    return (code, js, hdrs) if headers is not None else (code, js)


def psql(sql, *p):
    args = ["psql", "-d", DB, "-v", "ON_ERROR_STOP=1", "-Atq"] + sum((["-v", f"v{i}={x}"] for i, x in enumerate(p, 1)), [])
    return subprocess.run(args, input=sql, capture_output=True, text=True, check=True).stdout.strip()


def mkuser(name, role="user", extra=()):
    pw = secrets.token_urlsafe(18) + "aA1!"
    out = subprocess.run([DOTNET, DLL, *ENV, "--create-user", name, role, *extra, "--password-stdin"], input=pw + "\n",
                         capture_output=True, text=True, cwd=ROOT)
    assert out.returncode == 0, out.stderr[-300:]
    USERS.append(name)
    c, js = http("POST", BASE + "/api/auth/login", {"username": name, "password": pw})
    assert c == 200, (c, js)
    return js["access_token"]


def wait_job(tok, jid, secs=JOB_WAIT, stop=FINAL):
    t0, js = time.time(), None
    while time.time() - t0 < secs:
        c, js = http("GET", f"{BASE}/api/reading/jobs/{jid}", token=tok)
        if c == 200 and js["job"]["status"] in stop:
            return js["job"]
        time.sleep(0.5 if not PROD else 3)
    return (js or {}).get("job")


def art(sid):
    """reading.articles row (SELECT only): added_by, added_via, status, current_version, lang, title, deleted?, versions."""
    row = psql("SELECT coalesce(added_by,'') || '|' || added_via || '|' || status || '|' || current_version || '|' || lang || '|' || "
               "coalesce(title,'') || '|' || (deleted_at IS NOT NULL)::text || '|' || "
               "(SELECT count(*) FROM reading.versions v WHERE v.article_id = a.share_id) FROM reading.articles a WHERE share_id = :'v1';", sid)
    if not row:
        return None
    k = ("added_by", "added_via", "status", "version", "lang", "title", "deleted", "versions")
    d = dict(zip(k, row.split("|")))
    d["version"], d["versions"] = int(d["version"]), int(d["versions"])
    return d


def text_of(sid):
    return psql("SELECT text FROM reading.articles WHERE share_id = :'v1';", sid)


def ingest(tok, title, text, lang="ja"):
    c, js = http("POST", BASE + "/api/reading/ingest", {"title": title, "text": text, "lang": lang} if title else {"text": text, "lang": lang}, tok)
    assert c == 202 and js.get("share_id"), (c, js)
    SIDS.append(js["share_id"])
    return js["share_id"], js["job_id"]


def listed(tok, sid):
    c, js = http("GET", BASE + "/api/reading/articles", token=tok)
    return c, next((a for a in (js or {}).get("articles", []) if a["share_id"] == sid), None)


def sc(name):
    return "" if PROD else f"scenario:{name} "


def main():
    tag = "deltest" if PROD else "det"
    web1 = mkuser(f"zz_{tag}_web1_{SUF}", extra=["--reading-ingest", "web"])
    web2 = mkuser(f"zz_{tag}_web2_{SUF}", extra=["--reading-ingest", "web"])
    agent = mkuser(f"zz_{tag}_agent_{SUF}", extra=["--reading-ingest", "agent", "--muse-id", "A002"])
    admin = mkuser(f"zz_{tag}_admin_{SUF}", "admin")
    none = mkuser(f"zz_{tag}_none_{SUF}")

    print("== 1. web channel: new test article")
    sid, jid = ingest(web1, f"删改测试{SUF}", f"{sc('json_ok')}今日は{SUF}番目の削除テストです。駅は人でいっぱいだった。")
    job = wait_job(web1, jid)
    a0 = art(sid)
    check(f"web save -> job done, article web/web ({sid})", job and job["status"] == "done" and a0["added_by"] == "web"
          and a0["added_via"] == "web", (job and job["status"], a0))

    print("== list / detail")
    c, js = http("GET", BASE + "/api/reading/articles")
    check("list without JWT -> 401", c == 401, c)
    for name, tok, want in (("none", none, False), ("web2 (same channel)", web2, True), ("agent", agent, False), ("admin", admin, True),
                            ("web1", web1, True)):
        c, it = listed(tok, sid)
        check(f"list as {name}: visible, can_manage={want}", c == 200 and it and it["can_manage"] is want, (c, it))
    c, it = listed(none, sid)
    keys = {"share_id", "title", "status", "added_by", "added_via", "added_at", "sentences", "url"}
    check("list fields: share_id/title/status/added_by/added_via/added_at/sentences/url", it and keys <= set(it) and it["sentences"] == 2
          and it["url"] and it["url"].endswith("/v3?s=" + sid), it)
    c, js = http("GET", f"{BASE}/api/reading/articles/{sid}", token=web1)
    check("detail returns the original text for prefill", c == 200 and js["article"]["text"] == text_of(sid)
          and js["article"]["can_manage"] is True, (c, js))
    c, js = http("GET", f"{BASE}/api/reading/articles/zzz", token=web1)
    check("detail bad id -> 404", c == 404, c)

    print("== title-only edit (no new version)")
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"title": f"改名后{SUF}"}, web1)
    a1 = art(sid)
    check("PATCH title -> 200 mode=meta", c == 200 and js["mode"] == "meta" and js["title"] == f"改名后{SUF}", (c, js))
    check("title changed, version and versions count unchanged", a1["title"] == f"改名后{SUF}" and a1["version"] == a0["version"]
          and a1["versions"] == a0["versions"], (a0, a1))
    c, t = http("GET", f"{TTS}/v3/t/{sid}")
    check("/v3/t shows the new title", c == 200 and t.get("title") == f"改名后{SUF}", (c, t and t.get("title")))
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"title": f"改名后{SUF}", "text": text_of(sid) + "\n"}, web1)
    check("same text (+ whitespace) and same title -> 200 mode=noop", c == 200 and js["mode"] == "noop", (c, js))
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"text": "x", "rate": "+0%"}, web1)
    check("unknown field -> 400 unknown_field", c == 400 and js["code"] == "unknown_field", (c, js))
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"title": "  "}, web1)
    check("empty title -> 400 title_required", c == 400 and js["code"] == "title_required", (c, js))
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"lang": "fr"}, web1)
    check("bad lang -> 400 invalid_lang", c == 400 and js["code"] == "invalid_lang", (c, js))
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"text": "x" * 5001}, web1)
    check("text > 5000 -> 400 invalid_text", c == 400 and js["code"] == "invalid_text", (c, js))

    print("== text edit -> new version on the same share_id")
    new_text = f"{sc('json_ok')}今日は{SUF}番目の編集テストです。本文を書き換えました。三文目も足した。"
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"text": new_text}, web1)
    check("PATCH text -> 202 mode=job op=edit, same share_id", c == 202 and js["mode"] == "job" and js["job"]["op"] == "edit"
          and js["share_id"] == sid, (c, js))
    ejid = js.get("job_id")
    if not PROD:
        c2, js2 = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"text": new_text + "もう一文。"}, web1)
        check("second text edit while the first job is active -> 409 job_active", c2 == 409 and js2["code"] == "job_active", (c2, js2))
    job = wait_job(web1, ejid)
    a2 = art(sid)
    check("edit job done (outcome ok), article ready", job and job["status"] == "done" and a2["status"] == "ready", (job, a2))
    check("new version (current_version up), old versions kept (versions count up)", a2["version"] > a1["version"]
          and a2["versions"] > a1["versions"], (a1, a2))
    check("article text replaced, title kept", "編集テスト" in text_of(sid) and a2["title"] == f"改名后{SUF}", a2)
    old = psql("SELECT count(*) FROM reading.sentences WHERE article_id = :'v1' AND version = :v2;", sid, a1["version"])
    check("old version's sentences still in reading.sentences", int(old) == 2, old)
    prev = psql("SELECT (tool ? 'prev_text')::text FROM reading.versions WHERE article_id = :'v1' AND version = :v2;", sid, a2["version"])
    check("versions.tool records prev_text", prev == "true", prev)
    c, t = http("GET", f"{TTS}/v3/t/{sid}")
    check("short link unchanged and plays the new text", c == 200 and "編集テスト" in t.get("text", "") and t.get("version") == a2["version"],
          (c, t and t.get("version")))
    c, it = listed(web1, sid)
    check("list shows new sentence count", it and it["sentences"] == 3 and it.get("active_job") is None, it)

    print("== D8: edit to non-Japanese -> original only, no enrich")
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"lang": "zh", "text": f"这是第{SUF}篇中文测试。只存原文。"}, web1)
    job = wait_job(web1, js.get("job_id")) if c == 202 else None
    a3 = art(sid)
    check("zh edit: 202 -> done edit_non_ja, lang zh, new version, partial (no translation)", c == 202 and job and job["status"] == "done"
          and (job.get("result") or {}).get("outcome") == "edit_non_ja" and a3["lang"] == "zh" and a3["version"] > a2["version"]
          and a3["status"] == "partial", (c, job and job.get("result"), a3))
    tr = psql("SELECT count(*) FROM reading.sentences WHERE article_id = :'v1' AND version = :v2 AND tr IS NOT NULL;", sid, a3["version"])
    check("zh version has no translations", tr == "0", tr)

    if not PROD:
        print("== edit whose new text equals another live article -> failed edit_duplicate_text, article unchanged")
        osid, ojid = ingest(web2, f"别的{SUF}", f"scenario:json_ok 重複{SUF}の記事です。")
        wait_job(web2, ojid)
        before = art(osid)
        c, js = http("PATCH", f"{BASE}/api/reading/articles/{osid}", {"text": text_of(sid)}, web2)
        job = wait_job(web2, js.get("job_id")) if c == 202 else None
        check("duplicate text edit -> job failed edit_duplicate_text, version unchanged", job and job["status"] == "failed"
              and job["error_code"] == "edit_duplicate_text" and art(osid)["version"] == before["version"], (c, js, job))

    print("== 2. permissions (agent-channel test article)")
    asid, ajid = ingest(agent, None, f"{sc('json_ok')}エージェント{SUF}の記事です。")
    job = wait_job(agent, ajid)
    aa = art(asid)
    check(f"agent save -> article added_by=A002 / agent ({asid})", aa["added_by"] == "A002" and aa["added_via"] == "agent", aa)
    for name, tok in (("web1", web1), ("none", none)):
        c, js = http("DELETE", f"{BASE}/api/reading/articles/{asid}", token=tok)
        check(f"{name} DELETE agent article -> 403 article_forbidden", c == 403 and js["code"] == "article_forbidden", (c, js))
        c, js = http("PATCH", f"{BASE}/api/reading/articles/{asid}", {"title": "hijack"}, tok)
        check(f"{name} PATCH agent article -> 403", c == 403 and js["code"] == "article_forbidden", (c, js))
        c, js = http("PATCH", f"{BASE}/api/reading/articles/{asid}", {"text": "乗っ取りの本文です。"}, tok)
        check(f"{name} text edit agent article -> 403 (no job, no quota)", c == 403, (c, js))
    c, js = http("DELETE", f"{BASE}/api/reading/articles/{sid}", token=agent)
    check("agent DELETE web article -> 403", c == 403, (c, js))
    check("agent article untouched by the refused calls", art(asid)["deleted"] == "false" and art(asid)["title"] != "hijack", art(asid))
    c, js = http("DELETE", f"{BASE}/api/reading/articles/{asid}")
    check("DELETE without JWT -> 401", c == 401, c)
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{asid}", {"title": f"管理员改{SUF}"}, admin)
    check("admin PATCH agent article title -> 200 meta", c == 200 and js["mode"] == "meta" and art(asid)["title"] == f"管理员改{SUF}", (c, js))
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{asid}", {"text": f"{sc('json_ok')}管理員{SUF}が書き換えた本文です。"}, admin)
    job = wait_job(admin, js.get("job_id")) if c == 202 else None
    check("admin text edit agent article -> 202, job done, added_by still A002", c == 202 and job and job["status"] == "done"
          and art(asid)["added_by"] == "A002" and art(asid)["version"] > aa["version"], (c, js, job and job["status"]))
    c, js = http("DELETE", f"{BASE}/api/reading/articles/{asid}", token=admin)
    check("admin DELETE agent article -> 200", c == 200 and js["ok"] and art(asid)["deleted"] == "true", (c, js))
    a2sid, a2jid = ingest(agent, None, f"{sc('json_ok')}エージェント{SUF}の二本目です。")
    wait_job(agent, a2jid)
    c, it = listed(agent, a2sid)
    check("agent sees can_manage=true on its own article", it and it["can_manage"] is True, it)
    c, js = http("DELETE", f"{BASE}/api/reading/articles/{a2sid}", token=agent)
    check("agent DELETE own article -> 200", c == 200, (c, js))

    if not PROD:
        print("== edit while L1 cannot translate (all providers quota) -> text-only version first, then retries / L2")
        c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"lang": "ja", "text": f"scenario:allquota 今日は{SUF}の失敗テストです。"}, web1)
        jid4 = js.get("job_id")
        job = wait_job(web1, jid4, 60, stop=("l2", "failed", "done", "cancelled"))
        a4 = art(sid)
        check("text-only commit landed (new text, ja, partial), job ends in L2 after the web retries", job and job["status"] == "l2"
              and "失敗テスト" in text_of(sid) and a4["lang"] == "ja" and a4["status"] == "partial" and a4["version"] > a3["version"],
              (job and (job["status"], job.get("error_code"), job.get("trail")), a4))

    print("== CORS")
    c, js, h = http("OPTIONS", f"{BASE}/api/reading/articles/{sid}", headers={"Origin": "https://x.example",
                                                                             "Access-Control-Request-Method": "DELETE"})
    check("preflight answers no Access-Control-* headers", not any(k.lower().startswith("access-control") for k in h), h)

    print("== 1 (end). delete the web article")
    pend = psql("SELECT count(*) FROM agnes_auth.ingest_jobs WHERE share_id = :'v1' AND status IN ('queued','retry','l2');", sid)
    c, js = http("DELETE", f"{BASE}/api/reading/articles/{sid}", token=web1)
    check("web1 DELETE own article -> 200", c == 200 and js["ok"] and js["share_id"] == sid, (c, js))
    check("pending jobs of the article cancelled (article_deleted)", js.get("jobs_cancelled") == int(pend)
          and psql("SELECT count(*) FROM agnes_auth.ingest_jobs WHERE share_id = :'v1' AND status IN ('queued','retry','l2');", sid) == "0",
          (pend, js))
    a5 = art(sid)
    check("soft delete: row kept, deleted_at set", a5 and a5["deleted"] == "true", a5)
    for path in (f"/t/{sid}", f"/v3/t/{sid}"):
        c, _ = http("GET", TTS + path)
        check(f"{path} -> 404", c == 404, c)
    c, js = http("GET", TTS + "/v3/articles")
    check("/v3/articles (player list) no longer lists it", c == 200 and all(x["share_id"] != sid for x in js["articles"]), c)
    c, it = listed(web1, sid)
    check("8793 article list no longer lists it", c == 200 and it is None, it)
    c, js = http("DELETE", f"{BASE}/api/reading/articles/{sid}", token=web1)
    check("DELETE again -> 404 not_found", c == 404 and js["code"] == "not_found", (c, js))
    c, js = http("PATCH", f"{BASE}/api/reading/articles/{sid}", {"title": "x"}, web1)
    check("PATCH deleted -> 404", c == 404, (c, js))
    n = psql("SELECT count(*) FROM agnes_auth.admin_audit WHERE target_username = ANY(string_to_array(:'v1', ',')) "
             "AND action IN ('delete_article','update_article','edit_article');", ",".join([sid, asid]))
    check("admin_audit rows for article delete/update/edit", int(n) >= 5, n)


try:
    main()
finally:
    # cleanup: every test article soft-deleted (via TtsWeb through the admin API path is not needed: direct check),
    # test accounts removed
    tok = None
    left = [s for s in SIDS if (art(s) or {}).get("deleted") == "false"]
    if left:
        try:
            tok = mkuser(f"zz_cleanup_{SUF}", "admin")
            for s in left:
                c, _ = http("DELETE", f"{BASE}/api/reading/articles/{s}", token=tok)
                print(f"  cleanup delete {s} -> {c}")
        except Exception as e:  # noqa: BLE001
            print("  cleanup failed:", e)
    for name in USERS:
        psql("DELETE FROM agnes_auth.ingest_jobs WHERE username = :'v1';", name)
        psql("DELETE FROM agnes_auth.refresh_tokens WHERE user_id IN (SELECT id FROM agnes_auth.users WHERE username = :'v1');", name)
        psql("DELETE FROM agnes_auth.users WHERE username = :'v1';", name)
        psql("DELETE FROM agnes_auth.login_audit WHERE username = :'v1';", name)
    print("== test share_ids:", ", ".join(SIDS))
    print("== still live after cleanup:", [s for s in SIDS if (art(s) or {}).get("deleted") == "false"])
    print(f"== {PASS} passed, {FAIL} failed")
sys.exit(0 if FAIL == 0 else 1)
