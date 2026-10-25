using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloMemberPresenceSignalData(
    string ZaloUserId,
    string SignalType,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ProviderOccurredAt,
    string? MessageId);

internal sealed class ZaloMemberPresenceSignalStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public Task EnsureAsync(CancellationToken cancellationToken = default) =>
        PostgresSchemaInitialization.EnsureAsync(
            db,
            nameof(ZaloMemberPresenceSignalStore),
            EnsureCoreAsync,
            cancellationToken);

    private async Task EnsureCoreAsync(CancellationToken cancellationToken)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloMemberPresenceSignals" (
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "ZaloUserId" TEXT NOT NULL,
                "SignalType" TEXT NOT NULL,
                "ObservedAt" TEXT NOT NULL,
                "ProviderOccurredAt" TEXT NULL,
                "MessageId" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL,
                PRIMARY KEY ("ZaloConnectionId", "GroupId", "ZaloUserId", "SignalType")
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloMemberPresenceSignals_GroupObserved"
                ON "ZaloMemberPresenceSignals" ("ZaloConnectionId", "GroupId", "ObservedAt");
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<int> RecordAsync(
        ZaloMemberPresenceSignalEvent incoming,
        CancellationToken cancellationToken = default)
    {
        var signalType = NormalizeSignalType(incoming.EventType);
        var accountId = CleanId(incoming.AccountId);
        var groupId = CleanId(incoming.GroupId);
        if (signalType is null || accountId.Length == 0 || groupId.Length == 0) return 0;

        var connectionId = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId)
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(connectionId)) return 0;

        var observedAt = ParseUnixMs(incoming.ObservedAtUnixMs) ?? DateTimeOffset.UtcNow;
        var providerOccurredAt = incoming.ProviderOccurredAtUnixMs is { } providerMs
            ? ParseUnixMs(providerMs)
            : null;
        var memberIds = incoming.MemberIds
            .Select(CleanId)
            .Where(item => item.Length > 0 && item != accountId)
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToArray();
        if (memberIds.Length == 0) return 0;

        await EnsureAsync(cancellationToken);
        var accepted = 0;
        foreach (var memberId in memberIds)
        {
            const string sql = """
                INSERT INTO "ZaloMemberPresenceSignals" (
                    "ZaloConnectionId", "GroupId", "ZaloUserId", "SignalType",
                    "ObservedAt", "ProviderOccurredAt", "MessageId", "UpdatedAt")
                VALUES (
                    @ConnectionId, @GroupId, @ZaloUserId, @SignalType,
                    @ObservedAt, @ProviderOccurredAt, @MessageId, @UpdatedAt)
                ON CONFLICT ("ZaloConnectionId", "GroupId", "ZaloUserId", "SignalType") DO UPDATE SET
                    "ObservedAt" = excluded."ObservedAt",
                    "ProviderOccurredAt" = excluded."ProviderOccurredAt",
                    "MessageId" = excluded."MessageId",
                    "UpdatedAt" = excluded."UpdatedAt"
                WHERE "ZaloMemberPresenceSignals"."ObservedAt" <= excluded."ObservedAt";
                """;
            await using var command = await CreateCommandAsync(sql, cancellationToken);
            Add(command, "@ConnectionId", CleanId(connectionId));
            Add(command, "@GroupId", groupId);
            Add(command, "@ZaloUserId", memberId);
            Add(command, "@SignalType", signalType);
            Add(command, "@ObservedAt", FormatDate(observedAt));
            Add(command, "@ProviderOccurredAt", providerOccurredAt is null ? null : FormatDate(providerOccurredAt.Value));
            Add(command, "@MessageId", CleanOptional(incoming.MessageId, 160));
            Add(command, "@UpdatedAt", FormatDate(DateTimeOffset.UtcNow));
            accepted += await command.ExecuteNonQueryAsync(cancellationToken) > 0 ? 1 : 0;
        }
        return accepted;
    }

    public async Task<IReadOnlyList<ZaloMemberPresenceSignalData>> GetRecentAsync(
        string connectionId,
        string groupId,
        IReadOnlySet<string> userIds,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0) return [];
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT *
            FROM "ZaloMemberPresenceSignals"
            WHERE "ZaloConnectionId" = @ConnectionId
              AND "GroupId" = @GroupId
            ORDER BY "ObservedAt" DESC;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ConnectionId", CleanId(connectionId));
        Add(command, "@GroupId", CleanId(groupId));
        var rows = new List<ZaloMemberPresenceSignalData>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var userId = CleanId(Convert.ToString(reader["ZaloUserId"], CultureInfo.InvariantCulture));
            if (!userIds.Contains(userId)) continue;
            var observedAt = ParseDate(reader["ObservedAt"]);
            if (observedAt < since) continue;
            rows.Add(new ZaloMemberPresenceSignalData(
                userId,
                Convert.ToString(reader["SignalType"], CultureInfo.InvariantCulture) ?? string.Empty,
                observedAt,
                reader["ProviderOccurredAt"] is DBNull ? null : ParseDate(reader["ProviderOccurredAt"]),
                reader["MessageId"] is DBNull ? null : Convert.ToString(reader["MessageId"], CultureInfo.InvariantCulture)));
        }
        return rows;
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static string? NormalizeSignalType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "seen" => "seen",
        "typing" => "typing",
        _ => null
    };

    private static DateTimeOffset? ParseUnixMs(long value)
    {
        if (value <= 0) return null;
        try
        {
            var parsed = DateTimeOffset.FromUnixTimeMilliseconds(value);
            var now = DateTimeOffset.UtcNow;
            return parsed > now.AddMinutes(10) || parsed < now.AddDays(-7) ? null : parsed;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(object value) =>
        value is DateTimeOffset offset
            ? offset
            : DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
