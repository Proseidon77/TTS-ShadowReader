"""令牌鉴权（第 13/24/25/26 条，8.3 简化版）与鉴权失败限流。

tokens.json：{name: {"sha256": hex, "scope": "read"|"write", "can_act_for": [...]}}，只存哈希；
明文在 tokens/<name>.txt（0600），由 make_tokens.py 生成。改配置后重启即吊销。
"""
import collections
import hashlib
import hmac
import json
import time

from ingest import ApiError

FAIL_LIMIT, FAIL_WINDOW, BAN_SECONDS = 10, 60, 600


class Auth:
    def __init__(self, tokens_path):
        cfg = json.load(open(tokens_path, encoding="utf-8"))
        self.tokens = [(name, bytes.fromhex(v["sha256"]), v.get("scope", "read"), set(v.get("can_act_for", [])))
                       for name, v in cfg.items()]
        self.fails = collections.defaultdict(collections.deque)
        self.banned = {}

    def _ip_check(self, ip):
        until = self.banned.get(ip)
        if until and until > time.time():
            raise ApiError("RETRY_LATER", "鉴权失败次数过多，暂时封禁", f"{int(until - time.time())} 秒后再试",
                           {"retry_after": int(until - time.time())}, status=429)

    def _fail(self, ip, message, hint):
        q, now = self.fails[ip], time.time()
        q.append(now)
        while q and q[0] < now - FAIL_WINDOW:
            q.popleft()
        if len(q) > FAIL_LIMIT:
            self.banned[ip] = now + BAN_SECONDS
            q.clear()
        raise ApiError("AUTH_FAILED", message, hint, status=401)

    def identify(self, header, ip):
        """返回 (name, scope, can_act_for) 或 None（未带令牌）；令牌无效抛 AUTH_FAILED。"""
        self._ip_check(ip)
        if not header:
            return None
        if not header.startswith("Bearer "):
            self._fail(ip, "Authorization 头格式错误", "格式：Authorization: Bearer $TTSWEB_TOKEN")
        digest = hashlib.sha256(header[7:].strip().encode("utf-8")).digest()
        for name, h, scope, act in self.tokens:
            if hmac.compare_digest(h, digest):
                return name, scope, act
        self._fail(ip, "令牌无效或已吊销", "检查 TTSWEB_TOKEN；仍失败请报告用户，不要反复重试")

    def require(self, header, ip, scope):
        who = self.identify(header, ip)
        if who is None:
            self._fail(ip, "缺少令牌", "加上 Authorization: Bearer $TTSWEB_TOKEN")
        if scope == "write" and who[1] != "write":
            raise ApiError("AUTH_FAILED", "该令牌只有读权限", "写入请使用写令牌", {"caller": who[0]}, status=403)
        return who

    @staticmethod
    def resolve_added_by(who, requested):
        name, _scope, act = who
        if not requested or requested == name:
            return name
        if requested in act:
            return requested
        raise ApiError("AUTH_FAILED", f"调用方 {name} 不能代 {requested} 入库",
                       "删掉 added_by（服务端按令牌填），或改用驻地令牌", {"caller": name, "added_by": requested},
                       status=403)
