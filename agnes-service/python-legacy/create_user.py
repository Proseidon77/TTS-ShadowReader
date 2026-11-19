#!/usr/bin/env python3
"""Manage agnes-service users in PostgreSQL schema agnes_auth (run locally by the operator).

  python3 create_user.py USERNAME [--role user|admin]   create a user (prompts for password)
  python3 create_user.py USERNAME --reset-password      set new password, unlock, log out everywhere
  python3 create_user.py USERNAME --disable | --enable  deactivate / reactivate (disable = logout everywhere)
  python3 create_user.py USERNAME --unlock              clear failed-login lock
  python3 create_user.py USERNAME --logout-all          bump token_version + revoke refresh tokens
  python3 create_user.py --list                         list users
  python3 create_user.py --unblock-ip IP                remove an IP ban

Password is read with getpass (no echo) and asked twice; with --password-stdin it is read
from the first line of stdin instead (for scripted test users; never pass it as an argument).
DB connection: AGNES_DB_DSN from the environment or ./.env. Plaintext is never stored or logged.
"""

import argparse
import getpass
import sys

import auth
from common import setting


def read_password(from_stdin):
    if from_stdin:
        pw = sys.stdin.readline().rstrip("\r\n")
    else:
        pw = getpass.getpass("Password: ")
        if getpass.getpass("Repeat password: ") != pw:
            sys.exit("error: passwords do not match")
    problem = auth.check_password_policy(pw)
    if problem:
        sys.exit("error: " + problem)
    return pw


def main():
    ap = argparse.ArgumentParser(description="Manage agnes-service users (schema agnes_auth)")
    ap.add_argument("username", nargs="?")
    ap.add_argument("--role", choices=auth.ROLES, default=None)
    ap.add_argument("--password-stdin", action="store_true")
    g = ap.add_mutually_exclusive_group()
    g.add_argument("--reset-password", action="store_true")
    g.add_argument("--disable", action="store_true")
    g.add_argument("--enable", action="store_true")
    g.add_argument("--unlock", action="store_true")
    g.add_argument("--logout-all", action="store_true")
    g.add_argument("--list", action="store_true")
    g.add_argument("--unblock-ip", metavar="IP")
    a = ap.parse_args()

    if auth.psycopg is None:
        sys.exit("error: psycopg not installed - run: pip install 'psycopg[binary]'")
    if not setting("AGNES_DB_DSN"):
        sys.exit("error: AGNES_DB_DSN not set (environment or .env)")

    try:
        conn = auth.connect()
    except auth.ApiError as e:
        sys.exit("error: " + e.message)

    with conn:
        if a.list:
            rows = conn.execute(
                "SELECT id, username, role, is_active, failed_logins, locked_until, "
                "created_at, last_login_at FROM agnes_auth.users ORDER BY id").fetchall()
            print("%-4s %-20s %-6s %-6s %-5s %-25s %s" % ("id", "username", "role", "active",
                                                         "fails", "locked_until", "last_login_at"))
            for r in rows:
                print("%-4s %-20s %-6s %-6s %-5s %-25s %s" % (r[0], r[1], r[2], r[3], r[4],
                                                             r[5] or "-", r[7] or "-"))
            return
        if a.unblock_ip:
            n = conn.execute("DELETE FROM agnes_auth.ip_block WHERE ip = %s::inet",
                             (a.unblock_ip,)).rowcount
            print("removed %d ip_block row(s) for %s" % (n, a.unblock_ip))
            return

        if not a.username:
            ap.error("USERNAME is required")
        if not auth.USERNAME_RE.match(a.username):
            sys.exit("error: username must match %s" % auth.USERNAME_RE.pattern)
        row = conn.execute("SELECT id, username FROM agnes_auth.users WHERE lower(username) = "
                           "lower(%s)", (a.username,)).fetchone()

        def need_user():
            if not row:
                sys.exit("error: no such user: %s" % a.username)
            return row[0]

        def revoke_all(uid, reason):
            conn.execute("UPDATE agnes_auth.users SET token_version = token_version + 1, "
                         "updated_at = now() WHERE id = %s", (uid,))
            conn.execute("UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), "
                         "revoke_reason = %s WHERE user_id = %s AND revoked_at IS NULL",
                         (reason, uid))

        if a.reset_password:
            uid = need_user()
            h = auth.hash_password(read_password(a.password_stdin))
            conn.execute("UPDATE agnes_auth.users SET password_hash = %s, failed_logins = 0, "
                         "locked_until = NULL, updated_at = now() WHERE id = %s", (h, uid))
            revoke_all(uid, "admin")
            print("password reset for %s (all sessions logged out)" % row[1])
        elif a.disable or a.enable:
            uid = need_user()
            conn.execute("UPDATE agnes_auth.users SET is_active = %s, updated_at = now() "
                         "WHERE id = %s", (bool(a.enable), uid))
            if a.disable:
                revoke_all(uid, "admin")
            print("%s %s" % (row[1], "enabled" if a.enable else "disabled"))
        elif a.unlock:
            uid = need_user()
            conn.execute("UPDATE agnes_auth.users SET failed_logins = 0, locked_until = NULL, "
                         "updated_at = now() WHERE id = %s", (uid,))
            print("%s unlocked" % row[1])
        elif a.logout_all:
            uid = need_user()
            revoke_all(uid, "admin")
            print("%s logged out everywhere" % row[1])
        else:
            if row:
                sys.exit("error: user %s already exists (use --reset-password)" % row[1])
            h = auth.hash_password(read_password(a.password_stdin))
            new = conn.execute(
                "INSERT INTO agnes_auth.users (username, password_hash, role) "
                "VALUES (%s, %s, %s) RETURNING id",
                (a.username, h, a.role or "user")).fetchone()
            print("created user %s (id %s, role %s)" % (a.username, new[0], a.role or "user"))
        if a.role and row and not (a.list or a.unblock_ip):
            conn.execute("UPDATE agnes_auth.users SET role = %s, updated_at = now() WHERE id = %s",
                         (a.role, row[0]))
            print("role of %s set to %s" % (row[1], a.role))


if __name__ == "__main__":
    main()
