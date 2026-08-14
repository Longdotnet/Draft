using System.Data;
using System.Data.Common;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Read-only reverse traversal helpers for the additive V2 message graph. The graph
/// store remains the write owner; this helper is used by migration/trace projection.
/// </summary>
public sealed class ZaloMessageGraphQuery(VolleyDraftDbContext db)
{
    public async Task<string?> LoadBotReplyMessageIdAsync(
        string zaloConnectionId,
        string groupId,
        string parentMessageId,
        CancellationToken cancellationToken = default)
    {
        zaloConnectionId = Clean(zaloConnectionId, 100);
        groupId = Clean(groupId, 100);
        parentMessageId = Clean(parentMessageId, 160);
        if (zaloConnectionId.Length == 0 || groupId.Length == 0 || parentMessageId.Length == 0) return null;

        // Schema creation remains centralized in ZaloMessageGraphStore.
        await new ZaloMessageGraphStore(db)
            .LoadRelationAsync(zaloConnectionId, groupId, "__schema_probe__", cancellationToken);

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE("ProviderOutboundMessageId", "FromMessageId")
            FROM "ZaloMessageRelations"
            WHERE "ZaloConnectionId" = @connectionId
              AND "GroupId" = @groupId
              AND "ToMessageId" = @parentMessageId
              AND "RelationType" = 'BotReply'
            ORDER BY "CreatedAt" DESC
            LIMIT 1;
            """;
        Add(command, "@connectionId", zaloConnectionId);
        Add(command, "@groupId", groupId);
        Add(command, "@parentMessageId", parentMessageId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Clean(Convert.ToString(result), 160);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
