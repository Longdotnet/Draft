using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloSocialCardMemory(
    string OccurrenceKey,
    string ZaloConnectionId,
    string GroupId,
    int BackgroundId,
    int CycleNumber,
    string GroupName,
    string Headline,
    string Body,
    string Ribbon,
    DateTimeOffset CreatedAt);

internal static class ZaloSocialCardBackgroundCatalog
{
    public static readonly IReadOnlyList<int> ActiveIds = [1, 2, 3, 4];

    public static bool IsActive(int id) => id is >= 1 and <= 4;

    public static string LogicalResourceName(int id)
    {
        // Background 5 was retired from Morning. Keep old persisted occurrences readable
        // by mapping legacy id 5 to background 1, without embedding or selecting asset 5.
        var resourceId = id == 5 ? 1 : id;
        if (!IsActive(resourceId))
            throw new ArgumentOutOfRangeException(nameof(id));
        return $"VolleyDraft.Api.Assets.SocialCards.SocialCard{resourceId:00}.jpg";
    }
}

internal static class ZaloSocialCardDeckLogic
{
    public static IReadOnlyList<int> BuildShuffledDeck(int? lastBackgroundId) =>
        BuildShuffledDeck(lastBackgroundId, ZaloSocialCardBackgroundCatalog.ActiveIds);

    public static IReadOnlyList<int> BuildShuffledDeck(
        int? lastBackgroundId,
        IReadOnlyList<int> activeBackgroundIds)
    {
        var values = NormalizeActiveIds(activeBackgroundIds).ToArray();
        for (var index = values.Length - 1; index > 0; index -= 1)
        {
            var target = RandomNumberGenerator.GetInt32(index + 1);
            (values[index], values[target]) = (values[target], values[index]);
        }

        if (lastBackgroundId.HasValue &&
            values.Contains(lastBackgroundId.Value) &&
            values.Length > 1 &&
            values[0] == lastBackgroundId.Value)
        {
            var swapIndex = Array.FindIndex(values, 1, value => value != lastBackgroundId.Value);
            if (swapIndex > 0)
                (values[0], values[swapIndex]) = (values[swapIndex], values[0]);
        }

        return values;
    }

    internal static List<int> NormalizeRemainingDeck(string? json) =>
        NormalizeRemainingDeck(json, ZaloSocialCardBackgroundCatalog.ActiveIds);

    internal static List<int> NormalizeRemainingDeck(
        string? json,
        IReadOnlyList<int> activeBackgroundIds)
    {
        var active = NormalizeActiveIds(activeBackgroundIds).ToHashSet();
        try
        {
            return (JsonSerializer.Deserialize<List<int>>(json ?? "[]") ?? [])
                .Where(active.Contains)
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<int> NormalizeActiveIds(IReadOnlyList<int> activeBackgroundIds)
    {
        var result = activeBackgroundIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (result.Length == 0)
            throw new ArgumentException("Social-card background catalog cannot be empty.", nameof(activeBackgroundIds));
        return result;
    }
}

/// <summary>
/// Group-scoped persistent memory for dynamic social cards.
/// One occurrence is immutable: retries reuse the same AI copy/background.
/// Each group consumes its configured shuffled background deck before a new cycle begins.
/// </summary>
internal static class ZaloSocialCardMemoryStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalGates =
        new(StringComparer.Ordinal);

    public static async Task<IReadOnlyList<ZaloSocialCardMemory>> GetRecentAsync(
        VolleyDraftDbContext db,
        string connectionId,
        string groupId,
        int take = 6,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(db, cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "OccurrenceKey", "BackgroundId", "CycleNumber", "GroupName",
                   "Headline", "Body", "Ribbon", "CreatedAt"
            FROM "ZaloSocialCardMemories"
            WHERE "ZaloConnectionId" = @connectionId AND "GroupId" = @groupId
            ORDER BY "CreatedAt" DESC
            LIMIT @take
            """;
        AddParameter(command, "@connectionId", connectionId);
        AddParameter(command, "@groupId", groupId);
        AddParameter(command, "@take", Math.Clamp(take, 1, 20));

        var result = new List<ZaloSocialCardMemory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ZaloSocialCardMemory(
                reader.GetString(0),
                connectionId,
                groupId,
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                ReadDateTimeOffset(reader, 7)));
        }
        return result;
    }

    public static async Task<ZaloSocialCardMemory> RememberAsync(
        VolleyDraftDbContext db,
        string occurrenceKey,
        string connectionId,
        string groupId,
        string groupName,
        ZaloSocialCardCopy copy,
        CancellationToken cancellationToken = default,
        IReadOnlyList<int>? activeBackgroundIds = null)
    {
        await EnsureSchemaAsync(db, cancellationToken);
        var activeIds = activeBackgroundIds ?? ZaloSocialCardBackgroundCatalog.ActiveIds;

        var rotationKey = $"{connectionId}:{groupId}";
        var gate = LocalGates.GetOrAdd(rotationKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);

            await using var transaction =
                await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                var existing = await ReadMemoryAsync(
                    connection,
                    transaction,
                    occurrenceKey,
                    connectionId,
                    groupId,
                    cancellationToken);
                if (existing is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return existing;
                }

                await EnsureRotationRowAsync(
                    connection,
                    transaction,
                    rotationKey,
                    connectionId,
                    groupId,
                    cancellationToken);
                var state = await ReadRotationStateForUpdateAsync(
                    connection,
                    transaction,
                    rotationKey,
                    cancellationToken);

                var remaining = ZaloSocialCardDeckLogic.NormalizeRemainingDeck(
                    state.RemainingBackgroundIdsJson,
                    activeIds);
                var cycleNumber = state.CycleNumber;
                if (remaining.Count == 0)
                {
                    remaining = ZaloSocialCardDeckLogic
                        .BuildShuffledDeck(state.LastBackgroundId, activeIds)
                        .ToList();
                    cycleNumber += 1;
                }

                var backgroundId = remaining[0];
                remaining.RemoveAt(0);
                var now = DateTimeOffset.UtcNow;
                var memory = new ZaloSocialCardMemory(
                    occurrenceKey,
                    connectionId,
                    groupId,
                    backgroundId,
                    cycleNumber,
                    groupName,
                    copy.Headline,
                    copy.Body,
                    copy.Ribbon,
                    now);

                await InsertMemoryAsync(connection, transaction, memory, cancellationToken);
                await UpdateRotationStateAsync(
                    connection,
                    transaction,
                    rotationKey,
                    JsonSerializer.Serialize(remaining),
                    backgroundId,
                    cycleNumber,
                    now,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return memory;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task EnsureSchemaAsync(
        VolleyDraftDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var postgres = (db.Database.ProviderName ?? string.Empty)
            .Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var timestampType = postgres ? "timestamp with time zone" : "TEXT";

        await ExecuteNonQueryAsync(connection, null, $"""
            CREATE TABLE IF NOT EXISTS "ZaloSocialCardMemories" (
                "OccurrenceKey" TEXT PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "BackgroundId" INTEGER NOT NULL,
                "CycleNumber" INTEGER NOT NULL,
                "GroupName" TEXT NOT NULL,
                "Headline" TEXT NOT NULL,
                "Body" TEXT NOT NULL,
                "Ribbon" TEXT NOT NULL,
                "CreatedAt" {timestampType} NOT NULL
            );
            """, cancellationToken);

        await ExecuteNonQueryAsync(connection, null, $"""
            CREATE TABLE IF NOT EXISTS "ZaloSocialCardRotationStates" (
                "RotationKey" TEXT PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "RemainingBackgroundIdsJson" TEXT NOT NULL,
                "LastBackgroundId" INTEGER NULL,
                "CycleNumber" INTEGER NOT NULL,
                "UpdatedAt" {timestampType} NOT NULL
            );
            """, cancellationToken);

        await ExecuteNonQueryAsync(
            connection,
            null,
            """
            CREATE INDEX IF NOT EXISTS "IX_ZaloSocialCardMemories_Group_Created"
            ON "ZaloSocialCardMemories" ("ZaloConnectionId", "GroupId", "CreatedAt");
            """,
            cancellationToken);
    }

    private static async Task<ZaloSocialCardMemory?> ReadMemoryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string occurrenceKey,
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "BackgroundId", "CycleNumber", "GroupName",
                   "Headline", "Body", "Ribbon", "CreatedAt"
            FROM "ZaloSocialCardMemories"
            WHERE "OccurrenceKey" = @occurrenceKey
            LIMIT 1
            """;
        AddParameter(command, "@occurrenceKey", occurrenceKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ZaloSocialCardMemory(
            occurrenceKey,
            connectionId,
            groupId,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ReadDateTimeOffset(reader, 6));
    }

    private static async Task EnsureRotationRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string rotationKey,
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        var postgres = connection.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = postgres
            ? """
              INSERT INTO "ZaloSocialCardRotationStates"
                  ("RotationKey", "ZaloConnectionId", "GroupId", "RemainingBackgroundIdsJson",
                   "LastBackgroundId", "CycleNumber", "UpdatedAt")
              VALUES (@key, @connectionId, @groupId, '[]', NULL, 0, @now)
              ON CONFLICT ("RotationKey") DO NOTHING
              """
            : """
              INSERT OR IGNORE INTO "ZaloSocialCardRotationStates"
                  ("RotationKey", "ZaloConnectionId", "GroupId", "RemainingBackgroundIdsJson",
                   "LastBackgroundId", "CycleNumber", "UpdatedAt")
              VALUES (@key, @connectionId, @groupId, '[]', NULL, 0, @now)
              """;
        AddParameter(command, "@key", rotationKey);
        AddParameter(command, "@connectionId", connectionId);
        AddParameter(command, "@groupId", groupId);
        AddParameter(command, "@now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RotationState> ReadRotationStateForUpdateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string rotationKey,
        CancellationToken cancellationToken)
    {
        var postgres = connection.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "RemainingBackgroundIdsJson", "LastBackgroundId", "CycleNumber"
            FROM "ZaloSocialCardRotationStates"
            WHERE "RotationKey" = @key
            """ + (postgres ? " FOR UPDATE" : string.Empty);
        AddParameter(command, "@key", rotationKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Không khởi tạo được social-card deck của group.");

        return new RotationState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetInt32(2));
    }

    private static async Task InsertMemoryAsync(
        DbConnection connection,
        DbTransaction transaction,
        ZaloSocialCardMemory memory,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "ZaloSocialCardMemories"
                ("OccurrenceKey", "ZaloConnectionId", "GroupId", "BackgroundId", "CycleNumber",
                 "GroupName", "Headline", "Body", "Ribbon", "CreatedAt")
            VALUES
                (@occurrenceKey, @connectionId, @groupId, @backgroundId, @cycleNumber,
                 @groupName, @headline, @body, @ribbon, @createdAt)
            """;
        AddParameter(command, "@occurrenceKey", memory.OccurrenceKey);
        AddParameter(command, "@connectionId", memory.ZaloConnectionId);
        AddParameter(command, "@groupId", memory.GroupId);
        AddParameter(command, "@backgroundId", memory.BackgroundId);
        AddParameter(command, "@cycleNumber", memory.CycleNumber);
        AddParameter(command, "@groupName", memory.GroupName);
        AddParameter(command, "@headline", memory.Headline);
        AddParameter(command, "@body", memory.Body);
        AddParameter(command, "@ribbon", memory.Ribbon);
        AddParameter(command, "@createdAt", memory.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateRotationStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string rotationKey,
        string remainingJson,
        int lastBackgroundId,
        int cycleNumber,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE "ZaloSocialCardRotationStates"
            SET "RemainingBackgroundIdsJson" = @remaining,
                "LastBackgroundId" = @last,
                "CycleNumber" = @cycle,
                "UpdatedAt" = @updatedAt
            WHERE "RotationKey" = @key
            """;
        AddParameter(command, "@remaining", remainingJson);
        AddParameter(command, "@last", lastBackgroundId);
        AddParameter(command, "@cycle", cycleNumber);
        AddParameter(command, "@updatedAt", updatedAt);
        AddParameter(command, "@key", rotationKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ReadDateTimeOffset(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            string text when DateTimeOffset.TryParse(text, out var parsed) => parsed,
            _ => DateTimeOffset.UtcNow
        };
    }

    private sealed record RotationState(
        string RemainingBackgroundIdsJson,
        int? LastBackgroundId,
        int CycleNumber);
}