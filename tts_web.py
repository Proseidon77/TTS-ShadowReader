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
# 2026-11-18 P2a：数据源改为 PG（db77database 的 reading schema）优先，PG 不可用时回落
# 本地 SQLite（config.sqlite_path；生产指向现行 ttsweb.db）。页面与 /t、/share 的响应格式不变。
# 新增 Agent 入库接口 /api/v1/*（preview / commit / 只读 GET）与 /ingest.md、/ingest_prompt.md。
import datetime as _dt

import ingest as _ingest
from auth import Auth as _Auth
from store import PgUnavailable as _PgUnavailable
from store import Store as _Store

_JST = _dt.timezone(_dt.timedelta(hours=9))
_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
_CFG_PATH = os.environ.get("TTSWEB_CONFIG", os.path.join(_BASE_DIR, "config.json"))
CFG = json.load(open(_CFG_PATH, encoding="utf-8"))
STORE = _Store(CFG, _BASE_DIR)
AUTH = _Auth(os.path.join(_BASE_DIR, CFG.get("tokens_file", "tokens.json")))
GRACE_UNTIL = _dt.date.fromisoformat(CFG.get("grace_until", "2026-12-02"))
API_MAX_BODY = 512 * 1024


def _client_ip(request):
    return request.headers.get("X-Forwarded-For", "").split(",")[0].strip() or request.remote or "?"


async def _in_thread(fn, *args):
    return await asyncio.to_thread(fn, *args)


async def handle_share(request):
    """POST /share {text, lang?, rate?, title?, by?, added_via?} -> {"id", "url", ...}（原有两字段不变，其余为加性字段）。

    第二期（DF-2，§5 契约）：added_via=agent|web，只认带写令牌的调用（缺省按令牌推断为 agent）；不带令牌的旧调用
    一律记 legacy。网页通道由 agnes-service 用自己的服务令牌代调（by=web、added_via=web，令牌 can_act_for 含 web），
    本服务不为网页通道另写任何逻辑（PHASE2-AUTH-DESIGN）。

    内部：「只交原文」commit——规范化→分句→骨架→按 content_hash 去重（命中返回已有 ID）→status=partial。
    令牌：grace_until 之前可不带（日志记 no-token，by 原样写 added_by 但标未验证）；之后必须带写令牌。
    PG 不可用时回落本地 SQLite（日志标 fallback）。
    """
    ip = _client_ip(request)
    try:
        who = AUTH.identify(request.headers.get("Authorization"), ip)
    except _ingest.ApiError as exc:
        return web.json_response({"error": exc.message}, status=exc.status)
    if who is None:
        if _dt.datetime.now(_JST).date() >= GRACE_UNTIL:
            return web.json_response({"error": "缺少令牌（宽限期已过）"}, status=401)
        log.info("share no-token call ip=%s (grace until %s)", ip, GRACE_UNTIL)
    elif who[1] != "write":
        return web.json_response({"error": "该令牌只有读权限"}, status=403)
    try:
        params = await request.json()
    except (json.JSONDecodeError, ValueError):
        return web.json_response({"error": "请求体不是合法 JSON"}, status=400)
    if not isinstance(params, dict):
        return web.json_response({"error": "请求体必须是 JSON 对象"}, status=400)
    text = _ingest.T.normalize(params.get("text") or "")
    if not text:
        return web.json_response({"error": "缺少 text"}, status=400)
    if len(text) > MAX_TEXT_CHARS:
        return web.json_response({"error": f"text 超过 {MAX_TEXT_CHARS} 字上限"}, status=400)
    rate = params.get("rate") or "-20%"
    if not RATE_RE.match(rate):
        return web.json_response({"error": "rate 需如 -20%"}, status=400)
    try:
        via = _ingest.check_added_via(params)
    except _ingest.ApiError as exc:
        return web.json_response({"error": exc.message}, status=exc.status)
    if who is None:
        via = "legacy"            # 不带令牌（宽限期内的旧调用）：通道不可信，一律 legacy
    elif via is None:
        via = "agent"
    by = (params.get("by") or "").strip()[:40] or None
    if who is not None:
        try:
            by = AUTH.resolve_added_by(who, by)
        except _ingest.ApiError as exc:
            return web.json_response({"error": exc.message}, status=exc.status)
    meta = {"lang": (params.get("lang") or "ja").lower(), "rate": rate,
            "title": ((params.get("title") or "").strip()[:120] or None), "added_by": by,
            "added_via": via, "caller": who[0] if who else None}
    try:
        plan = await _in_thread(_ingest.plan_share, text)
        sid = await _in_thread(STORE.share, plan, meta)
    except _PgUnavailable as exc:
        log.warning("share fallback to sqlite: %s", exc)
        sid = uuid.uuid4().hex[:10]
        item = {"text": text, "lang": meta["lang"], "rate": rate, "added_by": by,
                "added_at": _dt.datetime.now(_JST).isoformat(timespec="seconds")}
        if meta["title"]:
            item["title"] = meta["title"]
        await _in_thread(STORE.sqlite_put, sid, item)
    resp = {"id": sid, "url": f"/?s={sid}", "v3_url": f"/v3?s={sid}"}
    if meta.get("_duplicate"):
        resp["duplicate"] = meta["_duplicate"]
    if who is not None:
        log.info("share sid=%s caller=%s added_by=%s via=%s dup=%s", sid, meta["caller"], by, via, bool(meta.get("_duplicate")))
    return web.json_response(resp)


async def handle_get_share(request):
    sid = request.match_info["sid"]
    try:
        item = await _in_thread(STORE.get_legacy_public, sid)
    except _PgUnavailable as exc:
        log.warning("get_share fallback to sqlite sid=%s: %s", sid, exc)
        item = await _in_thread(STORE.sqlite_get, sid)
    if item is None:
        return web.json_response({"error": "分享不存在"}, status=404)
    return web.json_response(item)


# ---------------- /api/v1：Agent 入库接口（定版 8.3） ----------------
def _api_error(exc):
    headers = {}
    if exc.code == "RETRY_LATER" and isinstance(exc.details, dict) and exc.details.get("retry_after"):
        headers["Retry-After"] = str(exc.details["retry_after"])
    return web.json_response(exc.body(), status=exc.status, headers=headers)


async def _api_body(request):
    if request.content_length is not None and request.content_length > API_MAX_BODY:
        raise _ingest.ApiError("BODY_INVALID", "请求体超过 512KB", "拆分文章后重提",
                               {"content_length": request.content_length})
    try:
        raw = await request.read()
    except web.HTTPRequestEntityTooLarge:
        raise _ingest.ApiError("BODY_INVALID", "请求体超过 512KB", "拆分文章后重提") from None
    if len(raw) > API_MAX_BODY:
        raise _ingest.ApiError("BODY_INVALID", "请求体超过 512KB", "拆分文章后重提", {"bytes": len(raw)})
    try:
        body = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise _ingest.ApiError("BODY_INVALID", "请求体不是合法 JSON", str(exc)) from None
    if not isinstance(body, dict):
        raise _ingest.ApiError("BODY_INVALID", "请求体必须是 JSON 对象")
    return body


def _api(scope):
    def deco(fn):
        async def wrapper(request):
            try:
                who = AUTH.require(request.headers.get("Authorization"), _client_ip(request), scope)
                return await fn(request, who)
            except _ingest.ApiError as exc:
                return _api_error(exc)
            except _PgUnavailable as exc:
                log.warning("api pg unavailable: %s", exc)
                return _api_error(_ingest.ApiError("RETRY_LATER", "数据库暂不可用", "稍后重试（指数退避，最多 5 次）",
                                                   status=503))
        return wrapper
    return deco


@_api("read")
async def handle_api_preview(request, who):
    body = await _api_body(request)
    return web.json_response(await _in_thread(_ingest.preview, body))


@_api("write")
async def handle_api_commit(request, who):
    body = await _api_body(request)
    added_by = AUTH.resolve_added_by(who, body.get("added_by"))
    plan = await _in_thread(_ingest.plan_commit, body)
    result = await _in_thread(STORE.commit, plan, added_by, who[0])
    result["warnings"] = plan["warnings"]
    log.info("commit caller=%s added_by=%s via=%s share_id=%s dup=%s v=%s status=%s",
             who[0], added_by, plan.get("added_via"), result["share_id"], result["duplicate"], result["version"], result["status"])
    return web.json_response(result)


@_api("read")
async def handle_api_readings_check(request, who):
    """第二期 §5：词典佐证（read 权限）。agnes-service 的 fixes 核对层第 2 层；不可用时对方退回本地分流。"""
    body = await _api_body(request)
    return web.json_response(await _in_thread(_ingest.readings_check, body))


@_api("read")
async def handle_api_articles(request, who):
    since = request.query.get("since")
    if since:
        try:
            since = _dt.datetime.fromisoformat(since)
        except ValueError:
            raise _ingest.ApiError("BODY_INVALID", "since 需为 ISO 时间", "", {"since": since}) from None
    # 2026-11-20 删改：include_deleted=1（须与 since 同用）另回墓碑行；缺省输出与之前相同（另加 sentences 句数字段）
    include_deleted = request.query.get("include_deleted") in ("1", "true")
    return web.json_response({"ok": True, "articles": await _in_thread(STORE.list_articles, since, 500, include_deleted)})


@_api("read")
async def handle_api_article(request, who):
    return web.json_response({"ok": True, "article": await _in_thread(STORE.get_article, request.match_info["sid"])})


_SID_RE = re.compile(r"^[0-9a-f]{10}$")


def _check_sid(sid):
    if not _SID_RE.match(sid or ""):
        raise _ingest.ApiError("NOT_FOUND", "文章不存在", "短链 ID 为 10 位十六进制", {"id": (sid or "")[:20]})
    return sid


@_api("write")
async def handle_api_article_delete(request, who):
    """DELETE /api/v1/articles/{sid}（2026-11-20 删改）：写令牌；软删（置 deleted_at），不物理删。
    与 commit replace 同一鉴权口径（写令牌即可，不按 added_by 限制）；「谁能删哪篇」由调用方（agnes-service
    按账号归属）把关。删后 /t、/v3/t → 404，/v3/articles 与 /api/v1/articles 不再列出；ddPC 下一轮预热 prune。"""
    sid = _check_sid(request.match_info["sid"])
    res = await _in_thread(STORE.soft_delete, sid)
    log.info("article soft-deleted sid=%s caller=%s", res["share_id"], who[0])
    return web.json_response({"ok": True, **res})


@_api("write")
async def handle_api_article_patch(request, who):
    """PATCH /api/v1/articles/{sid}（2026-11-20 删改）：只改 title / lang；不动正文、句子与版本。
    请求体只收 title、lang（至少一个），其他字段 BODY_INVALID。"""
    sid = _check_sid(request.match_info["sid"])
    body = await _api_body(request)
    extra = sorted(k for k in body if k not in ("title", "lang"))
    if extra:
        raise _ingest.ApiError("BODY_INVALID", f"不支持的字段 {extra[0]}", "PATCH 只改 title / lang；改正文走 commit replace",
                               {"path": extra[0]})
    title = _ingest.need_str(body, "title", required=False, maxlen=120)
    title = title.strip() if title else None
    lang = body.get("lang")
    if lang is not None:
        if not isinstance(lang, str) or lang.lower() not in _ingest.LANGS:
            raise _ingest.ApiError("BODY_INVALID", "lang 只能是 ja/zh/en", "", {"path": "lang"})
        lang = lang.lower()
    if title is None and lang is None:
        raise _ingest.ApiError("BODY_INVALID", "至少提供 title 或 lang 之一", "", {"path": "title"})
    art = await _in_thread(STORE.update_meta, sid, title, lang)
    log.info("article meta updated sid=%s caller=%s fields=%s", art["share_id"], who[0],
             ",".join(k for k, v in (("title", title), ("lang", lang)) if v is not None))
    return web.json_response({"ok": True, "article": art})


# ---------------- P4（加性）：/v3 播放页、Vue 静态文件、/v3/t/<id> 带句子的取文口 ----------------
async def handle_v3(_request):
    """播放页 v3（Vue 3，同页上卡下列）：/v3 读取 player_v3.html，与默认页 v2 并行试用。"""
    try:
        with open(os.path.join(_BASE_DIR, "player_v3.html"), encoding="utf-8") as fh:
            page = fh.read()
    except OSError:
        return web.Response(status=404, text="player_v3.html missing")
    return web.Response(text=page, content_type="text/html", charset="utf-8",
                        headers={"Cache-Control": "no-store"})


async def handle_v4(_request):
    """播放页 v4（v3 的视觉重设计，2026-11-20）：/v4 读取 player_v4.html，与 v3、默认页 v2 并行试看。"""
    try:
        with open(os.path.join(_BASE_DIR, "player_v4.html"), encoding="utf-8") as fh:
            page = fh.read()
    except OSError:
        return web.Response(status=404, text="player_v4.html missing")
    return web.Response(text=page, content_type="text/html", charset="utf-8",
                        headers={"Cache-Control": "no-store"})


async def handle_vue_js(_request):
    try:
        with open(os.path.join(_BASE_DIR, "vue.global.prod.js"), "rb") as fh:
            data = fh.read()
    except OSError:
        return web.Response(status=404, text="vue.global.prod.js missing")
    return web.Response(body=data, content_type="application/javascript",
                        headers={"Cache-Control": "no-store"})


async def handle_v3_share(request):
    """GET /v3/t/<id>：旧 /t 字段＋当前版本 sentences（免令牌，短链即凭据，与 /t 同等暴露面）。
    PG 不可用时回落 SQLite（无 sentences → 页面进简易模式）。"""
    sid = request.match_info["sid"]
    try:
        item = await _in_thread(STORE.get_with_sentences, sid)
    except _PgUnavailable as exc:
        log.warning("v3 get_share fallback to sqlite sid=%s: %s", sid, exc)
        item = await _in_thread(STORE.sqlite_get, sid)
    if item is None:
        return web.json_response({"error": "分享不存在"}, status=404)
    return web.json_response(item)


async def handle_v3_articles(_request):
    """GET /v3/articles（P4b 加性，免令牌）：v3 选文列表。只回公开字段 share_id/title/sentences/added_at/
    added_by/lang；只含未删除且 ready 的文章；不回正文、译文、tokens。PG 不可用 → 503（页面提示选文不可用）。"""
    try:
        items = await _in_thread(STORE.list_public)
    except _PgUnavailable as exc:
        log.warning("v3 articles unavailable: %s", exc)
        return web.json_response({"error": "选文列表暂不可用"}, status=503,
                                 headers={"Cache-Control": "no-store"})
    return web.json_response({"articles": items}, headers={"Cache-Control": "no-store"})


def _md_handler(name):
    async def handler(_request):
        try:
            with open(os.path.join(_BASE_DIR, name), encoding="utf-8") as fh:
                body = fh.read()
        except OSError:
            return web.Response(status=404, text=f"{name} missing")
        return web.Response(text=body, content_type="text/markdown", charset="utf-8",
                            headers={"Cache-Control": "no-cache"})
    return handler


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
    # 2026-11-20 起 v4（player_v4.html，Animal Island 主题，两轮走查改版后定版）转正为默认页；v2（2026-11-15 转正）与 v3 仍可经 /v2、/v3 访问。
    html_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "player_v4.html")
    try:
        with open(html_path, encoding="utf-8") as fh:
            page = fh.read()
    except OSError:
        page = "<html><body>muse-tts-web: player.html missing</body></html>"
    return web.Response(text=page, content_type="text/html", charset="utf-8",
                        headers={"Cache-Control": "no-store"})


def make_app():
    app = web.Application(client_max_size=API_MAX_BODY + 1024)
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
    app.router.add_post("/api/v1/ingest/preview", handle_api_preview)
    app.router.add_post("/api/v1/ingest/commit", handle_api_commit)
    app.router.add_get("/api/v1/articles", handle_api_articles)
    app.router.add_get("/api/v1/articles/{sid}", handle_api_article)
    app.router.add_delete("/api/v1/articles/{sid}", handle_api_article_delete)
    app.router.add_patch("/api/v1/articles/{sid}", handle_api_article_patch)
    app.router.add_post("/api/v1/ingest/readings_check", handle_api_readings_check)
    app.router.add_get("/ingest.md", _md_handler("ingest.md"))
    app.router.add_get("/ingest_prompt.md", _md_handler("ingest_prompt.md"))
    app.router.add_get("/v3", handle_v3)
    app.router.add_get("/v3/t/{sid}", handle_v3_share)
    app.router.add_get("/v3/articles", handle_v3_articles)
    app.router.add_get("/v4", handle_v4)
    app.router.add_get("/vue.global.prod.js", handle_vue_js)
    return app


if __name__ == "__main__":
    web.run_app(make_app(), host=CFG.get("host", HOST), port=int(CFG.get("port", PORT)), access_log=None)
