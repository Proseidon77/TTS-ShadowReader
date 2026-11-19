using System.Text.Json.Nodes;

namespace AgnesService;

/// <summary>
/// "My account" (dbapi): POST /api/auth/change-password {currentPassword, newPassword}, any role.
/// This is the path an admin's self-reset is pointed to. It verifies the current password (wrong
/// answers count toward the normal account lockout, so a stolen access token can't be used to
/// brute-force the password), applies the shared strength rule, bumps token_version + revokes every
/// refresh token (all other sessions die), then issues a fresh access token and refresh cookie for
/// the caller. One admin_audit row (action change_password, actor = target = self).
/// </summary>
public static partial class AuthService
{
    public static async Task<LoginResult> ChangePassword(AuthUser u, JsonObject body, string ip, string? ua)
    {
        RequireKey();
        var cur = Http.OptString(body, "currentPassword") ?? Http.OptString(body, "current_password");
        var next = Http.OptString(body, "newPassword") ?? Http.OptString(body, "new_password");
        static ApiError Missing(string f) =>
            new(400, $"{f} is required") { Code = "missing_field", Extra = new JsonObject { ["field"] = f } };
        if (string.IsNullOrEmpty(cur)) throw Missing("currentPassword");
        if (string.IsNullOrEmpty(next)) throw Missing("newPassword");
        if (cur.Length > Passwords.MaxLength) throw new ApiError(400, "password too long") { Code = "bad_request" };

        ApiError? err = null;
        LoginResult? result = null;
        await using (var c = await Db.Open())
        {
            await using var tx = await c.BeginTransactionAsync();
            (string uname, string phash, string role, bool locked, int lockedSecs)? row = null;
            await using (var cmd = Db.Cmd(c,
                "SELECT username, password_hash, role, coalesce(locked_until > now(), false), " +
                "coalesce(extract(epoch FROM (locked_until - now()))::int, 0) FROM agnes_auth.users WHERE id = $1 FOR UPDATE", tx, u.Id))
            await using (var r = await cmd.ExecuteReaderAsync())
                if (await r.ReadAsync()) row = (r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetInt32(4));

            if (row == null) err = Unauthorized("invalid token: unknown user");
            else
            {
                var x = row.Value;
                if (x.locked) err = Locked(x.lockedSecs);
                else if (!Passwords.Verify(cur, x.phash))
                {
                    var n = (int)(await Scalar(c, tx,
                        "UPDATE agnes_auth.users SET failed_logins = failed_logins + 1, updated_at = now() WHERE id = $1 RETURNING failed_logins", u.Id) ?? 0);
                    if (n >= MaxFailedLogins)
                    {
                        var mins = LockoutMinutes;
                        await Exec(c, tx, "UPDATE agnes_auth.users SET failed_logins = 0, locked_until = now() + make_interval(mins => $1::int) WHERE id = $2", mins, u.Id);
                        await Audit(c, tx, u.Id, x.uname, ip, false, "change_password_locked", ua);
                        err = Locked(mins * 60);
                    }
                    else
                    {
                        await Audit(c, tx, u.Id, x.uname, ip, false, "change_password_bad", ua);
                        err = new ApiError(400, "current password is incorrect")
                            { Code = "invalid_current_password", Extra = new JsonObject { ["field"] = "currentPassword" } };
                    }
                }
                else if (next == cur)
                    err = new ApiError(400, "new password must differ from the current one")
                        { Code = "password_unchanged", Extra = new JsonObject { ["field"] = "newPassword" } };
                else if (Passwords.PolicyProblem(next, x.uname) is { } problem)
                    err = new ApiError(400, problem) { Code = "weak_password", Extra = new JsonObject { ["field"] = "newPassword" } };
                else
                {
                    var ver = (int)(await Scalar(c, tx,
                        "UPDATE agnes_auth.users SET password_hash = $1, failed_logins = 0, locked_until = NULL, " +
                        "token_version = token_version + 1, updated_at = now() WHERE id = $2 RETURNING token_version",
                        Passwords.Hash(next), u.Id))!;
                    var revoked = await Exec(c, tx,
                        "UPDATE agnes_auth.refresh_tokens SET revoked_at = now(), revoke_reason = 'password_changed' " +
                        "WHERE user_id = $1 AND revoked_at IS NULL", u.Id);
                    var famExp = (DateTime)(await Scalar(c, tx, "SELECT now() + make_interval(secs => $1::double precision)", (double)FamilyTtl))!;
                    var (plain, _, maxAge) = await NewRefreshToken(c, tx, u.Id, Guid.NewGuid(), famExp, ip, ua);
                    await AuditService.Write(c, tx, u.Id, x.uname, ip, "change_password", u.Id, x.uname,
                        new JsonObject { ["other_sessions_invalidated"] = true, ["sessions_revoked"] = revoked });
                    result = new LoginResult(TokenBody(u.Id, x.uname, x.role, ver), plain, maxAge);
                }
            }
            await tx.CommitAsync();   // failure counter / audit rows persist even on error
        }
        if (err != null) throw err;
        ForgetUserState();
        return result!;
    }
}
