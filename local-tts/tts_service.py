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

HOST = "127.0.0.1"
PORT = 8790
MAX_TEXT_CHARS = 5000
SEGMENT_MAX_CHARS = 250
OUTPUT_FORMAT = "audio-24khz-48kbitrate-mono-mp3"
SSL_CTX = ssl.create_default_context(cafile="/etc/ssl/certs/ca-certificates.crt")
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


async def handle_health(_request):
    return web.json_response({"ok": True})


async def handle_index(_request):
    return web.Response(
        text=(
            "muse-tts: 微软 Edge TTS 流式服务\n"
            "POST /tts {text, lang|voice, rate, pitch, volume}\n"
            "GET  /tts?text=...&lang=ja\n"
            "GET  /health\n"
        ),
        content_type="text/plain",
        charset="utf-8",
    )


def make_app():
    app = web.Application()
    app.router.add_post("/tts", handle_tts)
    app.router.add_get("/tts", handle_tts)
    app.router.add_get("/health", handle_health)
    app.router.add_get("/", handle_index)
    return app


if __name__ == "__main__":
    web.run_app(make_app(), host=HOST, port=PORT, access_log=None)
