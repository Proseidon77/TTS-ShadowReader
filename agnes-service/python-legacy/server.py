#!/usr/bin/env python3
"""Agnes API service: a small HTTP wrapper around the Agnes AI API for
text / image / video generation, plus a single-page frontend.

Run:  python3 server.py            (listens on $HOST:$PORT, default 0.0.0.0:8793)
Key:  AGNES_API_KEY from the environment, else from ./.env (never logged).
Auth: JWT (see auth.py); generation endpoints need "Authorization: Bearer <token>".
Deps: Python stdlib + psycopg (v3) for the auth database (only imported by auth.py).
"""

import ipaddress
import json
import logging
import os
import re
import socket
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import auth
from common import HERE, ApiError, setting

VERSION = "2.0.0"
INDEX_FILE = os.path.join(HERE, "index.html")

HOST = os.environ.get("HOST", "0.0.0.0")
PORT = int(os.environ.get("PORT", "8793"))
AGNES_BASE = os.environ.get("AGNES_BASE_URL", "https://apihub.agnes-ai.com").rstrip("/")

# Upstream timeouts (seconds). urllib applies them per socket operation, i.e. it is
# the max time to wait for the upstream to start answering / send the next chunk.
TEXT_TIMEOUT = float(os.environ.get("TEXT_TIMEOUT", "300"))
IMAGE_TIMEOUT = float(os.environ.get("IMAGE_TIMEOUT", "360"))
VIDEO_CREATE_TIMEOUT = float(os.environ.get("VIDEO_CREATE_TIMEOUT", "120"))
VIDEO_STATUS_TIMEOUT = float(os.environ.get("VIDEO_STATUS_TIMEOUT", "30"))
MODELS_TIMEOUT = 15.0

MAX_BODY = 20 * 1024 * 1024        # allows data-URI images for image-to-image
MAX_PROMPT = 8000
MAX_CONCURRENT = int(os.environ.get("MAX_CONCURRENT", "6"))  # generation calls in flight
ERR_SNIPPET = 600                  # chars of upstream error body we pass back

KNOWN_MODELS = {
    "text": ["agnes-2.5-flash", "agnes-2.5-pro", "agnes-2.5-pro-alpha",
             "agnes-2.5-pro-beta", "agnes-3.0-flash"],
    "image": ["agnes-image-2.5-flash", "agnes-image-2.1-flash", "agnes-image-2.0-flash"],
    "video": ["agnes-video-2.5-flash", "agnes-video-2.5"],
}
DEFAULTS = {"text": "agnes-2.5-flash", "image": "agnes-image-2.5-flash",
            "video": "agnes-video-2.5-flash"}

VIDEO_ID_RE = re.compile(r"^[A-Za-z0-9_.\-]{1,200}$")
MODEL_RE = re.compile(r"^[A-Za-z0-9_.\-]{1,100}$")
SHORT_RE = re.compile(r"^[A-Za-z0-9:x.\-]{1,20}$")   # size / ratio / aspect_ratio values

logging.basicConfig(level=logging.INFO, stream=sys.stdout,
                    format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("agnes-service")

START_TIME = time.time()
_gen_slots = threading.BoundedSemaphore(MAX_CONCURRENT)


# --------------------------------------------------------------------------- config

def api_key():
    return setting("AGNES_API_KEY")


def redact(text):
    for secret in (api_key(), setting("JWT_SECRET")):
        if secret and secret in text:
            text = text.replace(secret, "***")
    return text


# --------------------------------------------------------------------------- upstream

def agnes(method, path, body=None, timeout=60.0):
    key = api_key()
    if not key:
        raise ApiError(503, "AGNES_API_KEY not configured")
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(
        AGNES_BASE + path, data=data, method=method,
        headers={"Authorization": "Bearer " + key,
                 "Content-Type": "application/json",
                 "Accept": "application/json",
                 "User-Agent": "agnes-service/" + VERSION})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
            status = resp.status
    except urllib.error.HTTPError as e:
        try:
            raw = e.read()
        except Exception:
            raw = b""
        snippet = redact(raw.decode("utf-8", "replace")[:ERR_SNIPPET])
        log.warning("upstream %s %s -> HTTP %s in %.1fs", method, path.split("?")[0],
                    e.code, time.time() - t0)
        # Pass client-side errors through (400/401/403/404/429); map 5xx to 502.
        out = e.code if e.code in (400, 401, 402, 403, 404, 409, 413, 422, 429) else 502
        raise ApiError(out, "Agnes API returned HTTP %d" % e.code, e.code, snippet)
    except (socket.timeout, TimeoutError):
        log.warning("upstream %s %s timed out after %.1fs", method, path.split("?")[0],
                    time.time() - t0)
        raise ApiError(504, "Agnes API timed out after %ds" % int(timeout))
    except urllib.error.URLError as e:
        reason = getattr(e, "reason", e)
        if isinstance(reason, (socket.timeout, TimeoutError)):
            raise ApiError(504, "Agnes API timed out after %ds" % int(timeout))
        raise ApiError(502, "Cannot reach Agnes API: %s" % redact(str(reason))[:200])
    except OSError as e:
        raise ApiError(502, "Network error talking to Agnes API: %s" % redact(str(e))[:200])
    log.info("upstream %s %s -> HTTP %s in %.1fs", method, path.split("?")[0], status,
             time.time() - t0)
    try:
        return json.loads(raw.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        raise ApiError(502, "Agnes API returned non-JSON response", status,
                       redact(raw.decode("utf-8", "replace")[:ERR_SNIPPET]))


class gen_slot:
    """Cap concurrent generation calls so a public page can't fan out unbounded work."""

    def __enter__(self):
        if not _gen_slots.acquire(blocking=False):
            raise ApiError(429, "Too many generation requests in progress, try again shortly")

    def __exit__(self, *exc):
        _gen_slots.release()


# --------------------------------------------------------------------------- helpers

def req_str(body, name, required=False, max_len=MAX_PROMPT, pattern=None):
    v = body.get(name)
    if v is None or (isinstance(v, str) and not v.strip()):
        if required:
            raise ApiError(400, "'%s' is required" % name)
        return None
    if not isinstance(v, str):
        raise ApiError(400, "'%s' must be a string" % name)
    v = v.strip()
    if len(v) > max_len:
        raise ApiError(400, "'%s' is too long (max %d chars)" % (name, max_len))
    if pattern and not pattern.match(v):
        raise ApiError(400, "'%s' has an invalid value" % name)
    return v


def pick_model(body, kind):
    m = req_str(body, "model", pattern=MODEL_RE) or DEFAULTS[kind]
    return m


def opt_url(v, name):
    if v is None or v == "":
        return None
    if not isinstance(v, str) or len(v) > MAX_BODY:
        raise ApiError(400, "'%s' must be a URL or data URI string" % name)
    if not (v.startswith("http://") or v.startswith("https://") or v.startswith("data:")):
        raise ApiError(400, "'%s' must start with http(s):// or data:" % name)
    return v


def opt_url_list(v, name):
    if v is None or v == [] or v == "":
        return None
    if isinstance(v, str):
        v = [v]
    if not isinstance(v, list):
        raise ApiError(400, "'%s' must be a list of URLs" % name)
    return [opt_url(x, name) for x in v if x]


# --------------------------------------------------------------------------- handlers

_models_cache = {"t": 0.0, "data": None}


def get_models():
    """Group models by type. Proxies Agnes /v1/models when a key is set (cached 10 min)."""
    if api_key():
        if _models_cache["data"] and time.time() - _models_cache["t"] < 600:
            return _models_cache["data"]
        try:
            res = agnes("GET", "/v1/models", timeout=MODELS_TIMEOUT)
            ids = sorted({m.get("id") for m in res.get("data", []) if m.get("id")})
            if ids:
                grouped = {"text": [], "image": [], "video": []}
                for i in ids:
                    kind = "video" if "video" in i else "image" if "image" in i else "text"
                    grouped[kind].append(i)
                for k in grouped:   # keep our preferred default first
                    if DEFAULTS[k] in grouped[k]:
                        grouped[k].remove(DEFAULTS[k])
                        grouped[k].insert(0, DEFAULTS[k])
                data = {"source": "agnes", "models": grouped, "defaults": DEFAULTS}
                _models_cache.update(t=time.time(), data=data)
                return data
        except ApiError as e:
            log.warning("model list proxy failed (%s); using static list", e.message)
    return {"source": "static", "models": KNOWN_MODELS, "defaults": DEFAULTS}


def do_text(body):
    prompt = req_str(body, "prompt", required=True)
    system = req_str(body, "system")
    model = pick_model(body, "text")
    messages = []
    if system:
        messages.append({"role": "system", "content": system})
    messages.append({"role": "user", "content": prompt})
    payload = {"model": model, "messages": messages}
    for k in ("temperature", "top_p"):
        if isinstance(body.get(k), (int, float)) and not isinstance(body.get(k), bool):
            payload[k] = body[k]
    if isinstance(body.get("max_tokens"), int) and 0 < body["max_tokens"] <= 65536:
        payload["max_tokens"] = body["max_tokens"]
    with gen_slot():
        res = agnes("POST", "/v1/chat/completions", payload, timeout=TEXT_TIMEOUT)
    try:
        choice = res["choices"][0]
        msg = choice.get("message") or {}
    except (KeyError, IndexError, TypeError):
        raise ApiError(502, "Unexpected response shape from Agnes chat API", 200,
                       redact(json.dumps(res)[:ERR_SNIPPET]))
    out = {"text": msg.get("content") or "", "model": res.get("model", model)}
    if msg.get("reasoning_content"):
        out["reasoning"] = msg["reasoning_content"]
    if res.get("usage"):
        out["usage"] = res["usage"]
    if choice.get("finish_reason"):
        out["finish_reason"] = choice["finish_reason"]
    return out


def do_image(body):
    prompt = req_str(body, "prompt", required=True)
    model = pick_model(body, "image")
    size = req_str(body, "size", pattern=SHORT_RE) or "1K"
    ratio = req_str(body, "ratio", pattern=SHORT_RE)
    fmt = req_str(body, "response_format") or "url"
    if fmt not in ("url", "b64_json"):
        raise ApiError(400, "'response_format' must be 'url' or 'b64_json'")
    extra = {"response_format": fmt}            # MUST be inside extra_body (verified)
    image = body.get("image")
    if isinstance(image, list):
        image = opt_url_list(image, "image")
    else:
        image = opt_url(image, "image")
    if image:
        extra["image"] = image                  # image-to-image input
    payload = {"model": model, "prompt": prompt, "size": size, "extra_body": extra}
    if ratio:
        payload["ratio"] = ratio
    with gen_slot():
        res = agnes("POST", "/v1/images/generations", payload, timeout=IMAGE_TIMEOUT)
    try:
        item = res["data"][0]
    except (KeyError, IndexError, TypeError):
        raise ApiError(502, "Unexpected response shape from Agnes image API", 200,
                       redact(json.dumps(res)[:ERR_SNIPPET]))
    out = {"model": model, "size": size}
    if ratio:
        out["ratio"] = ratio
    if item.get("url"):
        out["url"] = item["url"]
    if item.get("b64_json"):
        out["b64"] = item["b64_json"]
    if item.get("revised_prompt"):
        out["revised_prompt"] = item["revised_prompt"]
    if "url" not in out and "b64" not in out:
        raise ApiError(502, "Agnes image API returned no url/b64_json", 200,
                       redact(json.dumps(res)[:ERR_SNIPPET]))
    return out


# video_id -> model, so status polls can send model_name without the client repeating it
_video_models = {}
_video_lock = threading.Lock()


def _video_view(res, fallback_id=None, model=None):
    err = res.get("error")
    if isinstance(err, dict):
        err = err.get("message") or json.dumps(err)[:300]
    out = {
        "id": res.get("video_id") or fallback_id or res.get("id"),
        "task_id": res.get("task_id") or res.get("id"),
        "model": res.get("model") or model,
        "status": res.get("status", "unknown"),
        "progress": res.get("progress"),
        "url": res.get("url") if res.get("status") == "completed" else None,
        "error": err or None,
    }
    for k in ("seconds", "size", "created_at", "completed_at", "expires_at"):
        if res.get(k) is not None:
            out[k] = res[k]
    return out


def do_video_create(body):
    prompt = req_str(body, "prompt", required=True)
    model = pick_model(body, "video")
    flash = model.endswith("-flash")
    payload = {"model": model, "prompt": prompt}
    seconds = body.get("seconds")
    if seconds is not None and seconds != "":
        try:
            s = int(str(seconds))
        except ValueError:
            raise ApiError(400, "'seconds' must be an integer 4-12")
        if not 4 <= s <= 12:
            raise ApiError(400, "'seconds' must be between 4 and 12")
        payload["seconds"] = str(s)             # upstream wants a string
    else:
        payload["seconds"] = "5"
    size = req_str(body, "size", pattern=SHORT_RE) or "720P"
    if size not in ("720P", "1080P", "1K", "2K"):
        raise ApiError(400, "'size' must be one of 720P, 1080P, 1K, 2K")
    if flash and size != "720P":
        raise ApiError(400, "%s only supports size 720P" % model)
    payload["size"] = size
    ar = req_str(body, "aspect_ratio", pattern=SHORT_RE)
    if ar:
        if ar not in ("21:9", "16:9", "4:3", "1:1", "3:4", "9:16"):
            raise ApiError(400, "'aspect_ratio' must be one of 21:9 16:9 4:3 1:1 3:4 9:16")
        payload["aspect_ratio"] = ar
    if isinstance(body.get("seed"), int) and not isinstance(body.get("seed"), bool):
        payload["seed"] = body["seed"]

    first = opt_url(body.get("first_frame"), "first_frame")
    last = opt_url(body.get("last_frame"), "last_frame")
    images = opt_url_list(body.get("images"), "images")
    audios = opt_url_list(body.get("audios"), "audios")
    videos = body.get("videos")
    if videos is not None and videos != []:
        if not isinstance(videos, list) or not all(isinstance(v, dict) and v.get("url")
                                                   for v in videos):
            raise ApiError(400, "'videos' must be a list of {url, start_seconds?, require_audio?}")
        videos = [{k: v[k] for k in ("url", "start_seconds", "require_audio") if k in v}
                  for v in videos]
    else:
        videos = None

    mode = req_str(body, "mode")
    if not mode:
        mode = "keyframe" if (first or last) else "reference" if (images or audios or videos) \
            else "text"
    if mode not in ("text", "keyframe", "reference"):
        raise ApiError(400, "'mode' must be text, keyframe or reference")
    payload["mode"] = mode
    if mode == "keyframe":
        if not (first or last):
            raise ApiError(400, "keyframe mode needs first_frame and/or last_frame")
        if first:
            payload["first_frame"] = first
        if last:
            payload["last_frame"] = last
    elif mode == "reference":
        if not (images or audios or videos):
            raise ApiError(400, "reference mode needs images, audios or videos")
        if images:
            payload["images"] = images
        if audios:
            payload["audios"] = audios
        if videos:
            payload["videos"] = videos

    with gen_slot():
        res = agnes("POST", "/v1/videos", payload, timeout=VIDEO_CREATE_TIMEOUT)
    vid = res.get("video_id") or res.get("id")
    if not vid:
        raise ApiError(502, "Agnes video API returned no video_id", 200,
                       redact(json.dumps(res)[:ERR_SNIPPET]))
    with _video_lock:
        if len(_video_models) > 5000:
            _video_models.clear()
        _video_models[vid] = model
    out = _video_view(res, vid, model)
    out["poll"] = "/api/video/status/%s?model=%s" % (urllib.parse.quote(vid),
                                                     urllib.parse.quote(model))
    return out


def do_video_status(video_id, query):
    if not VIDEO_ID_RE.match(video_id):
        raise ApiError(400, "invalid video id")
    model = (query.get("model") or [None])[0]
    if model and not MODEL_RE.match(model):
        raise ApiError(400, "invalid model")
    if not model:
        with _video_lock:
            model = _video_models.get(video_id)
    params = {"video_id": video_id}
    if model:
        params["model_name"] = model
    res = agnes("GET", "/agnesapi?" + urllib.parse.urlencode(params),
                timeout=VIDEO_STATUS_TIMEOUT)
    return _video_view(res, video_id, model)


# --------------------------------------------------------------------------- HTTP layer

class Handler(BaseHTTPRequestHandler):
    server_version = "agnes-service/" + VERSION
    sys_version = ""

    def log_message(self, fmt, *args):     # path only, never headers/bodies
        log.info("%s %s", self.address_string(), fmt % args)

    def _send(self, status, obj=None, body=None, ctype="application/json; charset=utf-8",
              headers=()):
        if body is None:
            body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("X-Frame-Options", "DENY")
        for k, v in headers:
            self.send_header(k, v)
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    # ---- auth helpers

    def _client_ip(self):
        """Peer address; with TRUST_PROXY=1 the right-most X-Forwarded-For entry."""
        ip = self.client_address[0]
        if setting("TRUST_PROXY") == "1":
            xff = self.headers.get("X-Forwarded-For", "")
            parts = [p.strip() for p in xff.split(",") if p.strip()]
            if parts:
                ip = parts[-1]
        if ip.startswith("::ffff:"):
            ip = ip[7:]
        try:
            return str(ipaddress.ip_address(ip))
        except ValueError:
            return self.client_address[0]

    def _is_https(self):
        mode = setting("COOKIE_SECURE", "auto").lower()
        if mode in ("1", "true", "yes"):
            return True
        if mode in ("0", "false", "no"):
            return False
        return self.headers.get("X-Forwarded-Proto", "").lower() == "https"

    def _refresh_cookie(self, value, max_age):
        c = "%s=%s; Path=%s; Max-Age=%d; HttpOnly; SameSite=Strict" % (
            auth.COOKIE_NAME, value, auth.COOKIE_PATH, max_age)
        if self._is_https():
            c += "; Secure"
        return ("Set-Cookie", c)

    def _cookie_token(self):
        # Parsed by hand: cookies are not port-scoped, so other services on this host may
        # send cookies that http.cookies.SimpleCookie rejects wholesale.
        for part in self.headers.get("Cookie", "").split(";"):
            name, sep, value = part.strip().partition("=")
            if sep and name == auth.COOKIE_NAME and value:
                return value.strip('"')
        return None

    def _require_user(self):
        return auth.authenticate(self.headers.get("Authorization"))

    def _require_json_ctype(self):
        # Browsers cannot send application/json cross-site without a CORS preflight
        # (which we never approve) -> cheap CSRF protection for the cookie endpoints.
        ctype = self.headers.get("Content-Type", "").split(";")[0].strip().lower()
        if ctype != "application/json":
            raise ApiError(415, "Content-Type must be application/json")

    def _json_body(self, max_len=MAX_BODY, allow_empty=False):
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            raise ApiError(400, "bad Content-Length")
        if length <= 0:
            if allow_empty:
                return {}
            raise ApiError(400, "JSON body required")
        if length > max_len:
            raise ApiError(413, "request body too large")
        raw = self.rfile.read(length)
        try:
            body = json.loads(raw.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            raise ApiError(400, "body must be valid JSON")
        if not isinstance(body, dict):
            raise ApiError(400, "body must be a JSON object")
        return body

    def _dispatch(self, fn):
        try:
            res = fn()
            status, obj = res[0], res[1]
            self._send(status, obj, headers=res[2] if len(res) > 2 else ())
        except ApiError as e:
            self._send(e.status, e.payload(), headers=e.headers)
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception:
            log.exception("unhandled error on %s %s", self.command, self.path.split("?")[0])
            try:
                self._send(500, {"error": "internal server error"})
            except Exception:
                pass

    # ---- routes

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        parsed = urllib.parse.urlsplit(self.path)
        path = parsed.path
        query = urllib.parse.parse_qs(parsed.query)
        if path in ("/", "/index.html"):
            try:
                with open(INDEX_FILE, "rb") as fh:
                    page = fh.read()
            except OSError:
                return self._send(500, {"error": "index.html missing"})
            return self._send(200, body=page, ctype="text/html; charset=utf-8", headers=[
                ("Content-Security-Policy", "default-src 'self'; script-src 'unsafe-inline'; "
                 "style-src 'unsafe-inline'; img-src https: http: data: blob:; "
                 "media-src https: http: blob:; connect-src 'self'; frame-ancestors 'none'; "
                 "base-uri 'none'; form-action 'self'")])
        if path == "/health":
            return self._dispatch(lambda: (200, {
                "status": "ok", "version": VERSION,
                "key_configured": bool(api_key()),
                "auth": {"type": "jwt",
                         "jwt_secret": auth.jwt_secret_status(),
                         "access_token_minutes": auth.access_ttl() // 60,
                         "db": auth.db_status()},
                "upstream": AGNES_BASE,
                "uptime_s": int(time.time() - START_TIME)}))
        if path == "/api/auth/me":
            def me():
                c = self._require_user()
                return 200, {"user": {"id": int(c["sub"]), "username": c.get("name"),
                                      "role": c["role"]}, "expires_at": c["exp"]}
            return self._dispatch(me)
        if path == "/api/models":
            def models():
                self._require_user()
                return 200, get_models()
            return self._dispatch(models)
        if path.startswith("/api/video/status/"):
            vid = urllib.parse.unquote(path[len("/api/video/status/"):])

            def run():
                self._require_user()
                return 200, do_video_status(vid, query)
            return self._dispatch(run)
        self._send(404, {"error": "not found"})

    def _auth_login(self):
        self._require_json_ctype()
        body = self._json_body(max_len=16384)
        res, plain, max_age = auth.login(body.get("username"), body.get("password"),
                                         self._client_ip(), self.headers.get("User-Agent"))
        return 200, res, [self._refresh_cookie(plain, max_age)]

    def _auth_refresh(self):
        self._require_json_ctype()
        self._json_body(max_len=16384, allow_empty=True)
        try:
            res, plain, max_age = auth.refresh(self._cookie_token(), self._client_ip(),
                                               self.headers.get("User-Agent"))
        except ApiError as e:
            if e.status == 401:                 # dead cookie: clear it
                e.headers = e.headers + [self._refresh_cookie("", 0)]
            raise
        return 200, res, [self._refresh_cookie(plain, max_age)]

    def _auth_logout(self):
        self._require_json_ctype()
        self._json_body(max_len=16384, allow_empty=True)
        revoked = auth.logout(self._cookie_token())
        return 200, {"ok": True, "revoked": revoked}, [self._refresh_cookie("", 0)]

    def do_POST(self):
        path = urllib.parse.urlsplit(self.path).path
        auth_routes = {"/api/auth/login": self._auth_login,
                       "/api/auth/refresh": self._auth_refresh,
                       "/api/auth/logout": self._auth_logout}
        if path in auth_routes:
            return self._dispatch(auth_routes[path])
        routes = {"/api/text": do_text, "/api/image": do_image, "/api/video": do_video_create}
        fn = routes.get(path)
        if not fn:
            return self._send(404, {"error": "not found"})

        def run():
            self._require_user()               # auth before reading/validating the body
            body = self._json_body()
            return 200, fn(body)
        self._dispatch(run)


def main():
    srv = ThreadingHTTPServer((HOST, PORT), Handler)
    srv.daemon_threads = True
    db = auth.db_status()
    log.info("agnes-service %s listening on %s:%d (agnes key: %s, jwt secret: %s, "
             "pg driver: %s, db dsn: %s, db reachable: %s, schema ready: %s)",
             VERSION, HOST, PORT, bool(api_key()), auth.jwt_secret_status(),
             db["driver"], db["dsn_configured"], db["reachable"], db["schema_ready"])
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        srv.server_close()


if __name__ == "__main__":
    main()
