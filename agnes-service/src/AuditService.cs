using System.Text.Json.Nodes;
using Npgsql;

namespace AgnesService;

/// <summary>
/// Admin audit trail (dbapi AuditService): one row in agnes_auth.admin_audit per admin mutation and per
/// self-service password change. Rows are written inside the same transaction as the change they
/// describe, so an action is never applied without its audit row (and vice versa). Callers build
/// <c>detail</c> from whitelisted fields only; <see cref="Scrub"/> additionally drops any key that
/// mentions a password/hash/token/secret as a second line of defence (so detail keys must avoid those
/// words — e.g. revoked refresh tokens are reported as "sessions_revoked").
/// </summary>
public static class AuditService
{
    public static async Task Write(NpgsqlConnection c, NpgsqlTransaction tx, long? actorId, string actorName, string? ip,
        string action, long? targetId, string? targetName, JsonObject? detail = null)
    {
        var d = Scrub(detail ?? new JsonObject());
        await using var cmd = Db.Cmd(c,
            "INSERT INTO agnes_auth.admin_audit (actor_user_id, actor_username, actor_ip, action, target_user_id, target_username, detail) " +
            "VALUES ($1, $2, $3::inet, $4, $5, $6, $7::jsonb)",
            tx, actorId, actorName, ip, action, targetId, targetName, d.ToJsonString(Json.Options));
        await cmd.ExecuteNonQueryAsync();
    }

    private static readonly string[] SecretWords = { "password", "passwd", "hash", "token", "secret" };

    public static JsonObject Scrub(JsonObject o)
    {
        var copy = new JsonObject();
        foreach (var (k, v) in o)
        {
            if (SecretWords.Any(w => k.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            copy[k] = v is JsonObject child ? Scrub(child) : v?.DeepClone();
        }
        return copy;
    }

    public static async Task<JsonArray> Recent(int limit, CancellationToken ct)
    {
        await using var c = await Db.Open(ct);
        await using var cmd = Db.Cmd(c,
            "SELECT id, created_at, actor_user_id, actor_username, host(actor_ip), action, target_user_id, target_username, detail::text " +
            "FROM agnes_auth.admin_audit ORDER BY id DESC LIMIT $1", null, limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new JsonArray();
        while (await r.ReadAsync(ct))
        {
            rows.Add(new JsonObject
            {
                ["id"] = r.GetInt64(0),
                ["created_at"] = AdminApi.Iso(r.GetDateTime(1)),
                ["actor_user_id"] = r.IsDBNull(2) ? null : r.GetInt64(2),
                ["actor_username"] = r.GetString(3),
                ["actor_ip"] = r.IsDBNull(4) ? null : r.GetString(4),
                ["action"] = r.GetString(5),
                ["target_user_id"] = r.IsDBNull(6) ? null : r.GetInt64(6),
                ["target_username"] = r.IsDBNull(7) ? null : r.GetString(7),
                ["detail"] = JsonNode.Parse(r.GetString(8)),
            });
        }
        return rows;
    }
}
