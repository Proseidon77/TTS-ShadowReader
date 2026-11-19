-- =============================================================================
-- reading schema DDL —— 朗读文章库（77B 真源与本机镜像共用的唯一一份 DDL）
-- 依据：design_proposal.md 第 3 节数据模型 ＋ 8.3 瘦身口径（定版）＋ 第 23 条归属字段
-- 兼容：PostgreSQL 16 与 17（未用 PG17 专属语法）；要求数据库编码为 UTF8。
-- 可重复执行：所有对象均 IF NOT EXISTS / OR REPLACE；只含 reading schema 自身对象，
--             不建角色、不授权（角色与授权见 reading_role.sql，仅 77B 执行）。
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS reading;
COMMENT ON SCHEMA reading IS '朗读跟读文章库：文章 / 解析版本 / 分句（含译文与分词注音）';

-- 内容哈希：UTF-8 字节的 sha256 十六进制。
-- 声明为 IMMUTABLE 以便用于生成列（convert_to 本身是 STABLE；在 UTF8 库中结果恒定，
-- 故两侧数据库都必须是 UTF8 编码，否则哈希不可比）。
CREATE OR REPLACE FUNCTION reading.sha256_hex(t text)
  RETURNS text
  LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
  RETURN encode(sha256(convert_to(t, 'UTF8')), 'hex');
COMMENT ON FUNCTION reading.sha256_hex(text) IS 'UTF-8 字节 sha256 十六进制；仅限 UTF8 编码数据库';

-- ---------------------------------------------------------------- articles
CREATE TABLE IF NOT EXISTS reading.articles (
  share_id        text        PRIMARY KEY CHECK (share_id ~ '^[0-9a-f]{10}$'),
  alt_ids         text[]      NOT NULL DEFAULT '{}',
  text            text        NOT NULL CHECK (char_length(text) BETWEEN 1 AND 5000),
  lang            text        NOT NULL DEFAULT 'ja'   CHECK (lang IN ('ja','zh','en')),
  rate            text        NOT NULL DEFAULT '-20%' CHECK (rate ~ '^[+-][0-9]+%$'),
  title           text        CHECK (char_length(title) <= 120),
  added_by        text        CHECK (char_length(added_by) <= 40),
  added_at        timestamptz NOT NULL,
  status          text        NOT NULL DEFAULT 'raw' CHECK (status IN ('raw','partial','ready')),
  current_version int,
  content_hash    text        GENERATED ALWAYS AS (reading.sha256_hex(text)) STORED,
  deleted_at      timestamptz,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE  reading.articles IS '朗读文章；share_id 即 /?s=<id> 短链，旧链接原样保留';
COMMENT ON COLUMN reading.articles.alt_ids IS '别名短链（去重合并时另一侧的 ID 等），/t/<id> 查找时一并匹配';
COMMENT ON COLUMN reading.articles.text IS '规范化后的原文（2.2 ①：仅半角片假名/全角英数 NFKC，换行统一为 \n，去首尾空白）';
COMMENT ON COLUMN reading.articles.status IS 'raw=无骨架 / partial=有骨架、译文不全 / ready=逐句译文齐全';
COMMENT ON COLUMN reading.articles.current_version IS '当前版本号，指向 reading.versions；NULL=尚无版本';
COMMENT ON COLUMN reading.articles.content_hash IS '规范化原文的 sha256（生成列）；未删除文章间唯一，用于全库去重（第 19 条）';
COMMENT ON COLUMN reading.articles.deleted_at IS '软删除时刻；非空即不对外显示（第 11 条）';

CREATE UNIQUE INDEX IF NOT EXISTS articles_content_hash_uq
  ON reading.articles (content_hash) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS articles_alt_ids_gin
  ON reading.articles USING gin (alt_ids);
CREATE INDEX IF NOT EXISTS articles_added_at_idx
  ON reading.articles (added_at DESC) WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------- versions
CREATE TABLE IF NOT EXISTS reading.versions (
  article_id  text        NOT NULL REFERENCES reading.articles (share_id),
  version     int         NOT NULL CHECK (version >= 1),
  tool        jsonb       NOT NULL DEFAULT '{}',
  note        text,
  created_by  text,
  created_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (article_id, version)
);
COMMENT ON TABLE  reading.versions IS '解析版本记录（轻量版，8.3）：每次整篇 commit / replace / 迁移各一行，全部保留（第 16 条）';
COMMENT ON COLUMN reading.versions.tool IS '{kind: import|commit|replace, executor, model, prompt_version, splitter, tokenizer, dict, align, ...}';

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                 WHERE conname = 'articles_current_version_fk'
                   AND conrelid = 'reading.articles'::regclass) THEN
    ALTER TABLE reading.articles
      ADD CONSTRAINT articles_current_version_fk
      FOREIGN KEY (share_id, current_version)
      REFERENCES reading.versions (article_id, version)
      DEFERRABLE INITIALLY DEFERRED;
  END IF;
END $$;

-- ---------------------------------------------------------------- sentences
CREATE TABLE IF NOT EXISTS reading.sentences (
  article_id    text        NOT NULL,
  version       int         NOT NULL,
  seq           int         NOT NULL CHECK (seq >= 0),
  para          int         NOT NULL DEFAULT 0,
  text          text        NOT NULL CHECK (text <> ''),
  char_start    int         NOT NULL CHECK (char_start >= 0),
  char_end      int         NOT NULL,
  len_class     char(1)     NOT NULL DEFAULT 'm' CHECK (len_class IN ('s','m','l')),
  tr            text,
  tr_by         text,
  tr_via        text        CHECK (tr_via IN ('self','claude-code','agy-gemini','human','import','provided')),
  tr_model      text,
  tr_at         timestamptz,
  toks          jsonb,
  kana          text,
  lemmas        text[],
  fixes         jsonb       NOT NULL DEFAULT '[]',
  content_hash  text        GENERATED ALWAYS AS (reading.sha256_hex(
                  article_id || chr(31) || version::text || chr(31) || seq::text || chr(31) ||
                  para::text || chr(31) || text || chr(31) ||
                  char_start::text || chr(31) || char_end::text || chr(31) ||
                  coalesce(tr, '') || chr(31) || coalesce(tr_by, '') || chr(31) ||
                  coalesce(tr_via, '') || chr(31) || coalesce(tr_model, '') || chr(31) ||
                  coalesce(toks::text, '') || chr(31) || fixes::text)) STORED,
  PRIMARY KEY (article_id, version, seq),
  CHECK (char_end > char_start),
  FOREIGN KEY (article_id, version) REFERENCES reading.versions (article_id, version) ON DELETE CASCADE
);
COMMENT ON TABLE  reading.sentences IS '分句：原文、译文、分词注音（toks JSONB，结构见方案 2.4）';
COMMENT ON COLUMN reading.sentences.char_start IS '在 articles.text 中的起点（字符下标，含）；text = articles.text[char_start, char_end)';
COMMENT ON COLUMN reading.sentences.tr IS '中文译文；NULL=尚未生成';
COMMENT ON COLUMN reading.sentences.tr_by IS '译文执行者（ddpc-claude / 77b-claude / agent 本人；迁移为 import）';
COMMENT ON COLUMN reading.sentences.toks IS '分词注音数组 [{s,l,pos,pd,r,ruby,conj?,sub?,unk?,gloss?}]';
COMMENT ON COLUMN reading.sentences.content_hash IS '行内容哈希（生成列，不含时间戳）：镜像两侧各自计算后对账';

CREATE INDEX IF NOT EXISTS sentences_lemmas_gin ON reading.sentences USING gin (lemmas);

-- ---------------------------------------------------------------- 当前版本视图
CREATE OR REPLACE VIEW reading.v_current_sentences AS
SELECT s.*
FROM reading.articles a
JOIN reading.sentences s ON s.article_id = a.share_id AND s.version = a.current_version
WHERE a.deleted_at IS NULL;
COMMENT ON VIEW reading.v_current_sentences IS '业务读取口：未删除文章的当前版本句子';
