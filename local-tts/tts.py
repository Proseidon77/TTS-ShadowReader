#!/usr/bin/env python3
"""muse-tts 命令行客户端（纯标准库，直连本机服务）。

用法：
  python3 tts.py "読み上げるテキスト" --lang ja --out out.mp3
  python3 tts.py "Hello world" --lang en --rate +10% --out hello.mp3
  python3 tts.py "你好，世界" --voice zh-CN-XiaoxiaoNeural --out zh.mp3

打印首字节延迟与总字节数，方便验收。服务默认 http://127.0.0.1:8790，
可用环境变量 MUSE_TTS_URL 覆盖。
"""

import argparse
import http.client
import json
import os
import sys
import time
import urllib.parse


def main():
    parser = argparse.ArgumentParser(description="调用本机 muse-tts 流式服务")
    parser.add_argument("text", help="要合成的文本")
    parser.add_argument("--lang", default="ja", help="ja/en/zh（默认 ja）")
    parser.add_argument("--voice", default=None, help="直接指定音色，优先于 --lang")
    parser.add_argument("--rate", default=None, help="语速，如 +10%")
    parser.add_argument("--pitch", default=None, help="音调，如 +0Hz")
    parser.add_argument("--out", default="out.mp3", help="输出文件（默认 out.mp3）")
    args = parser.parse_args()

    base = urllib.parse.urlsplit(os.environ.get("MUSE_TTS_URL", "http://127.0.0.1:8790"))
    payload = {"text": args.text, "lang": args.lang}
    if args.voice:
        payload["voice"] = args.voice
    if args.rate:
        payload["rate"] = args.rate
    if args.pitch:
        payload["pitch"] = args.pitch

    conn = http.client.HTTPConnection(base.hostname, base.port or 80, timeout=180)
    started = time.perf_counter()
    conn.request(
        "POST",
        "/tts",
        body=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    resp = conn.getresponse()
    if resp.status != 200:
        body = resp.read().decode("utf-8", "replace")
        print(f"服务返回 {resp.status}: {body}", file=sys.stderr)
        return 1

    first = None
    total = 0
    with open(args.out, "wb") as fh:
        while True:
            chunk = resp.read(65536)
            if not chunk:
                break
            if first is None:
                first = time.perf_counter() - started
            fh.write(chunk)
            total += len(chunk)
    elapsed = time.perf_counter() - started
    print(
        f"out={args.out} bytes={total} "
        f"first_byte={first:.3f}s total={elapsed:.3f}s"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
