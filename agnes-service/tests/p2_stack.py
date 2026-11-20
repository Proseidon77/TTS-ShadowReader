#!/usr/bin/env python3
"""TASK6 phase 2 dev stack helper (never touches 8793 / 8791 or publish/).

  python3 tests/p2_stack.py build                     dotnet build -c Release (service) + MockAgnes
  python3 tests/p2_stack.py start [overlay …]         MockAgnes :18793 + dev instance :8795 (isolated DB agnes_p2dev)
  python3 tests/p2_stack.py restart [overlay …]       restart only the 8795 instance (in-memory state reset)
  python3 tests/p2_stack.py stop                      stop 8795 + MockAgnes (+ dev TtsWeb if started here)
  python3 tests/p2_stack.py ttsweb                    start the dev TtsWeb copy on :8796 (tts-redesign/impl/phase2)

overlays are env files under tests/ (e.g. mock8795.env ingest_mock.env); later files win.
"""
import os
import signal
import subprocess
import sys
import time
import urllib.request

ROOT = "/data/workspace/agnes-service"
DOTNET = "/data/dotnet-sdk/dotnet"
PROBE = ROOT + "/.probe"
os.makedirs(PROBE, exist_ok=True)
# dev TtsWeb copy; 2026-11-20 delete/edit work uses P2_TTS_DIR=/data/workspace/tts-redesign/impl/deledit
TTS_DIR = os.environ.get("P2_TTS_DIR", "/data/workspace/tts-redesign/impl/deledit")
TTS_PY = "/data/workspace/tts-web/venv/bin/python"


def wait(url, secs=60):
    t = time.time()
    while time.time() - t < secs:
        try:
            with urllib.request.urlopen(url, timeout=2) as r:
                if r.status == 200:
                    return True
        except Exception:  # noqa: BLE001
            time.sleep(0.5)
    return False


def pidfile(name):
    return f"{PROBE}/p2_{name}.pid"


def kill(name):
    try:
        pid = int(open(pidfile(name)).read())
        os.kill(pid, signal.SIGTERM)
        for _ in range(40):
            time.sleep(0.25)
            os.kill(pid, 0)
    except (FileNotFoundError, ProcessLookupError, ValueError):
        pass
    try:
        os.remove(pidfile(name))
    except FileNotFoundError:
        pass


def spawn(name, args, log, cwd=ROOT, env=None):
    fh = open(f"{PROBE}/{log}", "ab")
    p = subprocess.Popen(args, cwd=cwd, stdout=fh, stderr=subprocess.STDOUT, start_new_session=True, env=env)
    open(pidfile(name), "w").write(str(p.pid))
    return p


def start_service(overlays):
    args = [DOTNET, "bin/Release/net10.0/AgnesService.dll", "--env-file", ".env", "--env-file", "tests/port8795.env",
            "--env-file", "tests/devdb8795.env"]
    for o in overlays:
        args += ["--env-file", o if "/" in o else "tests/" + o]
    spawn("svc", args, "agnes-8795.log")
    ok = wait("http://127.0.0.1:8795/health")
    print("8795", "up" if ok else "NOT UP", overlays)


def main():
    cmd = sys.argv[1]
    rest = sys.argv[2:]
    if cmd == "build":
        r1 = subprocess.run([DOTNET, "build", "-c", "Release", "-nologo", "-v", "q"], cwd=ROOT)
        r2 = subprocess.run([DOTNET, "build", "tests/MockAgnes", "-c", "Release", "-o", "tests/MockAgnes/out", "-nologo", "-v", "q"], cwd=ROOT)
        sys.exit(r1.returncode or r2.returncode)
    if cmd == "start":
        kill("mock")
        spawn("mock", [DOTNET, "tests/MockAgnes/out/MockAgnes.dll"], "mock-18793.log")
        print("mock", "up" if wait("http://127.0.0.1:18793/v1/models") else "NOT UP")
        kill("svc")
        start_service(rest)
    elif cmd == "crash":   # SIGKILL (no graceful shutdown) then start again: exercises startup recovery
        try:
            os.kill(int(open(pidfile("svc")).read()), signal.SIGKILL)
            time.sleep(1)
        except (FileNotFoundError, ProcessLookupError, ValueError):
            pass
        start_service(rest)
    elif cmd == "restart":
        kill("svc")
        start_service(rest)
    elif cmd == "stop":
        kill("svc")
        kill("mock")
        kill("tts")
        print("stopped")
    elif cmd in ("selftest", "accept1", "accept2"):
        # run a suite, keep the full log in .probe/, print only the summary + failures
        env = dict(os.environ, AGNES_TEST_DB="agnes_p2dev",
                   AGNES_TEST_ENV_FILES=f"{ROOT}/.env,{ROOT}/tests/devdb8795.env")
        if cmd == "selftest":
            args = [DOTNET, "bin/Release/net10.0/AgnesService.dll", "--self-test"]
        elif cmd == "accept1":
            args = ["python3", "tests/task6_accept.py", rest[0] if rest else "mock", "--base", "http://127.0.0.1:8795"]
        else:
            args = ["python3", "tests/p2_accept.py", *rest]
        log = f"{PROBE}/p2_{cmd}_{'_'.join(rest) or 'default'}.log"
        r = subprocess.run(args, cwd=ROOT, env=env, capture_output=True, text=True)
        open(log, "w").write(r.stdout + r.stderr)
        lines = (r.stdout + r.stderr).splitlines()
        for ln in lines:
            if "FAIL" in ln or "Traceback" in ln or "Error" in ln:
                print(ln[:400])
        print("\n".join(lines[-3:]))
        print("exit", r.returncode, "log", log)
    elif cmd == "ttsweb":
        kill("tts")
        env = dict(os.environ, TTSWEB_CONFIG=TTS_DIR + "/config_dev.json")
        spawn("tts", [TTS_PY, TTS_DIR + "/tts_web.py"], "ttsweb-8796.log", cwd=TTS_DIR, env=env)
        print("ttsweb 8796", "up" if wait("http://127.0.0.1:8796/health", 90) else "NOT UP")


main()
