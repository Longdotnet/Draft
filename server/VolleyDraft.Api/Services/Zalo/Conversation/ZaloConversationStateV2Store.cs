using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public enum ZaloConversationStateV2Status
{
    Active,
    Completed,
    Cancelled,
    Expired,
    Superseded
}

public enum ZaloTopicSwitchDecision
{
    ContinuePending,
    CancelPending,
    SwitchToNewIntent
}

public sealed record ZaloConversationStateV2Snapshot(
    string Id,
    string GroupId,
    string SenderZaloUserId,
    string Intent,
    string CollectedArgumentsJson,
    string MissingArgumentsJson,
    string CandidateEntitiesJson,
    string? SourceMessageId,
    string? LastMessageId,
    int StateVersion,
    ZaloConversationStateV2Status Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Structured conversation state independent from a Zalo login connection.
/// It is intentionally additive beside the legacy state table so V2 can roll out
/// and roll back without corrupting existing confirmation workflows.
/// </summary>
public sealed class ZaloConversationStateV2Store(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task<ZaloConversationStateV2Snapshot?> LoadActiveAsync(
        string groupId,
        string senderZaloUserId,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        senderZaloUserId = Clean(senderZaloUserId, 100);
        if (groupId.Length == 0 || senderZaloUserId.Length == 0) return null;
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "GroupId", "SenderZaloUserId", "Intent", "CollectedArgumentsJson",
                   "MissingArgumentsJson", "CandidateEntitiesJson", "SourceMessageId", "LastMessageId",
                   "StateVersion", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloConversationStatesV2"
            WHERE "GroupId" = @groupId AND "SenderZaloUserId" = @senderId
              AND "Status" = 'Active'
            LIMIT 1;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@senderId", senderZaloUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var state = Read(reader);
        if (state.ExpiresAt > DateTimeOffset.UtcNow) return state;
        await reader.DisposeAsync();
        await SetStatusAsync(groupId, senderZaloUserId, ZaloConversationStateV2Status.Expired, cancellationToken);
        return null;
    }

    public async Task<ZaloConversationStateV2Snapshot> SaveActiveAsync(
        string groupId,
        string senderZaloUserId,
        string intent,
        string collectedArgumentsJson,
        string missingArgumentsJson,
        string candidateEntitiesJson,
        string? sourceMessageId,
        string? lastMessageId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        senderZaloUserId = Clean(senderZaloUserId, 100);
        intent = Clean(intent, 120);
        collectedArgumentsJson = Json(collectedArgumentsJson);
        missingArgumentsJson = Json(missingArgumentsJson);
        candidateEntitiesJson = Json(candidateEntitiesJson);
        sourceMessageId = CleanOptional(sourceMessageId, 160);
        lastMessageId = CleanOptional(lastMessageId, 160);
        if (groupId.Length == 0 || senderZaloUserId.Length == 0 || intent.Length == 0)
            throw new ArgumentException("Group, sender and intent are required for conversation state.");
        if (expiresAt <= DateTimeOffset.UtcNow) throw new ArgumentOutOfRangeException(nameof(expiresAt));

        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var existing = await LoadAnyAsync(connection, groupId, senderZaloUserId, cancellationToken);
        var id = existing?.Id ?? Guid.NewGuid().ToString("n");
        var version = (existing?.StateVersion ?? 0) + 1;
        var createdAt = existing?.CreatedAt ?? now;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "ZaloConversationStatesV2" (
                "Id", "GroupId", "SenderZaloUserId", "Intent", "CollectedArgumentsJson",
                "MissingArgumentsJson", "CandidateEntitiesJson", "SourceMessageId", "LastMessageId",
                "StateVersion", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt")
            VALUES (@id, @groupId, @senderId, @intent, @collected, @missing, @candidates,
                    @sourceMessageId, @lastMessageId, @version, 'Active', @expiresAt, @createdAt, @updatedAt)
            ON CONFLICT ("GroupId", "SenderZaloUserId") DO UPDATE SET
                "Intent" = excluded."Intent",
                "CollectedArgumentsJson" = excluded."CollectedArgumentsJson",
                "MissingArgumentsJson" = excluded."MissingArgumentsJson",
                "CandidateEntitiesJson" = excluded."CandidateEntitiesJson",
                "SourceMessageId" = excluded."SourceMessageId",
                "LastMessageId" = excluded."LastMessageId",
                "StateVersion" = excluded."StateVersion",
                "Status" = 'Active',
                "ExpiresAt" = excluded."ExpiresAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        Add(command, "@id", id);
        Add(command, "@groupId", groupId);
        Add(command, "@senderId", senderZaloUserId);
        Add(command, "@intent", intent);
        Add(command, "@collected", collectedArgumentsJson);
        Add(command, "@missing", missingArgumentsJson);
        Add(command, "@candidates", candidateEntitiesJson);
        Add(command, "@sourceMessageId", sourceMessageId);
        Add(command, "@lastMessageId", lastMessageId);
        Add(command, "@version", version);
        Add(command, "@expiresAt", expiresAt);
        Add(command, "@createdAt", createdAt);
        Add(command, "@updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new ZaloConversationStateV2Snapshot(
            id, groupId, senderZaloUserId, intent, collectedArgumentsJson, missingArgumentsJson,
            candidateEntitiesJson, sourceMessageId, lastMessageId, version,
            ZaloConversationStateV2Status.Active, expiresAt, createdAt, now);
    }

    public Task<int> CancelAsync(string groupId, string senderZaloUserId, CancellationToken cancellationToken = default) =>
        SetStatusAsync(groupId, senderZaloUserId, ZaloConversationStateV2Status.Cancelled, cancellationToken);

    public Task<int> CompleteAsync(string groupId, string senderZaloUserId, CancellationToken cancellationToken = default) =>
        SetStatusAsync(groupId, senderZaloUserId, ZaloConversationStateV2Status.Completed, cancellationToken);

    public static ZaloTopicSwitchDecision DecideTopicSwitch(
        string pendingIntent,
        string currentQuestion,
        string? freshIntent,
        double freshConfidence = 1)
    {
        var normalized = ZaloBotIntelligence.Normalize(currentQuestion ?? string.Empty);
        if (ZaloBotIntelligence.IsCancel(normalized)) return ZaloTopicSwitchDecision.CancelPending;
        if (ZaloBotIntelligence.IsConfirmation(normalized)) return ZaloTopicSwitchDecision.ContinuePending;
        if (string.IsNullOrWhiteSpace(freshIntent) || freshConfidence < .85)
            return ZaloTopicSwitchDecision.ContinuePending;
        if (string.Equals(pendingIntent, freshIntent, StringComparison.OrdinalIgnoreCase))
            return ZaloTopicSwitchDecision.ContinuePending;
        return ZaloTopicSwitchDecision.SwitchToNewIntent;
    }

    private async Task<int> SetStatusAsync(
        string groupId,
        string senderZaloUserId,
        ZaloConversationStateV2Status status,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "ZaloConversationStatesV2"
            SET "Status" = @status, "UpdatedAt" = @updatedAt
            WHERE "GroupId" = @groupId AND "SenderZaloUserId" = @senderId AND "Status" = 'Active';
            """;
        Add(command, "@status", status.ToString());
        Add(command, "@updatedAt", DateTimeOffset.UtcNow);
        Add(command, "@groupId", Clean(groupId, 100));
        Add(command, "@senderId", Clean(senderZaloUserId, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ZaloConversationStateV2Snapshot?> LoadAnyAsync(
        DbConnection connection,
        string groupId,
        string senderZaloUserId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "GroupId", "SenderZaloUserId", "Intent", "CollectedArgumentsJson",
                   "MissingArgumentsJson", "CandidateEntitiesJson", "SourceMessageId", "LastMessageId",
                   "StateVersion", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloConversationStatesV2"
            WHERE "GroupId" = @groupId AND "SenderZaloUserId" = @senderId
            LIMIT 1;
            """;
        Add(command, "@groupId", groupId);
        Add(command, "@senderId", senderZaloUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var isPostgres = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var isSqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        if (!isPostgres && !isSqlite) return;

        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            var sql = isPostgres
                ? """
                    CREATE TABLE IF NOT EXISTS "ZaloConversationStatesV2" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloConversationStatesV2" PRIMARY KEY,
                        "GroupId" TEXT NOT NULL,
                        "SenderZaloUserId" TEXT NOT NULL,
                        "Intent" TEXT NOT NULL,
                        "CollectedArgumentsJson" TEXT NOT NULL,
                        "MissingArgumentsJson" TEXT NOT NULL DEFAULT '[]',
                        "CandidateEntitiesJson" TEXT NOT NULL DEFAULT '[]',
                        "SourceMessageId" TEXT NULL,
                        "LastMessageId" TEXT NULL,
                        "StateVersion" INTEGER NOT NULL DEFAULT 1,
                        "Status" TEXT NOT NULL DEFAULT 'Active',
                        "ExpiresAt" timestamp with time zone NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "UX_ZaloConversationStatesV2_Scope" UNIQUE ("GroupId", "SenderZaloUserId")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloConversationStatesV2_Active"
                    ON "ZaloConversationStatesV2" ("GroupId", "SenderZaloUserId", "Status", "ExpiresAt");
                    """
                : """
                    CREATE TABLE IF NOT EXISTS "ZaloConversationStatesV2" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ZaloConversationStatesV2" PRIMARY KEY,
                        "GroupId" TEXT NOT NULL,
                        "SenderZaloUserId" TEXT NOT NULL,
                        "Intent" TEXT NOT NULL,
                        "CollectedArgumentsJson" TEXT NOT NULL,
                        "MissingArgumentsJson" TEXT NOT NULL DEFAULT '[]',
                        "CandidateEntitiesJson" TEXT NOT NULL DEFAULT '[]',
                        "SourceMessageId" TEXT NULL,
                        "LastMessageId" TEXT NULL,
                        "StateVersion" INTEGER NOT NULL DEFAULT 1,
                        "Status" TEXT NOT NULL DEFAULT 'Active',
                        "ExpiresAt" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL,
                        CONSTRAINT "UX_ZaloConversationStatesV2_Scope" UNIQUE ("GroupId", "SenderZaloUserId")
                    );
                    CREATE INDEX IF NOT EXISTS "IX_ZaloConversationStatesV2_Active"
                    ON "ZaloConversationStatesV2" ("GroupId", "SenderZaloUserId", "Status", "ExpiresAt");
                    """;
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static ZaloConversationStateV2Snapshot Read(DbDataReader reader)
    {
        var statusText = reader.GetString(10);
        _ = Enum.TryParse<ZaloConversationStateV2Status>(statusText, true, out var status);
        return new ZaloConversationStateV2Snapshot(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), NullableString(reader, 7),
            NullableString(reader, 8), Convert.ToInt32(reader.GetValue(9)), status,
            Timestamp(reader.GetValue(11)), Timestamp(reader.GetValue(12)), Timestamp(reader.GetValue(13)));
    }

    private static string Json(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? "{}" : text.Length <= 8000 ? text : text[..8000];
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var text = Clean(value, maxLength);
        return text.Length == 0 ? null : text;
    }

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

    private static DateTimeOffset Timestamp(object value)
    {
        if (value is DateTimeOffset dto) return dto;
        if (value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        return DateTimeOffset.TryParse(Convert.ToString(value), out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
    }

    private static async Task OpenIfNeededAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}