"""Shared helpers for agnes-service: settings (.env) and the JSON API error type."""

import os
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
ENV_FILE = os.path.join(HERE, ".env")

_env_cache = {"mtime": None, "values": {}}
_env_lock = threading.Lock()


def _read_env_file():
    """Parse ./.env (KEY=VALUE lines); re-read only when its mtime changes."""
    try:
        mtime = os.stat(ENV_FILE).st_mtime
    except OSError:
        return {}
    with _env_lock:
        if _env_cache["mtime"] != mtime:
            values = {}
            try:
                with open(ENV_FILE, encoding="utf-8") as fh:
                    for line in fh:
                        line = line.strip()
                        if not line or line.startswith("#") or "=" not in line:
                            continue
                        k, v = line.split("=", 1)
                        k = k.strip()
                        if k.startswith("export "):
                            k = k[7:].strip()
                        values[k] = v.strip().strip('"').strip("'")
            except OSError:
                values = {}
            _env_cache.update(mtime=mtime, values=values)
        return _env_cache["values"]


def setting(name, default=""):
    """Environment wins; falls back to ./.env so values added later work without restart."""
    val = os.environ.get(name, "").strip()
    if val:
        return val
    return _read_env_file().get(name, "").strip() or default


def int_setting(name, default):
    try:
        return int(setting(name, str(default)))
    except ValueError:
        return default


class ApiError(Exception):
    """An error that maps to a JSON response {"error": ..., ...} with an HTTP status."""

    def __init__(self, status, message, upstream_status=None, upstream_body=None,
                 code=None, headers=None, extra=None):
        super().__init__(message)
        self.status = status
        self.message = message
        self.upstream_status = upstream_status
        self.upstream_body = upstream_body
        self.code = code
        self.headers = headers or []
        self.extra = extra or {}

    def payload(self):
        p = {"error": self.message}
        if self.code:
            p["code"] = self.code
        if self.upstream_status is not None:
            p["upstream_status"] = self.upstream_status
        if self.upstream_body:
            p["upstream_body"] = self.upstream_body
        p.update(self.extra)
        return p
