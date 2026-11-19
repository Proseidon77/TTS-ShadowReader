#!/usr/bin/env python3
"""TASK6 phase 1 acceptance / smoke harness.

  python3 tests/task6_accept.py mock  --base http://127.0.0.1:8795   regression + §11 phase-1 items 1-6 (dev instance + MockAgnes)
  python3 tests/task6_accept.py real  --base http://127.0.0.1:8795   real Agnes smoke (2 raw + 1 enrich, costs 3 analyze units)
  python3 tests/task6_accept.py prod  --base http://127.0.0.1:8793   production smoke (/health, 401, 1 real raw, admin filter)

Each run creates its own throw-away account (random password, passed to the CLI on stdin, never printed),
and deletes it at the end (call_log / usage_daily rows cascade; its login_audit rows are deleted too).
Nothing else in the database is touched, except daily_quota of that test account for the 429 check.
"""
import json
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = "/data/workspace/agnes-service"
DOTNET = "/data/dotnet-sdk/dotnet"
DLL = ROOT + "/bin/Release/net10.0/AgnesService.dll"
import os
# TASK6 phase 2: the dev instance runs on an isolated database (AGNES_TEST_DB, default db77database for the
# phase-1 behaviour) and the CLI reads the same env overlays as the instance (AGNES_TEST_ENV_FILES, comma list).
TEST_DB = os.environ.get("AGNES_TEST_DB", "db77database")
ENV_FILES = [x for x in os.environ.get("AGNES_TEST_ENV_FILES", ROOT + "/.env").split(",") if x]
PSQL = ["psql", "-d", TEST_DB, "-v", "ON_ERROR_STOP=1", "-Atq"]

mode = sys.argv[1]
BASE = sys.argv[sys.argv.index("--base") + 1] if "--base" in sys.argv else "http://127.0.0.1:8795"
USER = "t6p1_" + mode + "_" + secrets.token_hex(3)
PASS = 0
FAIL = 0


def check(name, ok, info=""):
    global PASS, FAIL
    if ok:
        PASS += 1
    else:
        FAIL += 1
    print(("  ok    " if ok else "  FAIL  ") + name + (("  -> " + info) if info else ""), flush=True)


def call(method, path, body=None, token=None, timeout=600):
    data = None if body is None else json.dumps(body, ensure_ascii=False).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if body is not None:
        req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            code, raw = r.status, r.read()
    except urllib.error.HTTPError as e:
        code, raw = e.code, e.read()
    ms = int((time.time() - t0) * 1000)
    try:
        js = json.loads(raw) if raw else None
    except ValueError:
        js = {"_raw": raw[:300].decode("utf-8", "replace")}
    return code, js, ms


def psql(sql, *params):
    # params are bound as psql variables (:'v1' …) so values are quoted by psql, not by string formatting
    args = list(PSQL)
    for i, p in enumerate(params, 1):
        args += ["-v", f"v{i}={p}"]
    return subprocess.run(args, input=sql, capture_output=True, text=True, check=True).stdout.strip()


def create_user(role="admin", extra=()):
    pw = secrets.token_urlsafe(18)
    cmd = [DOTNET, DLL, *[a for f in ENV_FILES for a in ("--env-file", f)], "--create-user", USER, role, *extra, "--password-stdin"]
    out = subprocess.run(cmd, input=pw + "\n", capture_output=True, text=True, cwd=ROOT)
    if out.returncode != 0:
        raise SystemExit("create-user failed: " + out.stderr[-300:])
    print("  (created test account " + out.stdout.strip() + ")")
    code, js, _ = call("POST", "/api/auth/login", {"username": USER, "password": pw})
    if code != 200:
        raise SystemExit(f"login failed {code}")
    return js["access_token"], js["user"]["id"]


def delete_user():
    n1 = psql("DELETE FROM agnes_auth.users WHERE username = :'v1' RETURNING id;", USER)
    n2 = psql("WITH d AS (DELETE FROM agnes_auth.login_audit WHERE username = :'v1' RETURNING 1) SELECT count(*) FROM d;", USER)
    left = psql("SELECT (SELECT count(*) FROM agnes_auth.users WHERE username = :'v1') + "
                "(SELECT count(*) FROM agnes_auth.call_log WHERE username = :'v1') + "
                "(SELECT count(*) FROM agnes_auth.login_audit WHERE username = :'v1');", USER)
    print(f"  (cleanup: deleted user id {n1 or '-'}, {n2} login_audit rows; rows left for {USER}: {left})")
    return left == "0"


def used(token):
    _, js, _ = call("GET", "/api/limits", token=token)
    return js["usage"]["used"]


def tok(res, s):
    return next((t for t in res.get("toks", []) if t["s"] == s), None)


def mk_sent(seq, prefix, parts):
    """parts: list of (s, pos, r[, unk]) → sentence with toks; a scenario prefix becomes a sym token."""
    toks = []
    if prefix:
        toks.append({"s": prefix, "pos": "sym", "r": ""})
    for p in parts:
        t = {"s": p[0], "pos": p[1], "r": p[2]}
        if len(p) > 3:
            t["unk"] = True
        toks.append(t)
    return {"seq": seq, "text": "".join(t["s"] for t in toks), "toks": toks}


def sample_sents(prefix):
    return [
        mk_sent(0, prefix, [("一", "num", "いち"), ("本", "affix", "ほん"), ("の", "particle", "の"), ("ペン", "noun", "ぺん"), ("。", "sym", "")]),
        mk_sent(1, "", [("18", "num", None, True), ("年", "affix", "ねん"), ("の", "particle", "の"), ("手帳", "noun", "てちょう"), ("。", "sym", "")]),
        mk_sent(2, "", [("quiet", "noun", None, True), ("な", "aux", "な"), ("夜", "noun", "よる"), ("。", "sym", "")]),
    ]


def public_checks():
    """smoke.sh equivalents (that script cannot be run from this session): public pages + 401 without token."""
    c, h, _ = call("GET", "/health")
    check("GET /health 200 status=ok", c == 200 and h["status"] == "ok" and h["auth"]["db"]["calls_ready"], str(c))
    with urllib.request.urlopen(BASE + "/", timeout=30) as r:
        html, csp = r.read().decode(), r.headers.get("Content-Security-Policy", "")
    check("GET / html + CSP", "<html" in html and "default-src 'self'" in csp)
    check("frontend has analyze filter + kindZh", '<option value="analyze">分析</option>' in html and 'analyze: "分析"' in html)
    c, _, _ = call("GET", "/nope")
    check("GET /nope 404", c == 404)
    for m, p in [("POST", "/api/text"), ("POST", "/api/image"), ("POST", "/api/video"), ("POST", "/api/analyze"),
                 ("GET", "/api/models"), ("GET", "/api/limits"), ("GET", "/api/auth/me"), ("GET", "/api/admin/calls?kind=analyze")]:
        c, _, _ = call(m, p, {"prompt": "hi"} if m == "POST" else None)
        check(f"{m} {p} without token -> 401", c == 401, str(c))
    c, _, _ = call("GET", "/api/auth/me", token="garbage.token.here")
    check("garbage token -> 401", c == 401)


# =====================================================================================================
def run_mock():
    public_checks()
    token, uid = create_user("admin", ["--rate-per-minute", "500", "--daily-quota", "500"])
    expect_analyze = 0
    try:
        print("== /health analyze section (mock config: both providers ready)")
        code, h, _ = call("GET", "/health")
        a = h.get("analyze", {})
        check("health 200 + analyze section", code == 200 and "providers" in a, json.dumps(a, ensure_ascii=False))
        check("agnes ready + models", a["providers"]["agnes"] == {"ready": True, "models": ["agnes-3.0-flash", "agnes-2.5-flash"], "strategy": "json_object"})
        check("gemini ready + models", a["providers"]["gemini"] == {"ready": True, "models": ["gemini-flash-lite-latest", "gemini-flash-latest"], "strategy": "json_object+reasoning_effort"})
        check("default/order from config", a["default_provider"] == "agnes" and a["order"] == ["agnes", "gemini"])
        check("no URL in analyze section", "http" not in json.dumps(a))

        print("== regression: text / image / video (mock upstream)")
        c, js, _ = call("POST", "/api/text", {"prompt": "hi"}, token)
        check("text 200 mock reply", c == 200 and js["text"] == "mock reply" and js["model"] == "agnes-2.5-flash", f"{c} {js}")
        c, js, _ = call("POST", "/api/text", {}, token)
        check("text {} -> 400", c == 400, f"{c} {js}")
        c, js, _ = call("POST", "/api/text", {"prompt": "please reject"}, token)
        check("text upstream 400 passthrough", c == 400 and js.get("upstream_status") == 400, f"{c} {js}")
        c, js, _ = call("POST", "/api/text", {"prompt": "boom"}, token)
        check("text upstream 500 -> 502", c == 502 and js.get("upstream_status") == 500, f"{c} {js}")
        c, js, _ = call("POST", "/api/image", {"prompt": "a cat"}, token)
        check("image 200 url", c == 200 and js.get("url", "").startswith("https://example.invalid"), f"{c} {js}")
        c, js, _ = call("POST", "/api/video", {"prompt": "a cat"}, token)
        check("video 200 id+poll", c == 200 and js["id"] == "video_mock1" and js["poll"].startswith("/api/video/status/"), f"{c} {js}")
        c, js2, _ = call("GET", js["poll"], token=token)
        check("video status completed", c == 200 and js2["status"] == "completed", f"{c} {js2}")
        c, js, _ = call("GET", "/api/models", token=token)
        check("models 200", c == 200 and "text" in js["models"], f"{c}")
        u = used(token)
        check("usage: text 2 (reject refunded), image 1, video 1", (u["text"], u["image"], u["video"]) == (2, 1, 1), json.dumps(u))

        print("== /api/analyze: auth + pre-admission validation (400 costs nothing)")
        c, js, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "テスト"})
        check("no token -> 401", c == 401, f"{c}")
        before = used(token)["analyze"]
        for body, code_ in [({}, "invalid_mode"), ({"mode": "raw"}, "invalid_text"), ({"mode": "raw", "text": "x", "model": "agnes-2.5-pro"}, "invalid_model"),
                            ({"mode": "raw", "text": "x", "provider": "openai"}, "invalid_provider"),
                            ({"mode": "raw", "text": "x", "provider": "gemini", "model": "agnes-3.0-flash"}, "invalid_model"),
                            ({"mode": "enrich", "sentences": []}, "invalid_sentences"),
                            ({"mode": "enrich", "sentences": [{"seq": 0, "text": "あい", "toks": [{"s": "あ"}]}]}, "invalid_sentences"),
                            ({"mode": "raw", "text": "あ" * 2001}, "invalid_text")]:
            c, js, _ = call("POST", "/api/analyze", body, token)
            check(f"400 {code_}", c == 400 and js.get("code") == code_, f"{c} {js}")
        check("400s not counted", used(token)["analyze"] == before)

        print("== §11-1 raw normal: both shapes, pos map, ruby")
        c, r, ms = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:json_ok 彼女は静かな図書館で本を読んでいる。"}, token)
        expect_analyze += 1
        print("        " + json.dumps(r, ensure_ascii=False)[:1500])
        check("raw 200 agnes/agnes-3.0-flash attempts=1", c == 200 and r["provider"] == "agnes" and r["model"] == "agnes-3.0-flash" and r["attempts"] == 1 and r["prompt_version"] == "analyze-v1", f"{c}")
        check("toks/tokens same length", len(r["toks"]) == len(r["tokens"]) == 12)
        t = tok(r, "彼女")
        check("彼女 pos=pronoun r=かのじょ ruby whole", t == {"s": "彼女", "pos": "pronoun", "r": "かのじょ", "ruby": [{"t": "彼女", "r": "かのじょ"}]}, json.dumps(t, ensure_ascii=False))
        t = tok(r, "静かな")
        check("静かな pos=adj ruby 静/しず + かな", t and t["pos"] == "adj" and t["ruby"] == [{"t": "静", "r": "しず"}, {"t": "かな"}], json.dumps(t, ensure_ascii=False))
        t = tok(r, "読ん")
        check("読ん verb ruby 読/よ + ん", t and t["pos"] == "verb" and t["ruby"] == [{"t": "読", "r": "よ"}, {"t": "ん"}], json.dumps(t, ensure_ascii=False))
        t = tok(r, "いる")
        check("いる kana: ruby null", t and t["ruby"] is None and t["r"] == "いる")
        t = tok(r, "。")
        check("。 sym r='' ruby null", t == {"s": "。", "pos": "sym", "r": "", "ruby": None})
        check("analyzer view keeps JP labels", r["tokens"][1] == {"word": "彼女", "pos": "代名詞", "furigana": "かのじょ"}, json.dumps(r["tokens"][1], ensure_ascii=False))

        print("== §11-2 repair retry")
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:json_bad_concat 東京へ行く。"}, token)
        expect_analyze += 1
        check("concat error -> repaired, attempts=2", c == 200 and r["attempts"] == 2 and r["trail"] == ["agnes/3.0-flash:bad", "r:ok"], f"{c} {r.get('trail')} {r.get('attempts')}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:json_invalid 東京へ行く。"}, token)
        expect_analyze += 1
        check("invalid JSON -> repaired, attempts=2", c == 200 and r["attempts"] == 2 and r["trail"] == ["agnes/3.0-flash:json", "r:ok"], f"{c} {r.get('trail')}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:bad_always 東京へ行く。"}, token)
        expect_analyze += 1
        check("never valid -> 502 analyze_validation_failed, 4-call cap", c == 502 and r["code"] == "analyze_validation_failed" and r["attempts"] == 4
              and r["trail"] == ["agnes/3.0-flash:bad", "r:bad", "agnes/2.5-flash:bad", "r:bad", "cap"], f"{c} {json.dumps(r, ensure_ascii=False)[:600]}")
        check("502 details name the first difference", any("拼接" in d for d in r.get("details", [])), json.dumps(r.get("details"), ensure_ascii=False)[:300])

        print("== §11-3 fallback: model level first (O1), then provider; call_log trail")
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:403m 東京へ行く。"}, token)
        expect_analyze += 1
        check("403 on agnes-3.0-flash -> agnes-2.5-flash", c == 200 and r["provider"] == "agnes" and r["model"] == "agnes-2.5-flash" and r["trail"] == ["agnes/3.0-flash:403", "agnes/2.5-flash:ok"], f"{c} {r.get('trail')}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:403 東京へ行く。"}, token)
        expect_analyze += 1
        check("agnes 403 x2 -> gemini(mock) ok, attempts=3", c == 200 and r["provider"] == "gemini" and r["model"] == "gemini-flash-lite-latest" and r["attempts"] == 3
              and r["trail"] == ["agnes/3.0-flash:403", "agnes/2.5-flash:403", "gemini/flash-lite-latest:ok"], f"{c} {r.get('trail')}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:500 東京へ行く。"}, token)
        expect_analyze += 1
        check("agnes 500 x2 -> gemini", c == 200 and r["provider"] == "gemini" and r["trail"][:2] == ["agnes/3.0-flash:500", "agnes/2.5-flash:500"], f"{c} {r.get('trail')}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:json_ok 東京へ行く。", "model": "gemini-flash-lite-latest"}, token)
        expect_analyze += 1
        check("requested model gemini-flash-lite-latest (reasoning_effort=minimal accepted by mock)", c == 200 and r["model"] == "gemini-flash-lite-latest", f"{c} {r.get('trail')}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:json_ok 東京へ行く。", "provider": "gemini"}, token)
        expect_analyze += 1
        check("requested provider gemini -> gemini-flash-lite-latest", c == 200 and r["provider"] == "gemini" and r["model"] == "gemini-flash-lite-latest", f"{c}")
        u0 = used(token)["analyze"]
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:403all 東京へ行く。"}, token)
        check("all providers 403 -> 502 upstream_failed, 4-call cap", c == 502 and r["code"] == "upstream_failed" and r["attempts"] == 4, f"{c} {json.dumps(r, ensure_ascii=False)[:400]}")
        check("all-4xx -> refunded", used(token)["analyze"] == u0, f"{u0} -> {used(token)['analyze']}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:400 東京へ行く。"}, token)
        check("param 400 -> no fallback, 502 upstream_rejected, attempts=1", c == 502 and r["code"] == "upstream_rejected" and r["attempts"] == 1 and r["upstream_status"] == 400, f"{c} {json.dumps(r, ensure_ascii=False)[:400]}")
        check("param 400 -> refunded", used(token)["analyze"] == u0)
        u1 = used(token)["analyze"]
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:bad_always 東京。", "provider": "gemini"}, token)
        expect_analyze += 1
        check("validation failure (200s upstream) -> not refunded", c == 502 and used(token)["analyze"] == u1 + 1)

        print("== §11-4 enrich normal, out-of-range interception, chunking, split")
        sents = sample_sents("scenario:json_ok ")
        c, r, ms = call("POST", "/api/analyze", {"mode": "enrich", "sentences": sents}, token)
        expect_analyze += 1
        print("        " + json.dumps(r, ensure_ascii=False)[:1500])
        ok = c == 200 and r["prompt_version"] == "enrich-v2"   # phase 2 (P2R-6): context.full_text and [s["seq"] for s in r["sentences"]] == [0, 1, 2] and all(s["tr"] for s in r["sentences"])
        check("enrich 200, seq set, tr non-empty", ok, f"{c}")
        s1 = r["sentences"][1] if ok else {}
        check("fix for r=null digit token carries tok_i + s", s1.get("fixes") == [{"tok_i": 0, "s": "18", "reading": "じゅうはち"}], json.dumps(s1.get("fixes"), ensure_ascii=False))
        s2 = r["sentences"][2] if ok else {}
        check("Latin unk token excluded from fixes", s2.get("fixes") == [], json.dumps(s2, ensure_ascii=False))
        check("no gloss unless asked", all("gloss" not in s for s in r.get("sentences", [])))
        c, r, _ = call("POST", "/api/analyze", {"mode": "enrich", "sentences": sample_sents("scenario:oob ")}, token)
        expect_analyze += 1
        check("out-of-range i caught, repaired (attempts=2)", c == 200 and r["attempts"] == 2 and r["chunks"][0]["trail"] == ["agnes/3.0-flash:bad", "r:ok"], f"{c} {r.get('chunks')}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "enrich", "sentences": sample_sents("scenario:bad_always ")}, token)
        expect_analyze += 1
        det = json.dumps(r.get("details"), ensure_ascii=False)
        check("always out-of-range -> 502 analyze_validation_failed (chunk cap 3)", c == 502 and r["code"] == "analyze_validation_failed" and r["attempts"] == 3 and "越界" in det, f"{c} {det[:300]}")
        check("failed_seqs lists the chunk", r.get("failed_seqs") == [0, 1, 2], json.dumps(r.get("failed_seqs")))
        many = [mk_sent(i, "scenario:json_ok " if i == 0 else "", [("今日", "noun", "きょう"), ("は", "particle", "は"), ("晴れ", "noun", "はれ"), ("。", "sym", "")]) for i in range(45)]
        c, r, ms = call("POST", "/api/analyze", {"mode": "enrich", "sentences": many}, token)
        expect_analyze += 1
        check("45 sentences -> 3 chunks (20/20/5), one admission", c == 200 and [ch["seq"] for ch in r["chunks"]] == ["0-19", "20-39", "40-44"] and r["attempts"] == 3 and len(r["sentences"]) == 45,
              f"{c} {[ch.get('seq') for ch in r.get('chunks', [])]} {ms}ms")
        big = [mk_sent(i, "scenario:json_ok " if i == 0 else "", [("雪", "noun", "ゆき"), ("。", "sym", "")]) for i in range(200)]
        c, r, ms = call("POST", "/api/analyze", {"mode": "enrich", "sentences": big}, token)
        expect_analyze += 1
        check("200 sentences (max) -> 10 chunks, parallel, one admission", c == 200 and len(r["chunks"]) == 10 and r["attempts"] == 10 and len(r["sentences"]) == 200, f"{c} {ms}ms")
        ls = [mk_sent(i, "scenario:length " if i == 0 else "", [("雨", "noun", "あめ"), ("。", "sym", "")]) for i in range(4)]
        # every chunk containing the scenario sentence and > 1 sentence answers finish_reason=length
        c, r, _ = call("POST", "/api/analyze", {"mode": "enrich", "sentences": ls}, token)
        expect_analyze += 1
        chs = [(ch["chunk"], ch["seq"], ch["result"]) for ch in r.get("chunks", [])]
        check("finish_reason=length -> split in halves until it fits", c == 200 and len(r["sentences"]) == 4 and ("c0", "0-3", "split") in chs and ("c0a", "0-1", "split") in chs
              and ("c0aa", "0", "ok") in chs and ("c0b", "2-3", "ok") in chs, f"{c} {chs}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "enrich", "sentences": sample_sents("scenario:403 ")}, token)
        expect_analyze += 1
        check("enrich chunk fallback agnes 403 -> gemini", c == 200 and r["provider"] == "gemini" and "[gemini:gemini-flash-lite-latest]" in r["sentences"][0]["tr"], f"{c} {r.get('chunks')}")
        c, r, _ = call("POST", "/api/analyze", {"mode": "enrich", "sentences": sample_sents("scenario:json_ok "), "gloss": True}, token)
        expect_analyze += 1
        check("gloss:true accepted (mock gives none -> passthrough nothing)", c == 200)

        print("== usage accounting")
        u = used(token)
        check(f"used.analyze == admitted - refunded ({expect_analyze})", u["analyze"] == expect_analyze, json.dumps(u))
        check("total includes analyze", u["total"] == u["text"] + u["image"] + u["video"] + u["analyze"])
        _, lim, _ = call("GET", "/api/limits", token=token)
        check("counted_endpoints has POST /api/analyze", "POST /api/analyze" in lim["counted_endpoints"])
        _, me, _ = call("GET", "/api/auth/me", token=token)
        check("/me usage shows analyze", "analyze" in me["usage"]["used"])

        print("== §11-5 quota 429 recorded with kind=analyze")
        total = u["total"]
        psql("UPDATE agnes_auth.users SET daily_quota = :v2 WHERE username = :'v1';", USER, str(total))
        c, r, _ = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:json_ok 東京。"}, token)
        check("over daily quota -> 429 daily_quota_exceeded", c == 429 and r["code"] == "daily_quota_exceeded", f"{c} {r}")
        psql("UPDATE agnes_auth.users SET daily_quota = 500 WHERE username = :'v1';", USER)

        print("== call_log (admin view) kind=analyze")
        c, js, _ = call("GET", f"/api/admin/calls?kind=analyze&user_id={uid}&limit=100", token=token)
        rows = js["calls"] if c == 200 else []
        check("admin calls?kind=analyze 200, only analyze rows", c == 200 and rows and all(x["kind"] == "analyze" for x in rows), f"{c} n={len(rows)}")
        c2, _, _ = call("GET", "/api/admin/calls?kind=bogus", token=token)
        check("admin kind whitelist rejects bogus", c2 == 400)
        for x in reversed(rows):
            print(f"        #{x['id']} {x['outcome']:<26} {x['http_status']} us={x['upstream_status']} {x['model']} | {x['prompt_preview'][:40]!r} | {x['detail']}")
        q = [x for x in rows if x["outcome"] == "daily_quota_exceeded"]
        check("429 row present (kind=analyze, scope total)", len(q) == 1 and q[0]["detail"] == "total", json.dumps(q, ensure_ascii=False))
        fb = [x for x in rows if (x["prompt_preview"] or "").startswith("scenario:403 東京")]
        check("fallback row: model=gemini:gemini-flash-lite-latest, trail in detail", len(fb) == 1 and fb[0]["model"] == "gemini:gemini-flash-lite-latest"
              and "fb=agnes/3.0-flash:403>agnes/2.5-flash:403>gemini/flash-lite-latest:ok" in fb[0]["detail"], json.dumps(fb, ensure_ascii=False))
        rf = [x for x in rows if (x["prompt_preview"] or "").startswith("scenario:403all")]
        check("all-4xx row marked refund", len(rf) == 1 and rf[0]["detail"].startswith("refund;") and rf[0]["outcome"] == "upstream_failed", json.dumps(rf, ensure_ascii=False))
        en = [x for x in rows if x["detail"] and x["detail"].startswith("m=enrich;n=45")]
        check("enrich preview = first sentence; detail lists chunks", len(en) == 1 and en[0]["prompt_preview"].startswith("scenario:json_ok 今日は晴れ。") and ";c2[40-44]=" in en[0]["detail"], json.dumps(en, ensure_ascii=False)[:400])
        check("detail can exceed 300 chars (cap 1000)", any(len(x["detail"] or "") > 300 for x in rows), str(max(len(x["detail"] or "") for x in rows)))
    finally:
        check("test account cleaned up", delete_user())


# =====================================================================================================
REAL_ENRICH_SQL = """
SELECT json_agg(json_build_object('seq', x.seq, 'text', x.text, 'toks', x.toks) ORDER BY x.seq)
  FROM (SELECT s.seq, s.text, s.toks FROM reading.sentences s
          JOIN reading.articles a ON a.share_id = s.article_id AND s.version = a.current_version
         WHERE a.deleted_at IS NULL
           AND a.share_id = 'ce605b3d6e'
         ORDER BY s.seq LIMIT 3) x;
"""


def run_real():
    token, uid = create_user("user", ["--rate-per-minute", "30", "--daily-quota", "20"])
    try:
        print("== /health analyze section (real config)")
        _, h, _ = call("GET", "/health")
        print("        " + json.dumps(h["analyze"], ensure_ascii=False))
        check("agnes ready, gemini not ready (key empty)", h["analyze"]["providers"]["agnes"]["ready"] and not h["analyze"]["providers"]["gemini"]["ready"])
        for text in ["彼女は静かな図書館で本を読んでいる。", "一本のペンと十八年の手帳。"]:
            print(f"== real raw: {text}")
            c, r, ms = call("POST", "/api/analyze", {"mode": "raw", "text": text}, token)
            print(f"        HTTP {c} in {ms} ms")
            print(json.dumps(r, ensure_ascii=False, indent=1))
            check(f"raw 200 ({ms} ms)", c == 200)
        sents = json.loads(psql(REAL_ENRICH_SQL))
        print(f"== real enrich: {len(sents)} sentences from reading.sentences ce605b3d6e current version, seq 0-2 (read-only SELECT)")
        print(json.dumps([{k: s[k] for k in ("seq", "text")} for s in sents], ensure_ascii=False))
        c, r, ms = call("POST", "/api/analyze", {"mode": "enrich", "sentences": sents}, token)
        print(f"        HTTP {c} in {ms} ms")
        print(json.dumps(r, ensure_ascii=False, indent=1))
        check(f"enrich 200 ({ms} ms)", c == 200)
        _, js, _ = call("GET", "/api/limits", token=token)
        print("        usage: " + json.dumps(js["usage"]["used"]))
        rows = psql("SELECT kind, model, outcome, http_status, duration_ms, prompt_preview, detail FROM agnes_auth.call_log WHERE username = :'v1' ORDER BY id;", USER)
        print("        call_log:\n" + "\n".join("          " + x for x in rows.splitlines()))
    finally:
        check("test account cleaned up", delete_user())


def run_real_one():
    """One real raw call (verifies the cross-token sound-change prompt rule)."""
    token, uid = create_user("user", ["--rate-per-minute", "30", "--daily-quota", "5"])
    try:
        c, r, ms = call("POST", "/api/analyze", {"mode": "raw", "text": "一本のペンと十八年の手帳。"}, token)
        print(f"        HTTP {c} in {ms} ms")
        print(json.dumps(r, ensure_ascii=False))
        check(f"raw 200 ({ms} ms)", c == 200)
    finally:
        check("test account cleaned up", delete_user())


def run_timeout():
    """Needs ANALYZE_TIMEOUT_S=5 in an env file the dev instance reads (hot reload, no restart)."""
    token, uid = create_user("user", ["--rate-per-minute", "30", "--daily-quota", "20"])
    try:
        c, r, ms = call("POST", "/api/analyze", {"mode": "raw", "text": "scenario:sleep:8 東京へ行く。"}, token)
        check("agnes timeouts -> gemini", c == 200 and r["provider"] == "gemini"
              and r["trail"] == ["agnes/3.0-flash:timeout", "agnes/2.5-flash:timeout", "gemini/flash-lite-latest:ok"], f"{c} {r.get('trail')} {ms}ms")
        check("per-call timeout 5 s honoured (2 x ~5 s)", 9000 < ms < 15000, f"{ms}ms")
        check("timeouts are not refunded", used(token)["analyze"] == 1)
    finally:
        check("test account cleaned up", delete_user())


def run_prod():
    print(f"== production smoke {BASE}")
    c, h, _ = call("GET", "/health")
    check("/health 200 with analyze section", c == 200 and "analyze" in h, json.dumps(h.get("analyze"), ensure_ascii=False))
    check("agnes ready, gemini not ready", h["analyze"]["providers"]["agnes"]["ready"] and not h["analyze"]["providers"]["gemini"]["ready"])
    public_checks()
    token, uid = create_user("admin", ["--rate-per-minute", "10", "--daily-quota", "5"])
    try:
        c, r, ms = call("POST", "/api/analyze", {"mode": "raw", "text": "彼女は静かな図書館で本を読んでいる。"}, token)
        print(f"        HTTP {c} in {ms} ms")
        print(json.dumps(r, ensure_ascii=False, indent=1))
        check("real raw 200", c == 200)
        c, js, _ = call("GET", f"/api/admin/calls?kind=analyze&user_id={uid}&limit=5", token=token)
        check("admin filter kind=analyze returns the call", c == 200 and len(js["calls"]) == 1, json.dumps(js, ensure_ascii=False)[:500])
    finally:
        check("test account cleaned up", delete_user())


{"mock": run_mock, "real": run_real, "real1": run_real_one, "prod": run_prod, "timeout": run_timeout}[mode]()
print(f"== {PASS} passed, {FAIL} failed")
sys.exit(0 if FAIL == 0 else 1)
