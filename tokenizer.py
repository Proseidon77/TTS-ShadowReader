"""分句 + fugashi 骨架（preview / commit / /share 共用的唯一管线）。

来源：p0/fugashi_run.py（规范化、分句、tok_of、ruby 对齐）＋ impl/migrate_stock.py（tokenize 补空白）。
相对 P1 迁移脚本的差异（迁移数据用的是旧管线，见 P2a_report.md）：
  ① 词间空白补成空白符号 token（MeCab 吞空格）——迁移脚本已有，原样搬入；
  ② 新增：数字串合并——连续的数字/逗号/小数点及紧随的 万/億/兆 合并为一个 token，
     pos=num、r=None、unk=True（读音交执行者补，commit 时校验必须补齐）；
     合并串末尾的逗号/小数点不并入（如「1,」）。
"""
import re
import unicodedata

import fugashi

END = set("。！？!?…")
CLOSERS = set("」』）)》〉】\"'”’")
OPENERS = {"「": "」", "『": "』"}
KANJI_RE = re.compile(r"[㐀-鿿豈-﫿々〆ヶ]")
HALF_KATA_RE = re.compile(r"[｡-ﾟ]+")
FULL_ALNUM_RE = re.compile(r"[０-９Ａ-Ｚａ-ｚ]+")
HIRA_RE = re.compile(r"^[ぁ-ゖー]+$")
DIGIT_RE = re.compile(r"[0-9]")
NUM_PIECE_RE = re.compile(r"^[0-9][0-9.,]*$|^[.,]$")
NUM_UNITS = {"万", "億", "兆"}

POS_MAP = {
    "名詞": "noun", "代名詞": "pronoun", "動詞": "verb", "形容詞": "adj",
    "形状詞": "adj", "副詞": "adv", "連体詞": "adv", "接続詞": "conj",
    "感動詞": "conj", "助詞": "particle", "助動詞": "aux", "接頭辞": "affix",
    "接尾辞": "affix", "補助記号": "sym", "記号": "sym", "空白": "sym",
}
SPLITTER = "svc-v1"

_tagger = None


def tagger():
    global _tagger
    if _tagger is None:
        _tagger = fugashi.Tagger()
    return _tagger


def tool_info():
    t = tagger()
    return {"splitter": SPLITTER, "tokenizer": "fugashi " + getattr(fugashi, "__version__", "?"),
            "dict": t.dictionary_info[0]["filename"].split("/site-packages/")[-1]}


def normalize(text):
    """2.2 ①＋7.3.1：半角片假名/全角英数 NFKC，换行统一 \\n，去首尾空白。"""
    text = HALF_KATA_RE.sub(lambda m: unicodedata.normalize("NFKC", m.group(0)), text)
    text = FULL_ALNUM_RE.sub(lambda m: unicodedata.normalize("NFKC", m.group(0)), text)
    return text.replace("\r\n", "\n").replace("\r", "\n").strip()


def split_sentences(text):
    """返回 [(start, end, para)]；句末 。！？!?… 与换行；收尾括号并入；「」『』内不切；纯标点并入上一句。"""
    out, n, i, para = [], len(text), 0, 0
    while i < n:
        j = i
        while j < n and text[j].isspace():
            j += 1
        if out and text[i:j].count("\n") >= 2:
            para += 1
        i = j
        if i >= n:
            break
        start, depth, k = i, [], i
        while k < n:
            ch = text[k]
            if ch in OPENERS:
                depth.append(OPENERS[ch])
            elif depth and ch == depth[-1]:
                depth.pop()
            if ch == "\n" and not depth:
                break
            if ch in END and not depth:
                k += 1
                while k < n and (text[k] in END or text[k] in CLOSERS):
                    k += 1
                break
            k += 1
        end = k
        while end > start and text[end - 1].isspace():
            end -= 1
        seg = text[start:end]
        if out and all(c in END or c in CLOSERS for c in seg):
            ps, _, pp = out[-1]
            out[-1] = (ps, end, pp)
        elif seg:
            out.append((start, end, para))
        i = k
    return out


def kata2hira(s):
    return "".join(chr(ord(c) - 0x60) if "ァ" <= c <= "ヶ" else c for c in s)


def ruby_segments(surface, reading):
    """汉字段注音、假名段作锚点；对齐失败整词一段；数字 token 整词一段。"""
    if not reading:
        return None
    if DIGIT_RE.search(surface) and not KANJI_RE.search(surface.replace("万", "").replace("億", "").replace("兆", "")):
        return [{"t": surface, "r": reading}]
    if not KANJI_RE.search(surface):
        return None
    runs = re.findall(r"[㐀-鿿豈-﫿々〆ヶ]+|[^㐀-鿿豈-﫿々〆ヶ]+", surface)
    pattern = "".join("(.+?)" if KANJI_RE.match(r) else "(" + re.escape(kata2hira(r)) + ")" for r in runs)
    m = re.fullmatch(pattern, reading)
    if not m:
        return [{"t": surface, "r": reading}]
    return [{"t": run, "r": got} if KANJI_RE.match(run) else {"t": run} for run, got in zip(runs, m.groups())]


def tok_of(word):
    f = word.feature
    pos1 = f.pos1 or ""
    pd = "-".join(p for p in (f.pos1, f.pos2, f.pos3, f.pos4) if p and p != "*")
    kana = f.kana if f.kana not in (None, "*") else None
    r = kata2hira(kana) if kana is not None else None
    if pos1 in ("補助記号", "記号", "空白"):
        r = ""
    tok = {"s": word.surface, "l": f.lemma if f.lemma not in (None, "*") else word.surface,
           "pos": "num" if f.pos2 == "数詞" else POS_MAP.get(pos1, "other"),
           "pd": pd, "r": r, "ruby": ruby_segments(word.surface, r)}
    if f.cType not in (None, "*"):
        tok["conj"] = f"{f.cType}/{f.cForm}"
    if f.pos2 == "非自立可能" and pos1 in ("動詞", "形容詞"):
        tok["sub"] = True
    if kana is None and pos1 not in ("補助記号", "記号", "空白"):
        tok["unk"] = True
    return tok


def _ws_tok(ws):
    return {"s": ws, "l": ws, "pos": "sym", "pd": "空白", "r": "", "ruby": None}


def _merge_numbers(items):
    """items: [(white_space_before, tok)] → 合并数字串后的 toks（含补回的空白 token）。"""
    out, i, n = [], 0, len(items)
    while i < n:
        ws, tok = items[i]
        if ws:
            out.append(_ws_tok(ws))
        if not re.match(r"^[0-9]", tok["s"]):
            out.append(tok)
            i += 1
            continue
        group, j = [tok], i + 1
        while j < n and not items[j][0]:
            s = items[j][1]["s"]
            prev = group[-1]["s"]
            if NUM_PIECE_RE.match(s) or (s in NUM_UNITS and DIGIT_RE.search(prev)):
                group.append(items[j][1])
                j += 1
            else:
                break
        while len(group) > 1 and group[-1]["s"] in (",", "."):
            group.pop()
            j -= 1
        if len(group) == 1:
            out.append(tok)
        else:
            s = "".join(t["s"] for t in group)
            out.append({"s": s, "l": s, "pos": "num", "pd": "名詞-数詞", "r": None, "ruby": None,
                        "unk": True, "merged": len(group)})
        i = j
    return out


def tokenize(st):
    items = [(w.white_space, tok_of(w)) for w in tagger()(st)]
    return _merge_numbers(items)


def build_sentences(text):
    """规范化后的全文 → sentences（toks/kana/lemmas 已填，tr 为空）与 warnings。"""
    sents, warnings = [], []
    for seq, (a, b, para) in enumerate(split_sentences(text)):
        st = text[a:b]
        try:
            toks = tokenize(st)
        except Exception as exc:  # noqa: BLE001
            toks = None
            warnings.append({"code": "TOKENIZER_FAILED", "seq": seq, "message": repr(exc)})
        n = len(st)
        sents.append({
            "seq": seq, "para": para, "text": st, "char_start": a, "char_end": b,
            "len_class": "s" if n <= 20 else ("m" if n <= 45 else "l"),
            "toks": toks,
            "kana": "".join(t["r"] if t["r"] is not None else t["s"] for t in toks) if toks else None,
            "lemmas": sorted({t["l"] for t in toks if t["pos"] != "sym"}) if toks else [],
        })
    return sents, warnings


def check_concat(source, texts):
    """句子按序拼接 == 原文（只允许句间空白）。返回第一处错误 dict 或 None。"""
    cur = 0
    for i, t in enumerate(texts):
        while cur < len(source) and source[cur].isspace() and not t.startswith(source[cur]):
            cur += 1
        if source[cur:cur + len(t)] != t:
            return {"index": i, "at_char": cur, "expected": source[cur:cur + max(len(t), 1)][:60], "got": t[:60]}
        cur += len(t)
    if source[cur:].strip():
        return {"index": len(texts), "at_char": cur, "expected": source[cur:cur + 60], "got": ""}
    return None
