#!/usr/bin/env python3
"""2026-11-20 delete/edit: TtsWeb contract against the DEV copy (tts-redesign/impl/deledit, :8796, DB agnes_p2dev).
Never touches 8791 / db77database.

  python3 tests/p2_stack.py ttsweb
  python3 tests/de_ttswebdev.py
"""
import json
import secrets
import subprocess
import sys
import urllib.error
import urllib.request

TTS = "http://127.0.0.1:8796"
DB = "agnes_p2dev"
SEC = "/data/workspace/tts-redesign/impl/phase2/dev_secrets/"
SUF = secrets.token_hex(3)
PASS = FAIL = 0


def check(name, ok, info=""):
    global PASS, FAIL
    PASS, FAIL = (PASS + 1, FAIL) if ok else (PASS, FAIL + 1)
    print(("  ok    " if ok else "  FAIL  ") + name + (("  -> " + str(info)[:400]) if info and not ok else ""), flush=True)


def http(method, url, body=None, token=None):
    req = urllib.request.Request(url, data=None if body is None else json.dumps(body, ensure_ascii=False).encode(), method=method)
    if body is not None:
        req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            code, raw = r.status, r.read()
    except urllib.error.HTTPError as e:
        code, raw = e.code, e.read()
    try:
        return code, json.loads(raw) if raw else None
    except ValueError:
        return code, {"_raw": raw[:200]}


def psql(sql):
    return subprocess.run(["psql", "-d", DB, "-v", "ON_ERROR_STOP=1", "-Atq"], input=sql, capture_output=True, text=True,
                          check=True).stdout.strip()


def main():
    w = open(SEC + "agnes-service.txt").read().strip()
    rd = open(SEC + "reader.txt").read().strip()
    c, js = http("POST", TTS + "/share", {"text": f"削除テスト{SUF}の記事です。二文目です。", "title": "削除前", "by": "web",
                                          "added_via": "web"}, w)
    check("share a test article", c == 200 and js.get("id"), (c, js))
    sid = js["id"]
    c, a = http("GET", f"{TTS}/api/v1/articles/{sid}", token=rd)
    v0 = a["article"]["current_version"]

    print("== PATCH (title / lang only)")
    c, js = http("PATCH", f"{TTS}/api/v1/articles/{sid}", {"title": "削除後タイトル"}, rd)
    check("PATCH with read token -> 403", c == 403, (c, js))
    c, js = http("PATCH", f"{TTS}/api/v1/articles/{sid}", {"title": "x"})
    check("PATCH without token -> 401", c == 401, (c, js))
    c, js = http("PATCH", f"{TTS}/api/v1/articles/{sid}", {"text": "別の本文"}, w)
    check("PATCH text -> 400 BODY_INVALID", c == 400 and js["error"]["code"] == "BODY_INVALID", (c, js))
    c, js = http("PATCH", f"{TTS}/api/v1/articles/{sid}", {}, w)
    check("PATCH empty -> 400", c == 400, (c, js))
    c, js = http("PATCH", f"{TTS}/api/v1/articles/{sid}", {"lang": "fr"}, w)
    check("PATCH lang=fr -> 400", c == 400, (c, js))
    c, js = http("PATCH", f"{TTS}/api/v1/articles/{sid}", {"title": "新しいタイトル", "lang": "ZH"}, w)
    check("PATCH title+lang -> 200", c == 200 and js["article"]["title"] == "新しいタイトル" and js["article"]["lang"] == "zh", (c, js))
    c, a = http("GET", f"{TTS}/api/v1/articles/{sid}", token=rd)
    check("PATCH did not bump the version / text", a["article"]["current_version"] == v0 and "削除テスト" in a["article"]["text"], a)
    vers = psql(f"SELECT count(*) FROM reading.versions WHERE article_id = '{sid}'")
    check("no new versions row", vers == "1", vers)
    c, js = http("PATCH", f"{TTS}/api/v1/articles/zzzz", {"title": "x"}, w)
    check("PATCH bad id -> 404", c == 404, (c, js))
    c, js = http("PATCH", f"{TTS}/api/v1/articles/0000000000", {"title": "x"}, w)
    check("PATCH unknown id -> 404", c == 404, (c, js))
    c, js = http("PATCH", f"{TTS}/api/v1/articles/{sid}", {"lang": "ja"}, w)

    print("== list fields")
    c, js = http("GET", TTS + "/api/v1/articles", token=rd)
    hit = next((x for x in js["articles"] if x["share_id"] == sid), None)
    check("/api/v1/articles has the article with sentences count", hit and hit.get("sentences") == 2 and "deleted" not in hit, hit)
    since = psql("SELECT (now() - interval '1 minute')::text")

    print("== DELETE (soft)")
    c, js = http("DELETE", f"{TTS}/api/v1/articles/{sid}", token=rd)
    check("DELETE with read token -> 403", c == 403, (c, js))
    c, js = http("DELETE", f"{TTS}/api/v1/articles/{sid}")
    check("DELETE without token -> 401", c == 401, (c, js))
    c, js = http("DELETE", f"{TTS}/api/v1/articles/{sid}", token=w)
    check("DELETE -> 200 ok + deleted_at", c == 200 and js["ok"] and js["share_id"] == sid and js.get("deleted_at"), (c, js))
    row = psql(f"SELECT (deleted_at IS NOT NULL)::text || '|' || (SELECT count(*) FROM reading.sentences WHERE article_id = '{sid}') "
               f"FROM reading.articles WHERE share_id = '{sid}'")
    check("row kept (soft): deleted_at set, sentences still there", row.startswith("true|") and row != "true|0", row)
    c, js = http("DELETE", f"{TTS}/api/v1/articles/{sid}", token=w)
    check("DELETE again -> 410", c == 410 and js["error"]["code"] == "NOT_FOUND", (c, js))
    c, js = http("PATCH", f"{TTS}/api/v1/articles/{sid}", {"title": "x"}, w)
    check("PATCH deleted -> 410", c == 410, (c, js))
    c, js = http("GET", f"{TTS}/t/{sid}")
    check("/t -> 404", c == 404, c)
    c, js = http("GET", f"{TTS}/v3/t/{sid}")
    check("/v3/t -> 404", c == 404, c)
    c, js = http("GET", f"{TTS}/api/v1/articles/{sid}", token=rd)
    check("/api/v1/articles/{id} -> 410", c == 410, c)
    c, js = http("GET", TTS + "/v3/articles")
    check("/v3/articles no longer lists it", c == 200 and all(x["share_id"] != sid for x in js["articles"]), c)
    c, js = http("GET", TTS + "/api/v1/articles", token=rd)
    check("/api/v1/articles (full, = ddPC preheat) no longer lists it", all(x["share_id"] != sid for x in js["articles"]))
    c, js = http("GET", TTS + "/api/v1/articles?since=" + urllib.request.quote(since), token=rd)
    check("since without include_deleted: no tombstone", all(x["share_id"] != sid for x in js["articles"]), js)
    c, js = http("GET", TTS + "/api/v1/articles?include_deleted=1&since=" + urllib.request.quote(since), token=rd)
    tomb = next((x for x in js["articles"] if x["share_id"] == sid), None)
    check("since + include_deleted=1: tombstone {deleted:true, status:'deleted'}", tomb and tomb["deleted"] is True
          and tomb["status"] == "deleted" and tomb.get("deleted_at") and "text" not in tomb, tomb)
    c, js = http("GET", TTS + "/api/v1/articles?include_deleted=1", token=rd)
    check("include_deleted without since: no tombstones (full list unchanged)", all(not x.get("deleted") for x in js["articles"]))
    c, js = http("POST", TTS + "/share", {"text": f"削除テスト{SUF}の記事です。二文目です。", "by": "web", "added_via": "web"}, w)
    check("same text can be shared again after delete (new id)", c == 200 and js["id"] != sid, (c, js))
    http("DELETE", f"{TTS}/api/v1/articles/{js['id']}", token=w)


try:
    main()
finally:
    print(f"== {PASS} passed, {FAIL} failed")
sys.exit(0 if FAIL == 0 else 1)
