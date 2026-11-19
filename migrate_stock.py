"""P1 存量迁移：两边 SQLite shares（只读副本）→ 77B db77database 的 reading schema。

用法：p0/venv/bin/python impl/migrate_stock.py [--dry-run]
规则（task_p1.md / 定版方案）：
  - 保留 share_id、added_by、added_at、title、lang、rate；
  - 按规范化原文 content_hash 全库去重（第 19 条）：同一原文以 added_at 最早者为正主，其余 ID 进 alt_ids；
    库里已有同哈希文章（含软删除的）时不新增，只把未登记的 ID 补进 alt_ids；
  - 新文章：复用 p0/fugashi_run.py 的分句＋fugashi 生成 v1 sentences（tr 为空），
    versions 写 v1、tool.kind=import；articles.status=partial；
  - 测试文 d2a28f9d78 照常迁入后软删除（第 11 条）；
  - 句子拼接校验（① 句子按序拼接==原文、② toks 拼接==句子）全过才提交；任何一条不过整批不写；
    事务内再核一次 DB 生成列 content_hash 与本地计算是否一致，不一致即回滚；
  - 写库经本机 psql（peer 认证），整批一个事务；可重复执行（幂等）。
"""
import datetime as dt
import hashlib
import json
import os
import sqlite3
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
P0 = os.path.join(os.path.dirname(HERE), "p0")
sys.path.insert(0, P0)
import fugashi  # noqa: E402
from fugashi_run import normalize, split_sentences, tok_of  # noqa: E402
from check import check_concat  # noqa: E402

SOURCES = [("77b", os.path.join(HERE, "data", "ttsweb_77b.db")),
           ("ddpc", os.path.join(HERE, "data", "ttsweb_ddpc.db"))]
TEST_IDS = {"d2a28f9d78"}
DB = "db77database"
TAG = "$mig_payload$"


def psql(sql, *, single_tx=False):
    args = ["psql", "-d", DB, "-v", "ON_ERROR_STOP=1", "-X", "-q", "-At"]
    if single_tx:
        args.append("-1")
    r = subprocess.run(args, input=sql, text=True, capture_output=True)
    if r.returncode:
        raise RuntimeError(f"psql 失败（事务已回滚）：{r.stderr.strip()}")
    return r.stdout


def canon(text):
    return normalize(text).strip()


def sha(t):
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def load_sources():
    rows = []
    for site, path in SOURCES:
        con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
        for sid, text, lang, rate, title, by, at in con.execute(
                "SELECT id, text, lang, rate, title, added_by, added_at FROM shares"):
            norm = canon(text)
            rows.append({"share_id": sid, "site": site, "raw_text": text, "text": norm,
                         "normalized_changed": norm != text, "hash": sha(norm),
                         "lang": (lang or "ja").lower(), "rate": rate or "-20%",
                         "title": title, "added_by": by, "added_at": at,
                         "added_at_dt": dt.datetime.fromisoformat(at)})
        con.close()
    return rows


def db_state():
    out = psql("SELECT coalesce(json_agg(json_build_object('share_id', share_id, 'hash', content_hash,"
               " 'alt_ids', alt_ids, 'deleted', deleted_at IS NOT NULL)), '[]') FROM reading.articles;")
    return json.loads(out)


def tokenize(tagger, st):
    """fugashi 分词；MeCab 会吞掉词间空白（存于 word.white_space），这里补回为空白符号 token，
    保证 toks 的 s 按序拼接 == 句子原文（P1 迁移时在 ce605b3d6e「quiet quitting」上发现，P0 未覆盖）。"""
    toks = []
    for w in tagger(st):
        if w.white_space:
            toks.append({"s": w.white_space, "l": w.white_space, "pos": "sym", "pd": "空白",
                         "r": "", "ruby": None})
        toks.append(tok_of(w))
    return toks


def build_sentences(text, tagger):
    sents = []
    for seq, (a, b, para) in enumerate(split_sentences(text)):
        st = text[a:b]
        toks = tokenize(tagger, st)
        n = len(st)
        sents.append({
            "seq": seq, "para": para, "text": st, "char_start": a, "char_end": b,
            "len_class": "s" if n <= 20 else ("m" if n <= 45 else "l"),
            "toks": toks,
            "kana": "".join(t["r"] if t["r"] is not None else t["s"] for t in toks),
            "lemmas": sorted({t["l"] for t in toks if t["pos"] != "sym"}),
        })
    return sents


def validate(art):
    errs = check_concat(art["text"], art["sentences"])                          # ①
    for s in art["sentences"]:
        if art["text"][s["char_start"]:s["char_end"]] != s["text"]:
            errs.append({"code": "RANGE_MISMATCH", "seq": s["seq"]})
        if "".join(t["s"] for t in s["toks"]) != s["text"]:                    # ②
            errs.append({"code": "TOKS_CONCAT_MISMATCH", "seq": s["seq"]})
    return errs


def main(dry_run=False):
    src = load_sources()
    groups = {}
    for r in src:
        groups.setdefault(r["hash"], []).append(r)

    state = db_state()
    by_hash = {a["hash"]: a for a in state}
    known_ids = {a["share_id"]: a["hash"] for a in state}
    for a in state:
        for x in a["alt_ids"]:
            known_ids[x] = a["hash"]

    tagger = fugashi.Tagger()
    dict_name = tagger.dictionary_info[0]["filename"].split("/site-packages/")[-1]
    now = dt.datetime.now(dt.timezone(dt.timedelta(hours=9))).isoformat(timespec="seconds")

    new_articles, alt_updates, skipped, problems = [], [], [], []
    for h, members in groups.items():
        members.sort(key=lambda r: r["added_at_dt"])
        for m in members:                                   # ID 撞车：同 ID 不同原文
            if m["share_id"] in known_ids and known_ids[m["share_id"]] != h:
                problems.append(f"ID 冲突：{m['share_id']} 在库中对应另一原文")
        if h in by_hash:                                    # 已迁：只补未登记的别名
            master = by_hash[h]
            registered = {master["share_id"], *master["alt_ids"]}
            extra = [m["share_id"] for m in members if m["share_id"] not in registered]
            if extra:
                alt_updates.append({"share_id": master["share_id"], "add": extra})
            skipped.append(master["share_id"])
            continue
        master, others = members[0], members[1:]
        art = {
            "share_id": master["share_id"], "alt_ids": [o["share_id"] for o in others],
            "text": master["text"], "lang": master["lang"], "rate": master["rate"],
            "title": master["title"], "added_by": master["added_by"], "added_at": master["added_at"],
            "status": "partial", "current_version": 1,
            "deleted_at": now if master["share_id"] in TEST_IDS else None,
            "hash": h,
            "sources": [{"site": m["site"], "share_id": m["share_id"], "added_at": m["added_at"],
                         "normalized_changed": m["normalized_changed"]} for m in members],
        }
        art["sentences"] = build_sentences(art["text"], tagger)
        errs = validate(art)
        if errs:
            problems.append(f"{art['share_id']} 拼接校验不过：{errs[:3]}")
        new_articles.append(art)

    print(f"源头：77b={sum(r['site']=='77b' for r in src)} ddpc={sum(r['site']=='ddpc' for r in src)} "
          f"合计={len(src)}；去重后原文={len(groups)}；词典={dict_name}")
    for r in src:
        if r["normalized_changed"]:
            print(f"  注意：{r['share_id']} 规范化改动了原文（已按规范化文本入库）")
    print(f"本次计划：新增 {len(new_articles)}，补别名 {len(alt_updates)}，已存在跳过 {len(skipped)}")
    if problems:
        print("校验/冲突问题，整批不写：")
        for p in problems:
            print("  -", p)
        return 2
    if dry_run or (not new_articles and not alt_updates):
        print("无写入（dry-run 或幂等命中）。")
        return 0

    payload = {
        "articles": [{k: a[k] for k in ("share_id", "alt_ids", "text", "lang", "rate", "title", "added_by",
                                        "added_at", "status", "current_version", "deleted_at", "hash")}
                     for a in new_articles],
        "versions": [{"article_id": a["share_id"], "version": 1, "created_by": "migrate_stock",
                      "note": "P1 存量迁移（原样迁入，tr 待驻地补）",
                      "tool": {"kind": "import", "splitter": "p0-v1",
                               "tokenizer": "fugashi " + getattr(fugashi, "__version__", "?"),
                               "dict": dict_name, "sources": a["sources"]}}
                     for a in new_articles],
        "sentences": [dict(s, article_id=a["share_id"], version=1, toks=s["toks"])
                      for a in new_articles for s in a["sentences"]],
        "alt_updates": alt_updates,
    }
    blob = json.dumps(payload, ensure_ascii=False)
    assert TAG not in blob
    sql = f"""
SET CONSTRAINTS ALL DEFERRED;
CREATE TEMP TABLE _p ON COMMIT DROP AS SELECT {TAG}{blob}{TAG}::jsonb AS j;
INSERT INTO reading.articles (share_id, alt_ids, text, lang, rate, title, added_by, added_at,
                              status, current_version, deleted_at)
SELECT share_id, alt_ids, text, lang, rate, title, added_by, added_at, status, current_version, deleted_at
FROM _p, jsonb_to_recordset(j->'articles') AS x(share_id text, alt_ids text[], text text, lang text, rate text,
     title text, added_by text, added_at timestamptz, status text, current_version int, deleted_at timestamptz);
INSERT INTO reading.versions (article_id, version, tool, note, created_by)
SELECT article_id, version, tool, note, created_by
FROM _p, jsonb_to_recordset(j->'versions') AS x(article_id text, version int, tool jsonb, note text, created_by text);
INSERT INTO reading.sentences (article_id, version, seq, para, text, char_start, char_end, len_class, toks, kana, lemmas)
SELECT article_id, version, seq, para, text, char_start, char_end, len_class, toks, kana, lemmas
FROM _p, jsonb_to_recordset(j->'sentences') AS x(article_id text, version int, seq int, para int, text text,
     char_start int, char_end int, len_class text, toks jsonb, kana text, lemmas text[]);
UPDATE reading.articles a SET alt_ids = a.alt_ids || ARRAY(SELECT jsonb_array_elements_text(u->'add')),
                              updated_at = now()
FROM _p, jsonb_array_elements(j->'alt_updates') AS u WHERE a.share_id = u->>'share_id';
DO $chk$
DECLARE bad int;
BEGIN
  SELECT count(*) INTO bad
  FROM _p, jsonb_to_recordset(j->'articles') AS x(share_id text, hash text)
  JOIN reading.articles a USING (share_id) WHERE a.content_hash <> x.hash;
  IF bad > 0 THEN RAISE EXCEPTION '库内 content_hash 与本地计算不一致：% 条', bad; END IF;
  SELECT count(*) INTO bad FROM reading.sentences s JOIN reading.articles a ON a.share_id = s.article_id
  WHERE substr(a.text, s.char_start + 1, s.char_end - s.char_start) <> s.text;
  IF bad > 0 THEN RAISE EXCEPTION '库内句子区间与原文不符：% 句', bad; END IF;
END $chk$;
"""
    psql(sql, single_tx=True)
    print(f"已提交：新增文章 {len(new_articles)}（句子 {len(payload['sentences'])}），补别名 {len(alt_updates)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(dry_run="--dry-run" in sys.argv))
