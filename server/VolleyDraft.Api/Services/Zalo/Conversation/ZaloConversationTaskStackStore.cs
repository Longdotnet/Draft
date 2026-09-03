using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloConversationTaskStatus
{
    Active,
    Completed,
    Cancelled,
    Expired,
    Superseded
}

internal sealed record ZaloConversationTaskSnapshot(
    string TaskKey,
    string GroupId,
    string SenderZaloUserId,
    string Domain,
    string Intent,
    string SessionId,
    string SessionName,
    string CollectedArgumentsJson,
    string MissingArgumentsJson,
    string CandidateEntitiesJson,
    string? SourceMessageId,
    string? LastMessageId,
    int Version,
    ZaloConversationTaskStatus Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable multi-task conversation memory. Unlike ZaloConversationStateV2Store,
/// this store is not unique by (group,sender): the same sender may concurrently
/// discuss guest work for T7 and CN, or keep a profile task beside a pending add.
/// Mutation authority never comes from this table; task rows only recover context.
/// </summary>
internal sealed class ZaloConversationTaskStackStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task<ZaloConversationTaskSnapshot> UpsertAsync(
        string taskKey,
        string groupId,
        string senderZaloUserId,
        string domain,
        string intent,
        string sessionId,
        string sessionName,
        string collectedArgumentsJson,
        string missingArgumentsJson,
        string candidateEntitiesJson,
        string? sourceMessageId,
        string? lastMessageId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        taskKey = Clean(taskKey, 180);
        groupId = Clean(groupId, 100);
        senderZaloUserId = Clean(senderZaloUserId, 100);
        domain = Clean(domain, 80);
        intent = Clean(intent, 120);
        sessionId = Clean(sessionId, 100);
        sessionName = Clean(sessionName, 160);
        collectedArgumentsJson = Json(collectedArgumentsJson);
        missingArgumentsJson = Json(missingArgumentsJson);
        candidateEntitiesJson = Json(candidateEntitiesJson);
        sourceMessageId = CleanOptional(sourceMessageId, 160);
        lastMessageId = CleanOptional(lastMessageId, 160);
        if (taskKey.Length == 0 || groupId.Length == 0 || senderZaloUserId.Length == 0 ||
            domain.Length == 0 || intent.Length == 0 || sessionId.Length == 0)
            throw new ArgumentException("Task key, group, sender, domain, intent and session are required.");
        if (expiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        await EnsureSchemaAsync(cancellationToken);
        var existing = await LoadByKeyAsync(taskKey, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var version = (existing?.Version ?? 0) + 1;
        var createdAt = existing?.CreatedAt ?? now;
        const string sql = """
            INSERT INTO "ZaloConversationTasks" (
                "TaskKey", "GroupId", "SenderZaloUserId", "Domain", "Intent", "SessionId", "SessionName",
                "CollectedArgumentsJson", "MissingArgumentsJson", "CandidateEntitiesJson",
                "SourceMessageId", "LastMessageId", "Version", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt")
            VALUES (
                @TaskKey, @GroupId, @SenderId, @Domain, @Intent, @SessionId, @SessionName,
                @Collected, @Missing, @Candidates, @SourceMessageId, @LastMessageId,
                @Version, 'Active', @ExpiresAt, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("TaskKey") DO UPDATE SET
                "GroupId" = excluded."GroupId",
                "SenderZaloUserId" = excluded."SenderZaloUserId",
                "Domain" = excluded."Domain",
                "Intent" = excluded."Intent",
                "SessionId" = excluded."SessionId",
                "SessionName" = excluded."SessionName",
                "CollectedArgumentsJson" = excluded."CollectedArgumentsJson",
                "MissingArgumentsJson" = excluded."MissingArgumentsJson",
                "CandidateEntitiesJson" = excluded."CandidateEntitiesJson",
                "SourceMessageId" = excluded."SourceMessageId",
                "LastMessageId" = excluded."LastMessageId",
                "Version" = excluded."Version",
                "Status" = 'Active',
                "ExpiresAt" = excluded."ExpiresAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@TaskKey", taskKey);
        Add(command, "@GroupId", groupId);
        Add(command, "@SenderId", senderZaloUserId);
        Add(command, "@Domain", domain);
        Add(command, "@Intent", intent);
        Add(command, "@SessionId", sessionId);
        Add(command, "@SessionName", sessionName);
        Add(command, "@Collected", collectedArgumentsJson);
        Add(command, "@Missing", missingArgumentsJson);
        Add(command, "@Candidates", candidateEntitiesJson);
        Add(command, "@SourceMessageId", sourceMessageId);
        Add(command, "@LastMessageId", lastMessageId);
        Add(command, "@Version", version);
        Add(command, "@ExpiresAt", FormatDate(expiresAt));
        Add(command, "@CreatedAt", FormatDate(createdAt));
        Add(command, "@UpdatedAt", FormatDate(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await LoadByKeyAsync(taskKey, cancellationToken))!;
    }

    public async Task<IReadOnlyList<ZaloConversationTaskSnapshot>> LoadActiveAsync(
        string groupId,
        string senderZaloUserId,
        string? domain = null,
        int max = 12,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        senderZaloUserId = Clean(senderZaloUserId, 100);
        domain = CleanOptional(domain, 80);
        if (groupId.Length == 0 || senderZaloUserId.Length == 0) return [];
        await EnsureSchemaAsync(cancellationToken);
        var sql = domain is null
            ? """
              SELECT "TaskKey", "GroupId", "SenderZaloUserId", "Domain", "Intent", "SessionId", "SessionName",
                     "CollectedArgumentsJson", "MissingArgumentsJson", "CandidateEntitiesJson",
                     "SourceMessageId", "LastMessageId", "Version", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt"
              FROM "ZaloConversationTasks"
              WHERE "GroupId" = @GroupId AND "SenderZaloUserId" = @SenderId AND "Status" = 'Active';
              """
            : """
              SELECT "TaskKey", "GroupId", "SenderZaloUserId", "Domain", "Intent", "SessionId", "SessionName",
                     "CollectedArgumentsJson", "MissingArgumentsJson", "CandidateEntitiesJson",
                     "SourceMessageId", "LastMessageId", "Version", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt"
              FROM "ZaloConversationTasks"
              WHERE "GroupId" = @GroupId AND "SenderZaloUserId" = @SenderId AND "Domain" = @Domain AND "Status" = 'Active';
              """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@GroupId", groupId);
        Add(command, "@SenderId", senderZaloUserId);
        if (domain is not null) Add(command, "@Domain", domain);
        var rows = new List<ZaloConversationTaskSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        await reader.DisposeAsync();

        var now = DateTimeOffset.UtcNow;
        foreach (var expired in rows.Where(item => item.ExpiresAt <= now).ToArray())
            await SetStatusAsync(expired.TaskKey, ZaloConversationTaskStatus.Expired, cancellationToken);
        return rows
            .Where(item => item.ExpiresAt > now)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Version)
            .Take(Math.Clamp(max, 1, 50))
            .ToArray();
    }

    public Task<int> CompleteAsync(string taskKey, CancellationToken cancellationToken = default) =>
        SetStatusAsync(taskKey, ZaloConversationTaskStatus.Completed, cancellationToken);

    public Task<int> CancelAsync(string taskKey, CancellationToken cancellationToken = default) =>
        SetStatusAsync(taskKey, ZaloConversationTaskStatus.Cancelled, cancellationToken);

    public async Task<int> CompleteSessionDomainAsync(
        string groupId,
        string senderZaloUserId,
        string domain,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloConversationTasks"
            SET "Status" = 'Completed', "UpdatedAt" = @UpdatedAt
            WHERE "GroupId" = @GroupId AND "SenderZaloUserId" = @SenderId
              AND "Domain" = @Domain AND "SessionId" = @SessionId AND "Status" = 'Active';
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@UpdatedAt", FormatDate(DateTimeOffset.UtcNow));
        Add(command, "@GroupId", Clean(groupId, 100));
        Add(command, "@SenderId", Clean(senderZaloUserId, 100));
        Add(command, "@Domain", Clean(domain, 80));
        Add(command, "@SessionId", Clean(sessionId, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static ZaloConversationTaskSnapshot? SelectForMessage(
        IReadOnlyList<ZaloConversationTaskSnapshot> tasks,
        string? message)
    {
        if (tasks.Count == 0) return null;
        var normalized = ZaloBotIntelligence.Normalize(message ?? string.Empty);
        var named = tasks
            .Where(item =>
            {
                var session = ZaloBotIntelligence.Normalize(item.SessionName);
                return session.Length >= 2 && normalized.Contains(session, StringComparison.Ordinal);
            })
            .OrderByDescending(item => item.UpdatedAt)
            .Take(2)
            .ToArray();
        if (named.Length == 1) return named[0];
        if (named.Length > 1) return null;
        return tasks.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
    }

    private async Task<ZaloConversationTaskSnapshot?> LoadByKeyAsync(
        string taskKey,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            SELECT "TaskKey", "GroupId", "SenderZaloUserId", "Domain", "Intent", "SessionId", "SessionName",
                   "CollectedArgumentsJson", "MissingArgumentsJson", "CandidateEntitiesJson",
                   "SourceMessageId", "LastMessageId", "Version", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloConversationTasks" WHERE "TaskKey" = @TaskKey LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@TaskKey", Clean(taskKey, 180));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private async Task<int> SetStatusAsync(
        string taskKey,
        ZaloConversationTaskStatus status,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloConversationTasks" SET "Status" = @Status, "UpdatedAt" = @UpdatedAt
            WHERE "TaskKey" = @TaskKey AND "Status" = 'Active';
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Status", status.ToString());
        Add(command, "@UpdatedAt", FormatDate(DateTimeOffset.UtcNow));
        Add(command, "@TaskKey", Clean(taskKey, 180));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) &&
            !provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;
        await SchemaGate.WaitAsync(cancellationToken);
        try
        {
            const string sql = """
                CREATE TABLE IF NOT EXISTS "ZaloConversationTasks" (
                    "TaskKey" TEXT NOT NULL PRIMARY KEY,
                    "GroupId" TEXT NOT NULL,
                    "SenderZaloUserId" TEXT NOT NULL,
                    "Domain" TEXT NOT NULL,
                    "Intent" TEXT NOT NULL,
                    "SessionId" TEXT NOT NULL,
                    "SessionName" TEXT NOT NULL,
                    "CollectedArgumentsJson" TEXT NOT NULL DEFAULT '{}',
                    "MissingArgumentsJson" TEXT NOT NULL DEFAULT '[]',
                    "CandidateEntitiesJson" TEXT NOT NULL DEFAULT '[]',
                    "SourceMessageId" TEXT NULL,
                    "LastMessageId" TEXT NULL,
                    "Version" INTEGER NOT NULL DEFAULT 1,
                    "Status" TEXT NOT NULL DEFAULT 'Active',
                    "ExpiresAt" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_ZaloConversationTasks_SenderActive"
                    ON "ZaloConversationTasks" ("GroupId", "SenderZaloUserId", "Domain", "Status");
                CREATE INDEX IF NOT EXISTS "IX_ZaloConversationTasks_Session"
                    ON "ZaloConversationTasks" ("SessionId", "Status");
                """;
            await using var command = await CreateCommandAsync(sql, cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            SchemaGate.Release();
        }
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

    private static ZaloConversationTaskSnapshot Read(DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
        Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture),
        Enum.TryParse<ZaloConversationTaskStatus>(reader.GetString(13), out var status) ? status : ZaloConversationTaskStatus.Active,
        ParseDate(reader.GetValue(14)), ParseDate(reader.GetValue(15)), ParseDate(reader.GetValue(16)));

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(object value) => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Json(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length == 0 ? "{}" : text.Length <= 12000 ? text : text[..12000];
    }
    private static string Clean(string? value, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }
    private static string? CleanOptional(string? value, int max)
    {
        var text = Clean(value, max);
        return text.Length == 0 ? null : text;
    }
}
