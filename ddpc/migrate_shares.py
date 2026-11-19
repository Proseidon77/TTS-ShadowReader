# One-time migration: shares.json -> ttsweb.db (SQLite shares table).
# added_by/added_at for the 4 legacy rows are attributed from chat records
# (times approximate to ~15 min; server did not log /share calls).
import json
import os
import sqlite3

BASE = r"C:\Tools\tts-web"
SRC = os.path.join(BASE, "shares.json")
DB = os.path.join(BASE, "ttsweb.db")
META = {
    "b02daa25df": ("A001", "2026-11-15T01:00:00+09:00"),
    "71be6cc46e": ("A001", "2026-11-15T17:00:00+09:00"),
    "ce605b3d6e": ("A003", "2026-11-15T18:10:00+09:00"),
    "75666a9829": ("A001", "2026-11-15T18:17:00+09:00"),
}

with open(SRC, encoding="utf-8") as fh:
    data = json.load(fh)

conn = sqlite3.connect(DB)
conn.execute(
    "CREATE TABLE IF NOT EXISTS shares ("
    "id TEXT PRIMARY KEY, text TEXT NOT NULL, lang TEXT, rate TEXT, "
    "title TEXT, added_by TEXT, added_at TEXT)"
)
for sid, item in data.items():
    by, at = META.get(sid, (None, None))
    conn.execute(
        "INSERT OR REPLACE INTO shares"
        " (id, text, lang, rate, title, added_by, added_at)"
        " VALUES (?, ?, ?, ?, ?, ?, ?)",
        (sid, item.get("text"), item.get("lang"), item.get("rate"),
         item.get("title"), by, at),
    )
conn.commit()
for row in conn.execute(
        "SELECT id, lang, rate, title, added_by, added_at"
        " FROM shares ORDER BY added_at"):
    print(row)
print("rows:", conn.execute("SELECT COUNT(*) FROM shares").fetchone()[0])
