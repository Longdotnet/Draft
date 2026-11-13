using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal readonly record struct ZaloProviderGroupVisibilityKey(
    string ConnectionId,
    string GroupId);

/// <summary>
/// Caches authoritative group visibility returned by the connected Zalo account.
/// Unknown groups remain eligible for backwards compatibility; only a successful
/// provider directory refresh may mark a configured group as missing.
/// </summary>
internal sealed class ZaloProviderGroupVisibilityStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task RefreshAsync(
        IReadOnlyCollection<string> connectionIds,
        IReadOnlyCollection<string> providerGroupIds,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var visible = providerGroupIds
            .Select(CleanId)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var connectionIdRaw in connectionIds)
        {
            var connectionId = CleanId(connectionIdRaw);
            if (connectionId.Length == 0) continue;

            var candidates = await ReadConfiguredGroupIdsAsync(connectionId, cancellationToken);
            foreach (var groupId in candidates)
            {
                await UpsertAsync(
                    connectionId,
                    groupId,
                    visible.Contains(groupId),
                    verifiedAt,
                    cancellationToken);
            }
        }
    }

    public async Task<IReadOnlySet<ZaloProviderGroupVisibilityKey>> GetConfirmedMissingAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            """
            SELECT "ZaloConnectionId", "GroupId"
            FROM "ZaloProviderGroupVisibility"
            WHERE "IsVisible" = 0;
            """,
            cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<ZaloProviderGroupVisibilityKey>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var connectionId = CleanId(Convert.ToString(reader["ZaloConnectionId"], CultureInfo.InvariantCulture));
            var groupId = CleanId(Convert.ToString(reader["GroupId"], CultureInfo.InvariantCulture));
            if (connectionId.Length > 0 && groupId.Length > 0)
                result.Add(new ZaloProviderGroupVisibilityKey(connectionId, groupId));
        }
        return result;
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (ensured) return;
        await new ZaloAutoSessionStore(db).EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            """
            CREATE TABLE IF NOT EXISTS "ZaloProviderGroupVisibility" (
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "IsVisible" INTEGER NOT NULL,
                "VerifiedAt" TEXT NOT NULL,
                PRIMARY KEY ("ZaloConnectionId", "GroupId")
            );
            """,
            cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    private async Task<IReadOnlyList<string>> ReadConfiguredGroupIdsAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync(
            """
            SELECT "GroupId"
            FROM "ZaloTrackedGroups"
            WHERE "ZaloConnectionId" = @ConnectionId
            UNION
            SELECT "ZaloGroupId" AS "GroupId"
            FROM "MatchSessions"
            WHERE "ZaloConnectionId" = @ConnectionId
              AND "ZaloGroupId" IS NOT NULL;
            """,
            cancellationToken);
        AddParameter(command, "@ConnectionId", connectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var groupId = CleanId(Convert.ToString(reader["GroupId"], CultureInfo.InvariantCulture));
            if (groupId.Length > 0) result.Add(groupId);
        }
        return result.ToList();
    }

    private async Task UpsertAsync(
        string connectionId,
        string groupId,
        bool isVisible,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync(
            """
            INSERT INTO "ZaloProviderGroupVisibility" (
                "ZaloConnectionId", "GroupId", "IsVisible", "VerifiedAt")
            VALUES (
                @ConnectionId, @GroupId, @IsVisible, @VerifiedAt)
            ON CONFLICT ("ZaloConnectionId", "GroupId") DO UPDATE SET
                "IsVisible" = excluded."IsVisible",
                "VerifiedAt" = excluded."VerifiedAt";
            """,
            cancellationToken);
        AddParameter(command, "@ConnectionId", connectionId);
        AddParameter(command, "@GroupId", groupId);
        AddParameter(command, "@IsVisible", isVisible ? 1 : 0);
        AddParameter(command, "@VerifiedAt", verifiedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}
