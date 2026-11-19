#!/usr/bin/env python3
"""微软 Edge TTS 流式朗读服务（本机 127.0.0.1:8790）。

协议与做法照 A003（糖糖）2026-11-14 在 MuseHub 分享并实测的方案：
- 走 Edge ReadAloud 的 WSS 协议，协议辅助借 edge-tts==7.3.1
  （DRM.generate_sec_ms_gec / headers_with_muid / mkssml /
  ssml_headers_plus_data / TTSConfig / WSS_URL 等常量）；
- 传输层用 websockets 直连，不用 edge_tts 自带的 aiohttp 传输
  （代理环境下握手会被拒）；握手去掉重复的 Sec-WebSocket-Version 头，
  Origin 用 websockets 的 origin 参数单独传；
- TLS 用系统 CA 包 /etc/ssl/certs/ca-certificates.crt（不用 certifi）；
- 出网代理从环境变量 HTTPS_PROXY 读，候选列表 [env 代理, 直连]，
  未出音频前失败就换下一条（代理地址只存在于进程环境，不落任何文件）；
- 音频以 audio-24khz-48kbitrate-mono-mp3 流式返回：服务先取到第一块
  音频才回 200，上游彻底失败回 502，绝不回空音频的 200；
- 长文本按句界切分（每段 ≤500 字），逐段在同一 HTTP 响应里顺序流回。

接口：
  POST /tts  JSON {"text": "...", "lang": "ja", "rate": "+10%", "pitch": "+0Hz"}
  GET  /tts?text=...&lang=en&voice=en-US-AriaNeural
  参数：lang=ja/en/zh 或 voice 直接指定音色（二者给其一，voice 优先）；
        rate / pitch / volume 可选；文本上限 5000 字。
  GET  /health → {"ok": true}
默认音色：ja=ja-JP-NanamiNeural, en=en-US-AriaNeural, zh=zh-CN-XiaoxiaoNeural。
"""

import asyncio
import json
import logging
import os
import re
import ssl
import sys
import uuid
from xml.sax.saxutils import escape

import websockets
from aiohttp import web
from edge_tts.communicate import (
    connect_id,
    date_to_string,
    get_headers_and_data,
    mkssml,
    remove_incompatible_characters,
    ssml_headers_plus_data,
)
from edge_tts.constants import SEC_MS_GEC_VERSION, WSS_HEADERS, WSS_URL
from edge_tts.data_classes import TTSConfig
from edge_tts.drm import DRM

HOST = "0.0.0.0"
PORT = 8791
MAX_TEXT_CHARS = 5000
SEGMENT_MAX_CHARS = 2000  # 网页版：整段单次合成，避免拼接处 iOS 解码中断（2026-11-15 实测修正）
OUTPUT_FORMAT = "audio-24khz-48kbitrate-mono-mp3"

import shutil as _shutil


def _find_ffmpeg():
    for cand in (r"C:\Tools\ffmpeg\bin\ffmpeg.exe", "ffmpeg"):
        path = _shutil.which(cand) if cand == "ffmpeg" else (cand if os.path.exists(cand) else None)
        if path:
            return path
    return None


FFMPEG_EXE = _find_ffmpeg()
SSL_CTX = ssl.create_default_context()  # Windows 系统证书库
ORIGIN = WSS_HEADERS.get("Origin")
DEFAULT_VOICES = {
    "ja": "ja-JP-NanamiNeural",
    "en": "en-US-AriaNeural",
    "zh": "zh-CN-XiaoxiaoNeural",
}
RATE_RE = re.compile(r"^[+-]\d+%$")
PITCH_RE = re.compile(r"^[+-]\d+(\.\d+)?Hz$")
SENTENCE_RE = re.compile(r"[^。．.!?！？；;…\n]*[。．.!?！？；;…\n]+|[^。．.!?！？；;…\n]+")

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    stream=sys.stderr,
)
log = logging.getLogger("muse-tts")


def proxy_candidates():
    cands = []
    proxy = os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy")
    if proxy:
        cands.append(proxy)
    cands.append(None)  # 直连兜底
    return cands


def split_text(text):
    """按句界切分并打包成 ≤SEGMENT_MAX_CHARS 的段；超长句硬切。"""
    pieces = [m.group(0) for m in SENTENCE_RE.finditer(text) if m.group(0)]
    segments = []
    buf = ""
    for piece in pieces:
        while len(piece) > SEGMENT_MAX_CHARS:
            if buf:
                segments.append(buf)
                buf = ""
            segments.append(piece[:SEGMENT_MAX_CHARS])
            piece = piece[SEGMENT_MAX_CHARS:]
        if len(buf) + len(piece) > SEGMENT_MAX_CHARS:
            segments.append(buf)
            buf = piece
        else:
            buf += piece
    if buf:
        segments.append(buf)
    return [s for s in segments if s.strip()]


async def stream_segment(text, voice, rate, volume, pitch):
    """对一段文本流式产出 MP3 块；代理候选在首块音频到达前可切换。"""
    tts_config = TTSConfig(voice, rate, volume, pitch, "SentenceBoundary")
    headers = {
        k: v
        for k, v in DRM.headers_with_muid(WSS_HEADERS).items()
        if k not in ("Sec-WebSocket-Version", "Origin")
    }
    config_payload = json.dumps(
        {
            "context": {
                "synthesis": {
                    "audio": {
                        "metadataoptions": {
                            "sentenceBoundaryEnabled": "true",
                            "wordBoundaryEnabled": "false",
                        },
                        "outputFormat": OUTPUT_FORMAT,
                    }
                }
            }
        },
        separators=(",", ":"),
    )
    last_exc = None
    for proxy in proxy_candidates():
        got_audio = False
        try:
            url = (
                f"{WSS_URL}&ConnectionId={connect_id()}"
                f"&Sec-MS-GEC={DRM.generate_sec_ms_gec()}"
                f"&Sec-MS-GEC-Version={SEC_MS_GEC_VERSION}"
            )
            async with websockets.connect(
                url,
                additional_headers=headers,
                origin=ORIGIN,
                ssl=SSL_CTX,
                proxy=proxy,
                max_size=None,
                open_timeout=10,
                close_timeout=5,
            ) as ws:
                await ws.send(
                    f"X-Timestamp:{date_to_string()}\r\n"
                    "Content-Type:application/json; charset=utf-8\r\n"
                    "Path:speech.config\r\n\r\n"
                    f"{config_payload}\r\n"
                )
                await ws.send(
                    ssml_headers_plus_data(
                        connect_id(),
                        date_to_string(),
                        mkssml(
                            tts_config,
                            escape(remove_incompatible_characters(text)),
                        ),
                    )
                )
                async with asyncio.timeout(120):
                    async for msg in ws:
                        if isinstance(msg, str):
                            if "Path:turn.end" in msg:
                                break
                            continue
                        if len(msg) < 2:
                            continue
                        header_length = int.from_bytes(msg[:2], "big")
                        frame_headers, data = get_headers_and_data(msg, header_length)
                        if frame_headers.get(b"Path") != b"audio":
                            continue
                        if data:
                            got_audio = True
                            yield data
                if got_audio:
                    return
                last_exc = RuntimeError("连接成功但没有收到音频")
        except Exception as exc:  # noqa: BLE001 - 候选切换需要吞掉单条失败
            if got_audio:
                raise
            last_exc = exc
    raise RuntimeError(f"全部代理候选均失败: {last_exc!r}")


async def stream_text(text, voice, rate, volume, pitch):
    for segment in split_text(text):
        async for chunk in stream_segment(segment, voice, rate, volume, pitch):
            yield chunk


async def handle_tts(request):
    if request.method == "POST":
        try:
            params = await request.json()
        except (json.JSONDecodeError, ValueError):
            return web.json_response({"error": "请求体不是合法 JSON"}, status=400)
        if not isinstance(params, dict):
            return web.json_response({"error": "请求体必须是 JSON 对象"}, status=400)
    else:
        params = dict(request.query)

    text = (params.get("text") or "").strip()
    if not text:
        return web.json_response({"error": "缺少 text"}, status=400)
    if len(text) > MAX_TEXT_CHARS:
        return web.json_response(
            {"error": f"text 超过 {MAX_TEXT_CHARS} 字上限"}, status=400
        )

    voice = params.get("voice") or DEFAULT_VOICES.get(
        (params.get("lang") or "ja").lower(), DEFAULT_VOICES["ja"]
    )
    rate = params.get("rate") or "+0%"
    pitch = params.get("pitch") or "+0Hz"
    volume = params.get("volume") or "+0%"
    if not RATE_RE.match(rate) or not PITCH_RE.match(pitch) or not RATE_RE.match(volume):
        return web.json_response({"error": "rate/volume 需如 +10%，pitch 需如 +5Hz"}, status=400)

    agen = stream_text(text, voice, rate, volume, pitch)
    try:
        first = await agen.__anext__()
    except StopAsyncIteration:
        return web.json_response({"error": "上游未返回音频"}, status=502)
    except Exception as exc:  # noqa: BLE001
        log.warning("TTS 上游失败 voice=%s err=%r", voice, exc)
        return web.json_response({"error": f"上游合成失败: {exc}"}, status=502)

    response = web.StreamResponse(
        status=200, headers={"Content-Type": "audio/mpeg"}
    )
    await response.prepare(request)
    total = len(first)
    try:
        await response.write(first)
        async for chunk in agen:
            total += len(chunk)
            await response.write(chunk)
        log.info("TTS 完成 voice=%s chars=%d bytes=%d", voice, len(text), total)
    except (ConnectionResetError, asyncio.CancelledError):
        log.info("TTS 客户端中途断开 voice=%s bytes=%d", voice, total)
        raise
    except Exception as exc:  # noqa: BLE001 - 响应已开始，只能截断并记日志
        log.warning("TTS 流中段失败 voice=%s err=%r", voice, exc)
    finally:
        await agen.aclose()
    await response.write_eof()
    return response


async def handle_tts_aac(request):
    return await _handle_tts_transcode(request, "mp4")


async def handle_tts_adts(request):
    return await _handle_tts_transcode(request, "adts")


async def _handle_tts_transcode(request, fmt):
    """与 /tts 相同的参数，但把微软的 MP3 流实时转码成 AAC（fragmented MP4）再转发。

    iOS 的解码器对 24kHz MPEG-2 MP3 流式播放会中途停（整流经 ffmpeg 校验无损、
    桌面浏览器可完整播放，唯 iOS 在约七成处停），故网页版走这条：仍是边合成边播，
    不等整段。"""
    if FFMPEG_EXE is None:
        return web.json_response({"error": "ffmpeg 不可用"}, status=500)
    if request.method == "POST":
        try:
            params = await request.json()
        except (json.JSONDecodeError, ValueError):
            return web.json_response({"error": "请求体不是合法 JSON"}, status=400)
        if not isinstance(params, dict):
            return web.json_response({"error": "请求体必须是 JSON 对象"}, status=400)
    else:
        params = dict(request.query)

    text = (params.get("text") or "").strip()
    if not text:
        return web.json_response({"error": "缺少 text"}, status=400)
    if len(text) > MAX_TEXT_CHARS:
        return web.json_response({"error": f"text 超过 {MAX_TEXT_CHARS} 字上限"}, status=400)
    voice = params.get("voice") or DEFAULT_VOICES.get(
        (params.get("lang") or "ja").lower(), DEFAULT_VOICES["ja"]
    )
    rate = params.get("rate") or "+0%"
    pitch = params.get("pitch") or "+0Hz"
    volume = params.get("volume") or "+0%"
    if not RATE_RE.match(rate) or not PITCH_RE.match(pitch) or not RATE_RE.match(volume):
        return web.json_response({"error": "rate/volume 需如 +10%，pitch 需如 +5Hz"}, status=400)

    agen = stream_text(text, voice, rate, volume, pitch)
    try:
        first = await agen.__anext__()
    except StopAsyncIteration:
        return web.json_response({"error": "上游未返回音频"}, status=502)
    except Exception as exc:  # noqa: BLE001
        log.warning("TTS(AAC) 上游失败 voice=%s err=%r", voice, exc)
        return web.json_response({"error": f"上游合成失败: {exc}"}, status=502)

    proc = await asyncio.create_subprocess_exec(
        FFMPEG_EXE, "-v", "error", "-i", "pipe:0",
        "-ar", "44100", "-ac", "1", "-c:a", "aac", "-b:a", "96k",
        *(["-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof"]
          if fmt == "mp4" else ["-f", "adts"]),
        "pipe:1",
        stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.DEVNULL,
    )

    async def feed():
        try:
            proc.stdin.write(first)
            async for chunk in agen:
                proc.stdin.write(chunk)
                await proc.stdin.drain()
        except (ConnectionResetError, BrokenPipeError, asyncio.CancelledError):
            pass
        finally:
            try:
                proc.stdin.close()
            except Exception:  # noqa: BLE001
                pass

    feeder = asyncio.ensure_future(feed())
    response = web.StreamResponse(status=200, headers={"Content-Type": "audio/mp4" if fmt == "mp4" else "audio/aac"})
    await response.prepare(request)
    total = 0
    try:
        while True:
            data = await proc.stdout.read(65536)
            if not data:
                break
            total += len(data)
            await response.write(data)
        log.info("TTS(AAC) 完成 voice=%s chars=%d bytes=%d", voice, len(text), total)
    except Exception as exc:  # noqa: BLE001
        log.info("TTS(AAC) 流中断 voice=%s bytes=%d err=%r", voice, total, exc)
        feeder.cancel()
        try:
            proc.kill()
        except Exception:  # noqa: BLE001
            pass
        return response
    finally:
        try:
            await asyncio.wait_for(proc.wait(), timeout=10)
        except Exception:  # noqa: BLE001
            try:
                proc.kill()
            except Exception:  # noqa: BLE001
                pass
        await agen.aclose()
    await response.write_eof()
    return response


# ---------------- HLS 直播（iOS 原生流媒体格式） ----------------
# 裸 MP3 流 / 裸 ADTS 流在 iOS 的 <audio> 里都会播到中途自己收尾（数据经日志
# 证实完整送达、桌面端解码无误），故网页版改用苹果自家的 HLS：ffmpeg 把
# 转码中的 AAC 切成小段 TS 文件并维护 m3u8 列表，手机边下边播、列表持续
# 更新，合成结束后补 ENDLIST 收尾。起播只需等第一段（约 2-3 秒）。
HLS_ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "hls")
HLS_SEG_SECONDS = 3


async def _run_hls_session(sid, text, voice, rate, volume, pitch):
    sdir = os.path.join(HLS_ROOT, sid)
    os.makedirs(sdir, exist_ok=True)
    playlist = os.path.join(sdir, "playlist.m3u8")
    agen = stream_text(text, voice, rate, volume, pitch)
    proc = None
    try:
        first = await agen.__anext__()
        proc = await asyncio.create_subprocess_exec(
            FFMPEG_EXE, "-v", "error", "-i", "pipe:0",
            "-ar", "44100", "-ac", "1", "-c:a", "aac", "-b:a", "96k",
            "-f", "hls", "-hls_time", str(HLS_SEG_SECONDS), "-hls_list_size", "0",
            "-hls_flags", "omit_endlist",
            "-hls_segment_filename", os.path.join(sdir, "seg%03d.ts"),
            playlist,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.DEVNULL,
            stderr=asyncio.subprocess.DEVNULL,
        )
        try:
            proc.stdin.write(first)
            async for chunk in agen:
                proc.stdin.write(chunk)
                await proc.stdin.drain()
        except (ConnectionResetError, BrokenPipeError):
            pass
        finally:
            try:
                proc.stdin.close()
            except Exception:  # noqa: BLE001
                pass
        await asyncio.wait_for(proc.wait(), timeout=30)
        # ffmpeg 收尾后给列表补 ENDLIST，播放器据此知道已完整
        try:
            with open(playlist, encoding="utf-8") as fh:
                body = fh.read()
            if "#EXT-X-ENDLIST" not in body:
                with open(playlist, "a", encoding="utf-8") as fh:
                    fh.write("#EXT-X-ENDLIST\n")
        except OSError:
            pass
        log.info("HLS 会话完成 sid=%s chars=%d", sid, len(text))
    except Exception as exc:  # noqa: BLE001
        log.warning("HLS 会话失败 sid=%s err=%r", sid, exc)
        if proc is not None:
            try:
                proc.kill()
            except Exception:  # noqa: BLE001
                pass
    finally:
        await agen.aclose()


def _cleanup_hls_sessions(keep=6):
    try:
        entries = []
        for name in os.listdir(HLS_ROOT):
            full = os.path.join(HLS_ROOT, name)
            if os.path.isdir(full):
                entries.append((os.path.getmtime(full), full))
        entries.sort()
        for _mtime, full in entries[:-keep] if len(entries) > keep else []:
            import shutil as _sh
            _sh.rmtree(full, ignore_errors=True)
    except OSError:
        pass


async def handle_play(request):
    if FFMPEG_EXE is None:
        return web.json_response({"error": "ffmpeg 不可用"}, status=500)
    try:
        params = await request.json()
    except (json.JSONDecodeError, ValueError):
        return web.json_response({"error": "请求体不是合法 JSON"}, status=400)
    if not isinstance(params, dict):
        return web.json_response({"error": "请求体必须是 JSON 对象"}, status=400)
    text = (params.get("text") or "").strip()
    if not text:
        return web.json_response({"error": "缺少 text"}, status=400)
    if len(text) > MAX_TEXT_CHARS:
        return web.json_response({"error": f"text 超过 {MAX_TEXT_CHARS} 字上限"}, status=400)
    voice = params.get("voice") or DEFAULT_VOICES.get(
        (params.get("lang") or "ja").lower(), DEFAULT_VOICES["ja"]
    )
    rate = params.get("rate") or "+0%"
    pitch = params.get("pitch") or "+0Hz"
    volume = params.get("volume") or "+0%"
    if not RATE_RE.match(rate) or not PITCH_RE.match(pitch) or not RATE_RE.match(volume):
        return web.json_response({"error": "rate/volume 需如 +10%，pitch 需如 +5Hz"}, status=400)

    os.makedirs(HLS_ROOT, exist_ok=True)
    _cleanup_hls_sessions()
    sid = uuid.uuid4().hex[:16]
    asyncio.ensure_future(_run_hls_session(sid, text, voice, rate, volume, pitch))
    playlist = os.path.join(HLS_ROOT, sid, "playlist.m3u8")
    # 等第一段切片写进列表再返回，保证播放器拿到就能播
    for _ in range(60):
        await asyncio.sleep(0.25)
        try:
            with open(playlist, encoding="utf-8") as fh:
                if ".ts" in fh.read():
                    break
        except OSError:
            continue
    else:
        return web.json_response({"error": "会话启动超时"}, status=504)
    return web.json_response({"url": f"/hls/{sid}/playlist.m3u8"})


_HLS_NAME_RE = __import__("re").compile(r"^(playlist\.m3u8|seg\d+\.ts)$")


async def handle_hls_file(request):
    sid = request.match_info["sid"]
    name = request.match_info["file"]
    if not sid.isalnum() or not _HLS_NAME_RE.match(name):
        return web.Response(status=404)
    path = os.path.join(HLS_ROOT, sid, name)
    if not os.path.isfile(path):
        return web.Response(status=404)
    ctype = ("application/vnd.apple.mpegurl" if name.endswith(".m3u8")
             else "video/mp2t")
    with open(path, "rb") as fh:
        data = fh.read()
    return web.Response(body=data, content_type=ctype,
                        headers={"Cache-Control": "no-cache"})


# ---------------- 分享链接：文本存 SQLite，链接只带短 ID ----------------
# 2026-11-15 由 shares.json 改为 SQLite（ttsweb.db 的 shares 表）：除原字段
# text/lang/rate/title 外，另记 added_by（谁加的，如 A001/A003）与
# added_at（添加时间，JST ISO 格式）。shares.json 已迁移并改名备份。
import datetime as _dt
import sqlite3 as _sqlite3
import threading as _threading

DB_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ttsweb.db")
_db_lock = _threading.Lock()
_JST = _dt.timezone(_dt.timedelta(hours=9))


def _db_conn():
    conn = _sqlite3.connect(DB_FILE)
    conn.execute(
        "CREATE TABLE IF NOT EXISTS shares ("
        "id TEXT PRIMARY KEY, text TEXT NOT NULL, lang TEXT, rate TEXT, "
        "title TEXT, added_by TEXT, added_at TEXT)"
    )
    return conn


def _share_put(sid, item):
    with _db_lock, _db_conn() as conn:
        conn.execute(
            "INSERT OR REPLACE INTO shares"
            " (id, text, lang, rate, title, added_by, added_at)"
            " VALUES (?, ?, ?, ?, ?, ?, ?)",
            (sid, item.get("text"), item.get("lang"), item.get("rate"),
             item.get("title"), item.get("added_by"), item.get("added_at")),
        )


def _share_get(sid):
    with _db_lock, _db_conn() as conn:
        row = conn.execute(
            "SELECT text, lang, rate, title, added_by, added_at"
            " FROM shares WHERE id = ?", (sid,),
        ).fetchone()
    if row is None:
        return None
    item = {"text": row[0], "lang": row[1], "rate": row[2]}
    if row[3]:
        item["title"] = row[3]
    if row[4]:
        item["added_by"] = row[4]
    if row[5]:
        item["added_at"] = row[5]
    return item


async def handle_share(request):
    """POST /share {text, lang?, rate?, title?, by?} -> {"id", "url"}，页面以 /?s=<id> 打开。

    by 为保存者标识（如 A001/A003），与服务器时间一起记入 shares 表。
    """
    try:
        params = await request.json()
    except (json.JSONDecodeError, ValueError):
        return web.json_response({"error": "请求体不是合法 JSON"}, status=400)
    if not isinstance(params, dict):
        return web.json_response({"error": "请求体必须是 JSON 对象"}, status=400)
    text = (params.get("text") or "").strip()
    if not text:
        return web.json_response({"error": "缺少 text"}, status=400)
    if len(text) > MAX_TEXT_CHARS:
        return web.json_response({"error": f"text 超过 {MAX_TEXT_CHARS} 字上限"}, status=400)
    rate = params.get("rate") or "-20%"
    if not RATE_RE.match(rate):
        return web.json_response({"error": "rate 需如 -20%"}, status=400)
    sid = uuid.uuid4().hex[:10]
    item = {
        "text": text,
        "lang": (params.get("lang") or "ja").lower(),
        "rate": rate,
        "added_by": (params.get("by") or "").strip()[:40] or None,
        "added_at": _dt.datetime.now(_JST).isoformat(timespec="seconds"),
    }
    title = (params.get("title") or "").strip()
    if title:
        item["title"] = title[:120]
    _share_put(sid, item)
    return web.json_response({"id": sid, "url": f"/?s={sid}"})


async def handle_get_share(request):
    item = _share_get(request.match_info["sid"])
    if item is None:
        return web.json_response({"error": "分享不存在"}, status=404)
    return web.json_response(item)


# ================= P5（ddPC 版）：77B 只读缓存＋预热＋/v3 富模式 =================
# 上面的 /share、/t 是 ddPC 旧版原样（只读写本地 ttsweb.db 的 shares，简易模式）；
# 下面新增：经 77B HTTPS 只读 API（ddpc-cache 只读令牌）把 reading 库 ready 文章拉成本地只读缓存
# （独立的 cache.db，与 ttsweb.db 分开），供 /v3/t、/v3/articles 富模式使用；断网/77B 暂停时缓存直读。
# 零新增依赖：只用 aiohttp（已有）与标准库。令牌值只从 cache_token_file 读取，不进代码、配置与日志。
import aiohttp as _aiohttp

import ddpc_cache as _cachemod

_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
_CFG_PATH = os.environ.get("TTSWEB_CONFIG", os.path.join(_BASE_DIR, "config.json"))
_CFG_DEFAULTS = {
    "host": HOST, "port": PORT,
    "api_base": "https://8791-agq6dov7ieyr5kl67vfd2cqvce.instabox.tech",
    "cache_token_file": "",
    "cache_db": "cache.db",
    "shares_db": "ttsweb.db",
    "preheat_seconds": 3600,
}
try:
    with open(_CFG_PATH, encoding="utf-8") as _fh:
        CFG = dict(_CFG_DEFAULTS, **json.load(_fh))
except FileNotFoundError:
    CFG = dict(_CFG_DEFAULTS)


def _cfg_path(key):
    p = CFG.get(key) or ""
    return p if (not p or os.path.isabs(p)) else os.path.join(_BASE_DIR, p)


DB_FILE = _cfg_path("shares_db")          # 覆盖上方旧版常量（默认值相同：服务目录下 ttsweb.db）
CACHE = _cachemod.Cache(_cfg_path("cache_db"))


async def _in_thread(fn, *args):
    return await asyncio.to_thread(fn, *args)


def _read_cache_token():
    path = _cfg_path("cache_token_file")
    if not path:
        return None
    try:
        with open(path, encoding="utf-8") as fh:
            return fh.read().strip() or None
    except OSError:
        return None


async def _api_get(session, path, token):
    """GET 77B API；代理候选与 Edge 合成同口径（HTTPS_PROXY → 直连）。返回 JSON；失败抛异常。"""
    url = CFG["api_base"].rstrip("/") + path
    last = None
    for proxy in proxy_candidates():
        try:
            async with session.get(url, headers={"Authorization": "Bearer " + token}, proxy=proxy,
                                   ssl=SSL_CTX if url.startswith("https") else None) as resp:
                if resp.status != 200:
                    raise RuntimeError(f"HTTP {resp.status}")
                return await resp.json()
        except Exception as exc:  # noqa: BLE001 - 换下一条候选
            last = exc
    raise RuntimeError(f"{path}: {last!r}")


async def preheat_once():
    """一轮预热：列表 → 逐篇详情。单篇失败保留该篇旧缓存；整轮成功才替换列表快照并清理已下线文章。"""
    token = _read_cache_token()
    if not token:
        log.warning("preheat skipped: cache_token_file 未配置或不可读")
        return False
    timeout = _aiohttp.ClientTimeout(total=30)
    ok_all, snapshot, fails = True, [], []
    try:
        async with _aiohttp.ClientSession(timeout=timeout, trust_env=False) as session:
            listing = await _api_get(session, "/api/v1/articles", token)
            ready = [a for a in listing.get("articles", []) if a.get("status") == "ready"]
            for a in ready:
                sid = a["share_id"]
                try:
                    art = (await _api_get(session, f"/api/v1/articles/{sid}", token))["article"]
                    item = _cachemod.to_v3_payload(art)
                    bad = _cachemod.validate_payload(item)
                    if bad:
                        raise RuntimeError(f"payload 不完整：{bad}")
                    await _in_thread(CACHE.put, item, art.get("alt_ids") or [])
                    snapshot.append(_cachemod.list_entry(item))
                except Exception as exc:  # noqa: BLE001 - 单篇失败不影响其他篇
                    ok_all = False
                    fails.append(sid)
                    log.warning("preheat article %s failed (kept old cache): %r", sid, exc)
    except Exception as exc:  # noqa: BLE001 - 整轮失败：旧缓存原样保留
        log.warning("preheat round failed (kept old cache): %r", exc)
        return False
    if ok_all:
        snapshot.sort(key=lambda e: e.get("added_at") or "", reverse=True)
        await _in_thread(CACHE.set_list, snapshot)
        gone = await _in_thread(CACHE.prune, [e["share_id"] for e in snapshot])
        log.info("preheat ok: %d ready articles cached, list snapshot replaced, pruned=%s", len(snapshot), gone)
    else:
        log.warning("preheat partial: ok=%d failed=%s (list snapshot NOT replaced)", len(snapshot), fails)
    return ok_all


async def _preheat_loop(_app):
    while True:
        try:
            await preheat_once()
        except Exception as exc:  # noqa: BLE001 - 后台任务永不退出
            log.warning("preheat loop error: %r", exc)
        await asyncio.sleep(max(60, int(CFG.get("preheat_seconds") or 3600)))


async def _start_preheat(app):
    app["preheat_task"] = asyncio.ensure_future(_preheat_loop(app))


async def _stop_preheat(app):
    task = app.get("preheat_task")
    if task:
        task.cancel()


def _page(name):
    async def handler(_request):
        try:
            with open(os.path.join(_BASE_DIR, name), encoding="utf-8") as fh:
                page = fh.read()
        except OSError:
            return web.Response(status=404, text=f"{name} missing")
        return web.Response(text=page, content_type="text/html", charset="utf-8",
                            headers={"Cache-Control": "no-store"})
    return handler


handle_v3 = _page("player_v3.html")
handle_v4 = _page("player_v4.html")


async def handle_vue_js(_request):
    try:
        with open(os.path.join(_BASE_DIR, "vue.global.prod.js"), "rb") as fh:
            data = fh.read()
    except OSError:
        return web.Response(status=404, text="vue.global.prod.js missing")
    return web.Response(body=data, content_type="application/javascript",
                        headers={"Cache-Control": "no-store"})


async def handle_v3_share(request):
    """GET /v3/t/<id>：缓存（share_id 或 alt_ids）→ 富数据；未命中 → 本地 shares（旧形状、页面简易模式）；都无 → 404。"""
    sid = request.match_info["sid"]
    item = await _in_thread(CACHE.get, sid)
    if item is None:
        item = await _in_thread(_share_get, sid)
    if item is None:
        return web.json_response({"error": "分享不存在"}, status=404)
    return web.json_response(item, headers={"Cache-Control": "no-store"})


async def handle_v3_articles(_request):
    """GET /v3/articles：只列缓存中的 ready 文章（与 77B 同形状 6 字段）；缓存为空 → 503（页面提示选文不可用）。"""
    entries, _fetched = await _in_thread(CACHE.get_list)
    if not entries:
        return web.json_response({"error": "选文列表暂不可用（缓存为空，等待预热）"}, status=503,
                                 headers={"Cache-Control": "no-store"})
    return web.json_response({"articles": [{k: e.get(k) for k in _cachemod.LIST_KEYS} for e in entries]},
                             headers={"Cache-Control": "no-store"})


async def handle_v2(_request):
    """试验版页面（Claude 改版）：/v2 读取 player_v2.html，与现行版并行试用。"""
    html_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "player_v2.html")
    try:
        with open(html_path, encoding="utf-8") as fh:
            page = fh.read()
    except OSError:
        return web.Response(status=404, text="player_v2.html missing")
    return web.Response(text=page, content_type="text/html", charset="utf-8",
                        headers={"Cache-Control": "no-store"})


async def handle_hlsjs(_request):
    """hls.js 库文件（桌面 Chrome 走 MSE 播放 HLS，绕开其原生 HLS 的串段问题）。"""
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "hls.min.js")
    try:
        with open(path, "rb") as fh:
            data = fh.read()
    except OSError:
        return web.Response(status=404, text="hls.min.js missing")
    return web.Response(body=data, content_type="application/javascript",
                        headers={"Cache-Control": "public, max-age=86400"})


async def handle_health(_request):
    return web.json_response({"ok": True})


async def handle_index(_request):
    # 2026-11-15 起「日语朗读页」(player_v2.html) 转正为默认页；旧版 player.html 留盘备份。
    # P5（ddPC）：默认页切到 player_v3.html；v2 留 /v2 作回退入口。
    # 2026-11-20（ddPC）：v4 定版同步，默认页切到 player_v4.html；v3 留 /v3 作回退入口。
    html_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "player_v4.html")
    try:
        with open(html_path, encoding="utf-8") as fh:
            page = fh.read()
    except OSError:
        page = "<html><body>muse-tts-web: player.html missing</body></html>"
    return web.Response(text=page, content_type="text/html", charset="utf-8",
                        headers={"Cache-Control": "no-store"})


def make_app():
    app = web.Application()
    app.router.add_post("/tts", handle_tts)
    app.router.add_get("/tts", handle_tts)
    app.router.add_post("/tts2", handle_tts_aac)
    app.router.add_get("/tts2", handle_tts_aac)
    app.router.add_post("/tts3", handle_tts_adts)
    app.router.add_get("/tts3", handle_tts_adts)
    app.router.add_post("/play", handle_play)
    app.router.add_post("/share", handle_share)
    app.router.add_get("/t/{sid}", handle_get_share)
    app.router.add_get("/hls/{sid}/{file}", handle_hls_file)
    app.router.add_get("/health", handle_health)
    app.router.add_get("/hls.min.js", handle_hlsjs)
    app.router.add_get("/", handle_index)
    app.router.add_get("/v2", handle_v2)
    app.router.add_get("/v3", handle_v3)
    app.router.add_get("/v4", handle_v4)
    app.router.add_get("/v3/t/{sid}", handle_v3_share)
    app.router.add_get("/v3/articles", handle_v3_articles)
    app.router.add_get("/vue.global.prod.js", handle_vue_js)
    app.on_startup.append(_start_preheat)          # P5：启动即预热一轮，之后每 preheat_seconds 一轮
    app.on_cleanup.append(_stop_preheat)
    return app


if __name__ == "__main__":
    web.run_app(make_app(), host=CFG.get("host", HOST), port=int(CFG.get("port", PORT)), access_log=None)
