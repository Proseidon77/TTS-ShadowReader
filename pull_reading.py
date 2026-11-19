#!/usr/bin/env python3
"""pull_reading.py — 把 77B db77database.reading 单向 pull 到本机镜像（第 38 条）。

方向与 sync_77b.py 相反：77B 为真源、本机为镜像，只下行。
- 读：dbapi（MuseAgent 登录）GET reading 三表全部行（分页）。
- 写：本机 psql（reading_owner）单事务 upsert（临时表 + json_populate_recordset）。
- 对账：articles/sentences 用库内生成列 content_hash 逐行比对；versions 用
  Python 侧规范化 JSON 的 sha256 比对。任何不一致 exit 1 并报告。
- 软删除随行同步；源端没有的行本机不自动删，只报告。

用法: python3 pull_reading.py [--check]
"""
import hashlib, json, os, subprocess, sys, urllib.request, urllib.error
from urllib.parse import quote

BASE = os.environ.get('DBAPI_BASE', 'https://8792-agq6dov7ieyr5kl67vfd2cqvce.instabox.tech')
DB, SCHEMA, USER = 'db77database', 'reading', 'MuseAgent'
PW_FILE = os.path.expanduser('~/workspace/jpdb/dbapi_agent.pw')
PSQL = os.path.expanduser('~/workspace/postgres/pylibs/pgserver/pginstall/bin/psql')
SOCK = os.path.expanduser('~/workspace/postgres/srv/run')
OWNER_PW = os.path.expanduser('~/workspace/tts-redesign/reading_owner.pw')

TABLES = {
    'articles': {
        'pk': ['share_id'],
        'cols': ['share_id', 'alt_ids', 'text', 'lang', 'rate', 'title', 'added_by',
                 'added_at', 'status', 'current_version', 'deleted_at', 'created_at', 'updated_at'],
        'arrays': ['alt_ids'],
    },
    'versions': {
        'pk': ['article_id', 'version'],
        'cols': ['article_id', 'version', 'tool', 'note', 'created_by', 'created_at'],
        'arrays': [],
    },
    'sentences': {
        'pk': ['article_id', 'version', 'seq'],
        'cols': ['article_id', 'version', 'seq', 'para', 'text', 'char_start', 'char_end',
                 'len_class', 'tr', 'tr_by', 'tr_via', 'tr_model', 'tr_at', 'toks', 'kana',
                 'lemmas', 'fixes'],
        'arrays': ['lemmas'],
    },
}
ORDER = ['articles', 'versions', 'sentences']


def curl_json(method, url, token=None, body=None):
    # urllib 的 chunked 读取会被 instabox 代理截断，统一走 curl（sync_77b 之外的新教训）
    cmd = ['curl', '-s', '-X', method, url]
    if token:
        cmd += ['-H', 'Authorization: Bearer ' + token]
    if body is not None:
        cmd += ['-H', 'Content-Type: application/json', '-d', json.dumps(body)]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=90)
    if r.returncode != 0:
        sys.exit(f'curl failed: {r.stderr[:200]}')
    return json.loads(r.stdout)


def login():
    pw = open(PW_FILE).read().strip()
    j = curl_json('POST', BASE + '/api/v1/auth/login',
                  body={'username': USER, 'password': pw})
    return j['access_token']


def fetch_all(token, table):
    rows, offset = [], 0
    while True:
        url = f'{BASE}/api/v1/databases/{DB}/schemas/{SCHEMA}/tables/{table}/rows?limit=500&offset={offset}'
        j = curl_json('GET', url, token=token)
        batch = j.get('rows', [])
        rows.extend(batch)
        if len(batch) < 500:
            return rows
        offset += 500


def pg_array_literal(values):
    def q(v):
        return '"' + str(v).replace('\\', '\\\\').replace('"', '\\"') + '"'
    return '{' + ','.join(q(v) for v in values) + '}'


def psql(sql, db='db77database'):
    env = dict(os.environ)
    env['PGPASSWORD'] = open(OWNER_PW).read().strip()
    r = subprocess.run([PSQL, '-h', SOCK, '-U', 'reading_owner', '-d', db,
                        '-v', 'ON_ERROR_STOP=1', '-t', '-A'],
                       input=sql, capture_output=True, text=True, env=env)
    if r.returncode != 0:
        sys.exit('psql failed: ' + r.stderr[:500])
    return r.stdout


import re
from datetime import datetime, timezone

_TS_RE = re.compile(r'^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}')


def _norm(v):
    if isinstance(v, dict):
        return {k: _norm(x) for k, x in v.items()}
    if isinstance(v, list):
        return [_norm(x) for x in v]
    if isinstance(v, str) and _TS_RE.match(v):
        s = v.replace(' ', 'T', 1)
        s = re.sub(r'([+-]\d{2})$', r'\1:00', s)
        try:
            dt = datetime.fromisoformat(s.replace('Z', '+00:00'))
            if dt.tzinfo is not None:
                return dt.astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%f+00:00')
        except ValueError:
            pass
    return v


def canon_hash(row, cols):
    payload = {c: _norm(row.get(c)) for c in cols}
    return hashlib.sha256(json.dumps(payload, ensure_ascii=False, sort_keys=True,
                                     default=str).encode('utf-8')).hexdigest()


def main():
    check_only = '--check' in sys.argv
    token = login()
    src = {t: fetch_all(token, t) for t in ORDER}
    for t in ORDER:
        print(f'source {t}: {len(src[t])} rows')

    if not check_only:
        stmts = ['BEGIN;']
        for t in ORDER:
            spec = TABLES[t]
            cols = spec['cols']
            rows = []
            for row in src[t]:
                r = {c: row.get(c) for c in cols}
                for a in spec['arrays']:
                    if isinstance(r.get(a), list):
                        r[a] = pg_array_literal(r[a])
                rows.append(r)
            payload = json.dumps(rows, ensure_ascii=False).replace("'", "''")
            collist = ', '.join(cols)
            updates = ', '.join(f'{c} = EXCLUDED.{c}' for c in cols if c not in spec['pk'])
            stmts.append(f"CREATE TEMP TABLE stage_{t} AS SELECT {collist} FROM reading.{t} WITH NO DATA;")
            stmts.append(f"INSERT INTO stage_{t} SELECT * FROM json_populate_recordset(NULL::stage_{t}, '{payload}'::json);")
            stmts.append(f"INSERT INTO reading.{t} ({collist}) SELECT {collist} FROM stage_{t} "
                         f"ON CONFLICT ({', '.join(spec['pk'])}) DO UPDATE SET {updates};")
            stmts.append(f"DROP TABLE stage_{t};")
        stmts.append('COMMIT;')
        psql('\n'.join(stmts))
        print('upsert done')

    # ---- 对账 ----
    ok = True
    local = {}
    for t in ORDER:
        out = psql(f"SELECT COALESCE(json_agg(x), '[]'::json) FROM (SELECT * FROM reading.{t}) x")
        local[t] = json.loads(out)
        if len(local[t]) != len(src[t]):
            print(f'MISMATCH count {t}: source {len(src[t])} vs local {len(local[t])}')
            ok = False
    for t in ('articles', 'sentences'):
        spec = TABLES[t]
        s_map = {tuple(r[c] for c in spec['pk']): r['content_hash'] for r in src[t]}
        l_map = {tuple(r[c] for c in spec['pk']): r['content_hash'] for r in local[t]}
        if s_map != l_map:
            diff = [k for k in s_map if s_map.get(k) != l_map.get(k)]
            print(f'MISMATCH content_hash {t}: {len(diff)} rows differ, e.g. {diff[:3]}')
            ok = False
        else:
            print(f'{t}: content_hash all match ({len(s_map)} rows)')
    spec = TABLES['versions']
    s_map = {tuple(r[c] for c in spec['pk']): canon_hash(r, spec['cols']) for r in src['versions']}
    l_map = {tuple(r[c] for c in spec['pk']): canon_hash(r, spec['cols']) for r in local['versions']}
    if s_map != l_map:
        print('MISMATCH versions canon hash')
        ok = False
    else:
        print(f'versions: canon hash all match ({len(s_map)} rows)')
    print('PULL OK' if ok else 'PULL FAILED')
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
