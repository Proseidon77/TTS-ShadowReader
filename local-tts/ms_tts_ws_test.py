#!/usr/bin/env python3
"""最小流式客户端原型：websockets 直连微软 Edge ReadAloud WSS 协议。

协议辅助借 edge-tts==7.3.1（DRM.generate_sec_ms_gec / headers_with_muid /
mkssml / ssml_headers_plus_data / TTSConfig / 常量），传输层不用 edge_tts
自带的 aiohttp——代理环境下它的 WSS 握手会被拒（101 'Invalid connection
header'）。websockets 直连时：去掉重复的 Sec-WebSocket-Version 头，
Origin 用库的 origin 参数单独传。

代理策略：候选列表 [环境变量 HTTPS_PROXY, 直连]，未出音频前失败就换下一条。
TLS 用系统 CA 包（代理的 CA 只在系统包里，不用 certifi）。

用法：./venv/bin/python ms_tts_ws_test.py "テキスト" [voice]
产物：test-out.mp3；打印块数、总字节、首块延迟。
"""

import asyncio
import json
import os
import ssl
import sys
import time
from xml.sax.saxutils import escape

import websockets
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

SSL_CTX = ssl.create_default_context(cafile="/etc/ssl/certs/ca-certificates.crt")
ORIGIN = WSS_HEADERS.get("Origin")
HEADERS = {
    k: v
    for k, v in DRM.headers_with_muid(WSS_HEADERS).items()
    if k not in ("Sec-WebSocket-Version", "Origin")
}
OUTPUT_FORMAT = "audio-24khz-48kbitrate-mono-mp3"


def proxy_candidates():
    cands = []
    proxy = os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy")
    if proxy:
        cands.append(proxy)
    cands.append(None)  # 直连兜底
    return cands


async def stream_segment(text, voice, rate="+0%", volume="+0%", pitch="+0Hz"):
    """对一段文本流式产出 MP3 块；代理候选在首块音频到达前可切换。"""
    tts_config = TTSConfig(voice, rate, volume, pitch, "SentenceBoundary")
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
                additional_headers=HEADERS,
                origin=ORIGIN,
                ssl=SSL_CTX,
                proxy=proxy,
                max_size=None,
                open_timeout=10,
                close_timeout=5,
            ) as ws:
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
                async for msg in ws:
                    if isinstance(msg, str):
                        if "Path:turn.end" in msg:
                            break
                        continue
                    if len(msg) < 2:
                        continue
                    header_length = int.from_bytes(msg[:2], "big")
                    headers, data = get_headers_and_data(msg, header_length)
                    if headers.get(b"Path") != b"audio":
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


async def main():
    text = sys.argv[1] if len(sys.argv) > 1 else "こんにちは、世界。これはテストです。"
    voice = sys.argv[2] if len(sys.argv) > 2 else "ja-JP-NanamiNeural"
    started = time.perf_counter()
    first = None
    total = 0
    chunks = 0
    with open("test-out.mp3", "wb") as fh:
        async for chunk in stream_segment(text, voice):
            if first is None:
                first = time.perf_counter() - started
            fh.write(chunk)
            total += len(chunk)
            chunks += 1
    print(
        f"voice={voice} chunks={chunks} bytes={total} "
        f"first_chunk={first:.3f}s total={time.perf_counter() - started:.3f}s"
    )


if __name__ == "__main__":
    asyncio.run(main())
