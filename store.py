"""reading schema 存取（PG 优先，psycopg v3 同步连接，由调用方放进线程执行）＋ 本地 SQLite 回落。

- 角色 reading_rw，口令从文件（config.pg.password_file）或环境变量 READING_RW_PASSWORD 读取，不进代码与配置明文；
- 任何连接失败/查询异常都向上抛 PgUnavailable，由服务决定回落 SQLite 或返回 RETRY_LATER；
- 写入单事务；去重竞争以 content_hash 部分唯一索引兜底。
"""
import datetime as dt
import logging
import os
import secrets
import sqlite3
import threading

import psycopg
from psycopg.types.json import Jsonb

from ingest import ApiError

log = logging.getLogger("muse-tts")
JST = dt.timezone(dt.timedelta(hours=9))


class PgUnavailable(Exception):
    pass


class Store:
    def __init__(self, cfg, base_dir):
        pg = cfg["pg"]
        pw = os.environ.get("READING_RW_PASSWORD")
        if not pw:
            pw_file = pg["password_file"]
            if not os.path.isabs(pw_file):
                pw_file = os.path.normpath(os.path.join(base_dir, pw_file))
            pw = open(pw_file, encoding="utf-8").read().strip()
        self._conninfo = dict(host=pg.get("host", "127.0.0.1"), port=pg.get("port", 5432),
                              dbname=pg.get("dbname", "db77database"), user=pg.get("user", "reading_rw"),
                              password=pw, connect_timeout=pg.get("connect_timeout", 3),
                              application_name="tts-web-reading")
        sp = cfg["sqlite_path"]
        self.sqlite_path = sp if os.path.isabs(sp) else os.path.normpath(os.path.join(base_dir, sp))
        self._sqlite_lock = threading.Lock()

    # ------------------------------------------------------------------ PG 基础
    def _conn(self):
        try:
            return psycopg.connect(**self._conninfo)
        except Exception as exc:  # noqa: BLE001
            raise PgUnavailable(repr(exc)) from exc

    def _run(self, fn):
        conn = self._conn()
        try:
            with conn:
                return fn(conn)
        except (ApiError, PgUnavailable):
            raise
        except psycopg.errors.UniqueViolation:
            raise
        except psycopg.Error as exc:
            raise PgUnavailable(repr(exc)) from exc
        finally:
            conn.close()

    # ------------------------------------------------------------------ 读取
    def get_legacy(self, sid):
        """/t/<id>：按 share_id 或 alt_ids 命中未删除文章，返回与现行完全一致的 dict；无则 None。"""
        def q(conn):
            return conn.execute(
                "SELECT text, lang, rate, title, added_by, added_at FROM reading.articles "
                "WHERE deleted_at IS NULL AND (share_id = %s OR %s = ANY(alt_ids)) "
                "ORDER BY (share_id = %s) DESC LIMIT 1", (sid, sid, sid)).fetchone()
        row = self._run(q)
        if row is None:
            return None
        item = {"text": row[0], "lang": row[1], "rate": row[2]}
        if row[3]:
            item["title"] = row[3]
        if row[4]:
            item["added_by"] = row[4]
        if row[5]:
            item["added_at"] = row[5].astimezone(JST).isoformat(timespec="seconds")
        return item

    def get_with_sentences(self, sid):
        """P4 /v3/t/<id>（加性）：旧 /t 字段 ＋ 当前版本 sentences（页面渲染用的字段）；未命中/已删除 → None。"""
        legacy = self.get_legacy(sid)
        if legacy is None:
            return None

        def q(conn):
            return conn.execute(
                "SELECT s.seq, s.para, s.text, s.tr, s.kana, s.toks, s.len_class, a.current_version, a.share_id "
                "FROM reading.articles a JOIN reading.sentences s "
                "  ON s.article_id = a.share_id AND s.version = a.current_version "
                "WHERE a.deleted_at IS NULL AND (a.share_id = %s OR %s = ANY(a.alt_ids)) "
                "  AND a.share_id = (SELECT share_id FROM reading.articles WHERE deleted_at IS NULL "
                "      AND (share_id = %s OR %s = ANY(alt_ids)) ORDER BY (share_id = %s) DESC LIMIT 1) "
                "ORDER BY s.seq", (sid, sid, sid, sid, sid)).fetchall()
        rows = self._run(q)
        if rows:
            legacy["share_id"], legacy["version"] = rows[0][8], rows[0][7]
            legacy["sentences"] = [{"seq": r[0], "para": r[1], "text": r[2], "tr": r[3], "kana": r[4],
                                    "toks": r[5], "len_class": r[6]} for r in rows]
        return legacy

    def list_public(self, limit=500):
        """P4b /v3/articles（加性、免令牌）：只回公开字段；只含未删除且 status=ready；入库时间倒序。
        不回正文、译文、tokens。"""
        def q(conn):
            return conn.execute(
                "SELECT a.share_id, a.title, a.added_at, a.added_by, a.lang, "
                "  (SELECT count(*) FROM reading.sentences s "
                "    WHERE s.article_id = a.share_id AND s.version = a.current_version) AS sentences "
                "FROM reading.articles a WHERE a.deleted_at IS NULL AND a.status = 'ready' "
                "ORDER BY a.added_at DESC LIMIT %s", (limit,)).fetchall()
        cols = ("share_id", "title", "added_at", "added_by", "lang", "sentences")
        return [self._jsonable(dict(zip(cols, r))) for r in self._run(q)]

    ART_COLS = ("share_id", "alt_ids", "title", "lang", "rate", "added_by", "added_at", "status",
                "current_version", "content_hash", "created_at", "updated_at")

    @staticmethod
    def _jsonable(d):
        return {k: (v.astimezone(JST).isoformat(timespec="seconds") if isinstance(v, dt.datetime) else v)
                for k, v in d.items()}

    def list_articles(self, since=None, limit=500):
        def q(conn):
            sql = f"SELECT {', '.join(self.ART_COLS)} FROM reading.articles WHERE deleted_at IS NULL"
            args = []
            if since:
                sql += " AND updated_at > %s"
                args.append(since)
            sql += " ORDER BY updated_at DESC LIMIT %s"
            args.append(limit)
            return conn.execute(sql, args).fetchall()
        return [self._jsonable(dict(zip(self.ART_COLS, r))) for r in self._run(q)]

    def get_article(self, sid):
        def q(conn):
            row = conn.execute(
                f"SELECT {', '.join(self.ART_COLS)}, text, deleted_at FROM reading.articles "
                "WHERE share_id = %s OR %s = ANY(alt_ids) ORDER BY (share_id = %s) DESC LIMIT 1",
                (sid, sid, sid)).fetchone()
            if row is None:
                raise ApiError("NOT_FOUND", "文章不存在", "检查短链 ID", {"id": sid})
            art = dict(zip(self.ART_COLS + ("text", "deleted_at"), row))
            if art.pop("deleted_at"):
                raise ApiError("NOT_FOUND", "文章已删除", "报告用户", {"id": sid}, status=410)
            cols = ("seq", "para", "text", "char_start", "char_end", "len_class", "tr", "tr_by", "tr_via",
                    "tr_model", "toks", "kana", "fixes", "content_hash")
            rows = conn.execute(
                f"SELECT {', '.join(cols)} FROM reading.sentences WHERE article_id = %s AND version = %s "
                "ORDER BY seq", (art["share_id"], art["current_version"])).fetchall()
            art["sentences"] = [dict(zip(cols, r)) for r in rows]
            return art
        return self._jsonable(self._run(q))

    # ------------------------------------------------------------------ 写入
    @staticmethod
    def _new_share_id(conn):
        for _ in range(20):
            sid = secrets.token_hex(5)
            hit = conn.execute("SELECT 1 FROM reading.articles WHERE share_id = %s OR %s = ANY(alt_ids)",
                               (sid, sid)).fetchone()
            if not hit:
                return sid
        raise PgUnavailable("无法分配不冲突的 share_id")

    @staticmethod
    def _insert_version(conn, sid, version, tool, created_by, plan, tr_meta):
        conn.execute("INSERT INTO reading.versions (article_id, version, tool, created_by) VALUES (%s,%s,%s,%s)",
                     (sid, version, Jsonb(tool), created_by))
        now = dt.datetime.now(JST)
        rows = []
        for s in plan["sentences"]:
            has_tr = bool(s.get("tr"))
            rows.append((sid, version, s["seq"], s["para"], s["text"], s["char_start"], s["char_end"],
                         s["len_class"], s.get("tr"),
                         tr_meta.get("tr_by") if has_tr else None, tr_meta.get("tr_via") if has_tr else None,
                         tr_meta.get("tr_model") if has_tr else None, now if has_tr else None,
                         Jsonb(s["toks"]) if s["toks"] is not None else None, s["kana"], s["lemmas"],
                         Jsonb(s.get("fixes", []))))
        with conn.cursor() as cur:
            cur.executemany(
                "INSERT INTO reading.sentences (article_id, version, seq, para, text, char_start, char_end, "
                "len_class, tr, tr_by, tr_via, tr_model, tr_at, toks, kana, lemmas, fixes) "
                "VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)", rows)

    def _find_live_by_hash(self, conn, h):
        return conn.execute("SELECT share_id, current_version, status FROM reading.articles "
                            "WHERE content_hash = %s AND deleted_at IS NULL", (h,)).fetchone()

    def commit(self, plan, added_by, caller):
        tr_meta = {"tr_by": plan["executor"], "tr_via": plan["tr_via"], "tr_model": plan["tr_model"]}

        def tx(conn):
            conn.execute("SET CONSTRAINTS ALL DEFERRED")
            if plan["replace"]:
                return self._replace(conn, plan, added_by, caller, tr_meta)
            hit = self._find_live_by_hash(conn, plan["content_hash"])
            if hit:
                return {"ok": True, "share_id": hit[0], "url": f"/?s={hit[0]}", "duplicate": True,
                        "version": hit[1], "status": hit[2]}
            sid = self._new_share_id(conn)
            conn.execute(
                "INSERT INTO reading.articles (share_id, text, lang, rate, title, added_by, added_at, status, "
                "current_version) VALUES (%s,%s,%s,%s,%s,%s,%s,%s,1)",
                (sid, plan["text"], plan["lang"], plan["rate"], plan["title"], added_by,
                 dt.datetime.now(JST), plan["status"]))
            tool = dict(plan["tool"], kind="commit", caller=caller)
            self._insert_version(conn, sid, 1, tool, caller, plan, tr_meta)
            return {"ok": True, "share_id": sid, "url": f"/?s={sid}", "duplicate": False,
                    "version": 1, "status": plan["status"]}
        try:
            return self._run(tx)
        except psycopg.errors.UniqueViolation:
            hit = self._run(lambda c: self._find_live_by_hash(c, plan["content_hash"]))
            if hit:
                return {"ok": True, "share_id": hit[0], "url": f"/?s={hit[0]}", "duplicate": True,
                        "version": hit[1], "status": hit[2]}
            raise

    def _replace(self, conn, plan, added_by, caller, tr_meta):
        sid = plan.get("share_id")
        if not sid or not isinstance(sid, str):
            raise ApiError("BODY_INVALID", "replace 需要提供 share_id", "", {"path": "share_id"})
        row = conn.execute("SELECT share_id, current_version, deleted_at, text FROM reading.articles "
                           "WHERE share_id = %s OR %s = ANY(alt_ids) FOR UPDATE", (sid, sid)).fetchone()
        if row is None:
            raise ApiError("NOT_FOUND", "要替换的文章不存在", "", {"share_id": sid})
        sid, cur_v, deleted, old_text = row
        if deleted:
            raise ApiError("NOT_FOUND", "要替换的文章已删除", "", {"share_id": sid}, status=410)
        if plan.get("base_version") != cur_v:
            raise ApiError("VERSION_CONFLICT", "base_version 不是当前版本",
                           "先 GET /api/v1/articles/{id} 读最新版本，再基于它重做修改",
                           {"current_version": cur_v, "base_version": plan.get("base_version")})
        other = conn.execute("SELECT share_id FROM reading.articles WHERE content_hash = %s "
                             "AND deleted_at IS NULL AND share_id <> %s", (plan["content_hash"], sid)).fetchone()
        if other:
            raise ApiError("BODY_INVALID", "替换后的原文与另一篇已存在文章相同", "",
                           {"existing_share_id": other[0]})
        new_v = conn.execute("SELECT coalesce(max(version), 0) + 1 FROM reading.versions WHERE article_id = %s",
                             (sid,)).fetchone()[0]
        tool = dict(plan["tool"], kind="replace", caller=caller, base_version=cur_v)
        if old_text != plan["text"]:
            tool["prev_text"] = old_text
        self._insert_version(conn, sid, new_v, tool, caller, plan, tr_meta)
        conn.execute("UPDATE reading.articles SET text = %s, title = %s, lang = %s, rate = %s, status = %s, "
                     "current_version = %s, updated_at = now() WHERE share_id = %s",
                     (plan["text"], plan["title"], plan["lang"], plan["rate"], plan["status"], new_v, sid))
        return {"ok": True, "share_id": sid, "url": f"/?s={sid}", "duplicate": False,
                "version": new_v, "status": plan["status"]}

    def share(self, plan, meta):
        """/share 只交原文：去重命中返回已有 ID；否则新建 v1（status=partial）。返回 share_id。"""
        def tx(conn):
            conn.execute("SET CONSTRAINTS ALL DEFERRED")
            hit = self._find_live_by_hash(conn, plan["content_hash"])
            if hit:
                return hit[0]
            sid = self._new_share_id(conn)
            conn.execute(
                "INSERT INTO reading.articles (share_id, text, lang, rate, title, added_by, added_at, status, "
                "current_version) VALUES (%s,%s,%s,%s,%s,%s,%s,'partial',1)",
                (sid, plan["text"], meta["lang"], meta["rate"], meta.get("title"), meta.get("added_by"),
                 dt.datetime.now(JST)))
            tool = dict(plan["tool"], caller=meta.get("caller"), token=bool(meta.get("caller")))
            self._insert_version(conn, sid, 1, tool, meta.get("caller") or "share", plan, {})
            return sid
        try:
            return self._run(tx)
        except psycopg.errors.UniqueViolation:
            hit = self._run(lambda c: self._find_live_by_hash(c, plan["content_hash"]))
            if hit:
                return hit[0]
            raise

    # ------------------------------------------------------------------ SQLite 回落（与现行实现一致）
    def _sqlite(self):
        conn = sqlite3.connect(self.sqlite_path)
        conn.execute("CREATE TABLE IF NOT EXISTS shares (id TEXT PRIMARY KEY, text TEXT NOT NULL, lang TEXT, "
                     "rate TEXT, title TEXT, added_by TEXT, added_at TEXT)")
        return conn

    def sqlite_get(self, sid):
        with self._sqlite_lock, self._sqlite() as conn:
            row = conn.execute("SELECT text, lang, rate, title, added_by, added_at FROM shares WHERE id = ?",
                               (sid,)).fetchone()
        if row is None:
            return None
        item = {"text": row[0], "lang": row[1], "rate": row[2]}
        for k, v in zip(("title", "added_by", "added_at"), row[3:]):
            if v:
                item[k] = v
        return item

    def sqlite_put(self, sid, item):
        with self._sqlite_lock, self._sqlite() as conn:
            conn.execute("INSERT OR REPLACE INTO shares (id, text, lang, rate, title, added_by, added_at) "
                         "VALUES (?, ?, ?, ?, ?, ?, ?)",
                         (sid, item.get("text"), item.get("lang"), item.get("rate"), item.get("title"),
                          item.get("added_by"), item.get("added_at")))
