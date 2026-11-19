"""入库契约的纯逻辑部分（不依赖 aiohttp / 数据库，便于单测）：错误体、参数校验、commit 计划生成。

错误码只有 8 条（定版 8.3）：AUTH_FAILED / BODY_INVALID / SPLIT_MISMATCH / ALIGN_MISMATCH /
TR_MISSING / VERSION_CONFLICT / NOT_FOUND / RETRY_LATER。
warning 只有 3 种：TOKENIZER_FAILED / TR_SUSPECT / SPLIT_NONSTANDARD。
"""
import copy
import hashlib
import re

import tokenizer as T

MAX_TEXT = 5000
RATE_RE = re.compile(r"^[+-]\d+%$")
TR_VIA = {"self", "claude-code", "agy-gemini", "human", "import", "provided"}
LANGS = {"ja", "zh", "en"}
KANA_RE = re.compile(r"[぀-ヿ]")
NEEDS_READING_RE = re.compile(r"[㐀-鿿豈-﫿々〆ヶ0-9]")

HTTP = {"AUTH_FAILED": 401, "BODY_INVALID": 400, "SPLIT_MISMATCH": 422, "ALIGN_MISMATCH": 422,
        "TR_MISSING": 422, "VERSION_CONFLICT": 409, "NOT_FOUND": 404, "RETRY_LATER": 503}


class ApiError(Exception):
    def __init__(self, code, message, hint="", details=None, status=None):
        super().__init__(message)
        self.code, self.message, self.hint = code, message, hint
        self.details = details if details is not None else {}
        self.status = status or HTTP[code]

    def body(self):
        return {"ok": False, "error": {"code": self.code, "message": self.message,
                                       "hint": self.hint, "details": self.details}}


def sha256_hex(t):
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def need_str(body, key, *, required=True, maxlen=None):
    v = body.get(key)
    if v is None or (isinstance(v, str) and not v.strip()):
        if required:
            raise ApiError("BODY_INVALID", f"缺少字段 {key}", f"请提供非空的 {key}", {"path": key})
        return None
    if not isinstance(v, str):
        raise ApiError("BODY_INVALID", f"{key} 必须是字符串", "", {"path": key})
    if maxlen and len(v) > maxlen:
        raise ApiError("BODY_INVALID", f"{key} 超过 {maxlen} 字", "截短后重提", {"path": key, "len": len(v)})
    return v


def check_text(body):
    raw = need_str(body, "text")
    text = T.normalize(raw)
    if not text:
        raise ApiError("BODY_INVALID", "text 为空", "请提供原文", {"path": "text"})
    if len(text) > MAX_TEXT:
        raise ApiError("BODY_INVALID", f"text 超过 {MAX_TEXT} 字", "拆成多篇（标题加「上/下」）",
                       {"path": "text", "len": len(text)})
    return text


def check_lang_rate(body):
    lang = (body.get("lang") or "ja").lower()
    if lang not in LANGS:
        raise ApiError("BODY_INVALID", "lang 只能是 ja/zh/en", "", {"path": "lang"})
    rate = body.get("rate") or "-20%"
    if not isinstance(rate, str) or not RATE_RE.match(rate):
        raise ApiError("BODY_INVALID", "rate 需形如 -20%", "", {"path": "rate"})
    return lang, rate


def preview(body):
    text = check_text(body)
    check_lang_rate(body)
    sents, warnings = T.build_sentences(text)
    return {"ok": True, "text": text, "content_hash": sha256_hex(text),
            "sentences": [{k: s[k] for k in ("seq", "para", "text", "char_start", "char_end", "kana", "toks")}
                          for s in sents],
            "warnings": warnings}


def _ws(s):
    return re.sub(r"\s+", "", s or "")


def plan_commit(body):
    """校验 commit 请求体并生成写库计划（不碰数据库）。返回 dict；失败抛 ApiError。"""
    if not isinstance(body, dict):
        raise ApiError("BODY_INVALID", "请求体必须是 JSON 对象")
    text = check_text(body)
    title = need_str(body, "title", maxlen=120)
    lang, rate = check_lang_rate(body)
    executor = need_str(body, "executor", maxlen=40)
    tr_via = need_str(body, "tr_via")
    if tr_via not in TR_VIA:
        raise ApiError("BODY_INVALID", "tr_via 取值非法", f"可选：{sorted(TR_VIA)}", {"path": "tr_via"})
    tr_model = need_str(body, "tr_model", required=False, maxlen=80)
    prompt_version = need_str(body, "prompt_version", required=False, maxlen=40)
    translation_text = body.get("translation_text")
    allow_partial = bool(body.get("allow_partial"))
    replace = bool(body.get("replace"))
    items = body.get("sentences")
    if not isinstance(items, list) or not items:
        raise ApiError("BODY_INVALID", "sentences 必须是非空数组", "先调 preview 取分句", {"path": "sentences"})
    for i, it in enumerate(items):
        if not isinstance(it, dict) or not isinstance(it.get("text"), str):
            raise ApiError("BODY_INVALID", "sentences[].text 必须是字符串", "", {"path": f"sentences[{i}].text"})
    if translation_text is not None:
        if not isinstance(translation_text, str) or not translation_text.strip():
            raise ApiError("BODY_INVALID", "translation_text 必须是非空字符串", "", {"path": "translation_text"})
        if tr_via != "provided":
            raise ApiError("BODY_INVALID", "带 translation_text 时 tr_via 必须是 provided",
                           "现成译文只对齐不重译", {"path": "tr_via"})

    sents, warnings = T.build_sentences(text)
    server_texts = [s["text"] for s in sents]
    got_texts = [it["text"] for it in items]

    # 分句必须与服务端一致
    if got_texts != server_texts:
        bad = T.check_concat(text, got_texts)
        idx = next((i for i, (a, b) in enumerate(zip(server_texts, got_texts)) if a != b),
                   min(len(server_texts), len(got_texts)))
        details = {"index": idx,
                   "expected": server_texts[idx][:60] if idx < len(server_texts) else None,
                   "got": got_texts[idx][:60] if idx < len(got_texts) else None,
                   "server_count": len(server_texts), "got_count": len(got_texts)}
        if bad:
            details["at_char"] = bad["at_char"]
        raise ApiError("SPLIT_MISMATCH", "sentences 与服务端分句不一致" + ("（拼接也不等于原文）" if bad else ""),
                       "不要改动原文字符、不要自行合并或拆分句子；直接使用 preview 返回的 sentences[].text", details)

    # 读音覆盖：只改读音，不改边界
    for i, (it, s) in enumerate(zip(items, sents)):
        rds = it.get("readings") or []
        if not isinstance(rds, list):
            raise ApiError("BODY_INVALID", "readings 必须是数组", "", {"path": f"sentences[{i}].readings"})
        toks = s["toks"] or []
        for k, rd in enumerate(rds):
            path = f"sentences[{i}].readings[{k}]"
            if not isinstance(rd, dict) or not isinstance(rd.get("tok"), int) or not isinstance(rd.get("r"), str):
                raise ApiError("BODY_INVALID", "readings 项需为 {tok:int, s:str, r:str}", "", {"path": path})
            ti = rd["tok"]
            if not (0 <= ti < len(toks)) or toks[ti]["s"] != rd.get("s"):
                raise ApiError("BODY_INVALID", "readings 的 s 与该下标的 token 不一致",
                               "用 preview 返回的 toks 核对下标；下标从 0 起、含空白与标点 token",
                               {"path": path, "tok": ti, "expected": toks[ti]["s"] if 0 <= ti < len(toks) else None,
                                "got": rd.get("s")})
            if not T.HIRA_RE.match(rd["r"]):
                raise ApiError("BODY_INVALID", "读音只能是平假名（允许 ー）", "片假名请转平假名，去掉空格与罗马字",
                               {"path": path, "r": rd["r"]})
            t = toks[ti]
            s.setdefault("fixes", []).append({"tok": ti, "from": t["r"], "to": rd["r"], "by": executor})
            t["r"] = rd["r"]
            t.pop("unk", None)
            t["ruby"] = T.ruby_segments(t["s"], rd["r"])
        if s["toks"] is not None:
            if "".join(t["s"] for t in s["toks"]) != s["text"]:
                raise ApiError("SPLIT_MISMATCH", "toks 拼接与句子不一致", "", {"index": i})
            s["kana"] = "".join(t["r"] if t["r"] is not None else t["s"] for t in s["toks"])
    bad = T.check_concat(text, [s["text"] for s in sents])
    if bad:
        raise ApiError("SPLIT_MISMATCH", "句子拼接与原文不一致", "", bad)

    # 读音完整性：含汉字或数字的 token 必须有平假名读音
    missing = [{"index": i, "tok": k, "s": t["s"]}
               for i, s in enumerate(sents) for k, t in enumerate(s["toks"] or [])
               if t["pos"] != "sym" and NEEDS_READING_RE.search(t["s"]) and not t["r"]]
    readings_complete = not missing
    if missing and not allow_partial:
        raise ApiError("BODY_INVALID", f"有 {len(missing)} 个 token 缺读音",
                       "用 readings 逐个补齐（数字是重灾区：如 18→じゅうはち、3万2,000→さんまんにせん）；"
                       "确实无法补齐可传 allow_partial:true（文章将保持 partial）",
                       {"missing": missing[:50]})

    # 译文
    trs = [it.get("tr") if isinstance(it.get("tr"), str) else None for it in items]
    if translation_text is not None:
        empty = [i for i, t in enumerate(trs) if not (t or "").strip()]
        if empty:
            raise ApiError("ALIGN_MISMATCH", f"第 {empty[0]} 句没有对应译文",
                           "把该句译文补进对应 item，或并入相邻 item 的 tr（换行分隔）",
                           {"kind": "missing", "index": empty[0], "text": items[empty[0]]["text"][:60]})
        got, want = _ws("".join(trs)), _ws(translation_text)
        if got != want:
            if want.startswith(got):
                raise ApiError("ALIGN_MISMATCH", "translation_text 中有段落没有对应任何原文句",
                               "把悬空段并入相邻 item 的 tr；不要删改译文",
                               {"kind": "orphan", "rest": want[len(got):][:80]})
            k = next((i for i, (a, b) in enumerate(zip(got, want)) if a != b), min(len(got), len(want)))
            raise ApiError("ALIGN_MISMATCH", "各句 tr 拼接与 translation_text 不一致（译文被改动或顺序不对）",
                           "tr 必须一字不差地取自 translation_text，按原文顺序切分",
                           {"kind": "altered", "at_char": k, "expected": want[k:k + 30], "got": got[k:k + 30]})
    missing_tr = [i for i, t in enumerate(trs) if not (t or "").strip()]
    if missing_tr and not allow_partial:
        raise ApiError("TR_MISSING", f"有 {len(missing_tr)} 句缺译文", "补齐，或显式传 allow_partial:true",
                       {"indexes": missing_tr[:50]})
    for i, t in enumerate(trs):
        if t and len(t) > 500:
            raise ApiError("BODY_INVALID", "单句 tr 超过 500 字", "", {"path": f"sentences[{i}].tr"})
        if t and len(KANA_RE.findall(t)) > 0.3 * len(_ws(t)):
            warnings.append({"code": "TR_SUSPECT", "index": i, "message": "译文假名占比过高，疑似未翻译"})

    status = "ready" if (not missing_tr and readings_complete) else "partial"
    for s, t in zip(sents, trs):
        s["tr"] = t.strip() if t and t.strip() else None
        s.setdefault("fixes", [])
    tool = dict(T.tool_info(), executor=executor, model=tr_model, prompt_version=prompt_version)
    if translation_text is not None:
        tool["align"] = {"translation_chars": len(translation_text)}
    return {"text": text, "content_hash": sha256_hex(text), "title": title, "lang": lang, "rate": rate,
            "executor": executor, "tr_via": tr_via, "tr_model": tr_model, "prompt_version": prompt_version,
            "allow_partial": allow_partial, "replace": replace, "base_version": body.get("base_version"),
            "share_id": body.get("share_id"), "sentences": sents, "status": status, "tool": tool,
            "warnings": warnings}


def plan_share(text):
    """/share「只交原文」：规范化→分句→骨架，status=partial。"""
    sents, warnings = T.build_sentences(text)
    for s in sents:
        s["tr"] = None
        s["fixes"] = []
    return {"text": text, "content_hash": sha256_hex(text), "sentences": sents,
            "tool": dict(T.tool_info(), kind="share"), "warnings": warnings}


def clone(x):
    return copy.deepcopy(x)
