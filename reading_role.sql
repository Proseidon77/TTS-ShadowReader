-- reading_rw：TtsWeb 服务直连专用角色（第 15 条），只授 reading schema 读写，不授 DELETE（删除一律软删除）。
-- 仅 77B 执行；口令由 setup_role.py 经 psql 变量 :'pw' 传入（不落盘到本文件）。可重复执行。
SELECT format('CREATE ROLE reading_rw LOGIN PASSWORD %L', :'pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'reading_rw') \gexec
SELECT format('ALTER ROLE reading_rw LOGIN PASSWORD %L', :'pw') \gexec
ALTER ROLE reading_rw SET search_path = reading;

GRANT CONNECT ON DATABASE db77database TO reading_rw;
GRANT USAGE ON SCHEMA reading TO reading_rw;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA reading TO reading_rw;
GRANT EXECUTE ON FUNCTION reading.sha256_hex(text) TO reading_rw;
ALTER DEFAULT PRIVILEGES IN SCHEMA reading GRANT SELECT, INSERT, UPDATE ON TABLES TO reading_rw;
