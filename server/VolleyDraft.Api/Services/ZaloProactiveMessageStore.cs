using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal static class ZaloProactiveLane
{
    public const string DailyGreeting = "daily_greeting";
    public const string SocialPresence = "social_presence";
    public const string Community = "community";
}

internal sealed record ZaloProactiveMessageHistoryData(
    string Id,
    string ConnectionId,
    string GroupId,
    string LocalDate,
    string Lane,
    string Kind,
    string? ContentKey,
    string? SubjectUserId,
    string? SubjectName,
    string MessageText,
    DateTimeOffset SentAt,
    string? ProviderMessageId,
    string IdempotencyKey);

/// <summary>
/// Durable coordination for every unsolicited NPC message lane.
///
/// The short lease prevents two background workers from racing each other before either
/// one has persisted a send. After the provider accepts a message the lease is extended
/// into the shared proactive cooldown. History is independent from Zalo webhook echo, so
/// reconnects or delayed outbound reflection cannot make the bot forget that it just spoke.
/// </summary>
internal sealed class ZaloProactiveMessageStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;

        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloProactiveMessageHistory" (
                "Id" TEXT PRIMARY KEY,
                "ConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "LocalDate" TEXT NOT NULL,
                "Lane" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "ContentKey" TEXT NULL,
                "SubjectUserId" TEXT NULL,
                "SubjectName" TEXT NULL,
                "MessageText" TEXT NOT NULL,
                "SentAt" TEXT NOT NULL,
                "ProviderMessageId" TEXT NULL,
                "IdempotencyKey" TEXT NOT NULL,
                UNIQUE ("ConnectionId", "GroupId", "IdempotencyKey")
            );

            CREATE INDEX IF NOT EXISTS "IX_ZaloProactiveHistory_GroupDate"
                ON "ZaloProactiveMessageHistory" ("ConnectionId", "GroupId", "LocalDate", "SentAt");
            CREATE INDEX IF NOT EXISTS "IX_ZaloProactiveHistory_Subject"
                ON "ZaloProactiveMessageHistory" ("ConnectionId", "GroupId", "Lane", "Kind", "SubjectUserId", "SentAt");
            CREATE INDEX IF NOT EXISTS "IX_ZaloProactiveHistory_Content"
                ON "ZaloProactiveMessageHistory" ("ConnectionId", "GroupId", "Lane", "ContentKey", "SentAt");

            CREATE TABLE IF NOT EXISTS "ZaloProactiveSendLease" (
                "ConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "LeaseUntil" TEXT NOT NULL,
                "IdempotencyKey" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                PRIMARY KEY ("ConnectionId", "GroupId")
            );
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<IReadOnlyList<ZaloProactiveMessageHistoryData>> GetHistoryAsync(
        string connectionId,
        string groupId,
        int take = 300,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            """
            SELECT *
            FROM "ZaloProactiveMessageHistory"
            WHERE "ConnectionId" = @ConnectionId AND "GroupId" = @GroupId
            ORDER BY "SentAt" DESC
            LIMIT @Take;
            """,
            cancellationToken);
        AddParameter(command, "@ConnectionId", CleanId(connectionId));
        AddParameter(command, "@GroupId", CleanId(groupId));
        AddParameter(command, "@Take", Math.Clamp(take, 1, 1000));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloProactiveMessageHistoryData>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadHistory(reader));
        return result;
    }

    public async Task<bool> TryAcquireLeaseAsync(
        string connectionId,
        string groupId,
        string idempotencyKey,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloProactiveSendLease" (
                "ConnectionId", "GroupId", "LeaseUntil", "IdempotencyKey", "UpdatedAt")
            VALUES (
                @ConnectionId, @GroupId, @LeaseUntil, @IdempotencyKey, @UpdatedAt)
            ON CONFLICT ("ConnectionId", "GroupId") DO UPDATE SET
                "LeaseUntil" = excluded."LeaseUntil",
                "IdempotencyKey" = excluded."IdempotencyKey",
                "UpdatedAt" = excluded."UpdatedAt"
            WHERE "LeaseUntil" <= @Now;
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@ConnectionId", CleanId(connectionId));
        AddParameter(command, "@GroupId", CleanId(groupId));
        AddParameter(command, "@LeaseUntil", now.Add(leaseDuration).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@IdempotencyKey", idempotencyKey);
        AddParameter(command, "@UpdatedAt", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@Now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task CommitCooldownAsync(
        string connectionId,
        string groupId,
        string idempotencyKey,
        DateTimeOffset cooldownUntil,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloProactiveSendLease"
            SET "LeaseUntil" = @LeaseUntil, "UpdatedAt" = @UpdatedAt
            WHERE "ConnectionId" = @ConnectionId
              AND "GroupId" = @GroupId
              AND "IdempotencyKey" = @IdempotencyKey;
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@ConnectionId", CleanId(connectionId));
        AddParameter(command, "@GroupId", CleanId(groupId));
        AddParameter(command, "@IdempotencyKey", idempotencyKey);
        AddParameter(command, "@LeaseUntil", cooldownUntil.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@UpdatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseLeaseAsync(
        string connectionId,
        string groupId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            DELETE FROM "ZaloProactiveSendLease"
            WHERE "ConnectionId" = @ConnectionId
              AND "GroupId" = @GroupId
              AND "IdempotencyKey" = @IdempotencyKey;
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@ConnectionId", CleanId(connectionId));
        AddParameter(command, "@GroupId", CleanId(groupId));
        AddParameter(command, "@IdempotencyKey", idempotencyKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RecordAsync(
        ZaloProactiveMessageHistoryData item,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloProactiveMessageHistory" (
                "Id", "ConnectionId", "GroupId", "LocalDate", "Lane", "Kind",
                "ContentKey", "SubjectUserId", "SubjectName", "MessageText",
                "SentAt", "ProviderMessageId", "IdempotencyKey")
            VALUES (
                @Id, @ConnectionId, @GroupId, @LocalDate, @Lane, @Kind,
                @ContentKey, @SubjectUserId, @SubjectName, @MessageText,
                @SentAt, @ProviderMessageId, @IdempotencyKey)
            ON CONFLICT ("ConnectionId", "GroupId", "IdempotencyKey") DO NOTHING;
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@Id", item.Id);
        AddParameter(command, "@ConnectionId", CleanId(item.ConnectionId));
        AddParameter(command, "@GroupId", CleanId(item.GroupId));
        AddParameter(command, "@LocalDate", item.LocalDate);
        AddParameter(command, "@Lane", item.Lane);
        AddParameter(command, "@Kind", item.Kind);
        AddParameter(command, "@ContentKey", item.ContentKey);
        AddParameter(command, "@SubjectUserId", CleanNullableId(item.SubjectUserId));
        AddParameter(command, "@SubjectName", item.SubjectName);
        AddParameter(command, "@MessageText", item.MessageText);
        AddParameter(command, "@SentAt", item.SentAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@ProviderMessageId", CleanNullableId(item.ProviderMessageId));
        AddParameter(command, "@IdempotencyKey", item.IdempotencyKey);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
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

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static ZaloProactiveMessageHistoryData ReadHistory(DbDataReader reader)
    {
        var sentAtRaw = Convert.ToString(reader["SentAt"], CultureInfo.InvariantCulture);
        DateTimeOffset.TryParse(
            sentAtRaw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var sentAt);

        return new ZaloProactiveMessageHistoryData(
            Convert.ToString(reader["Id"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader["ConnectionId"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader["GroupId"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader["LocalDate"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader["Lane"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader["Kind"], CultureInfo.InvariantCulture) ?? string.Empty,
            reader["ContentKey"] is DBNull ? null : Convert.ToString(reader["ContentKey"], CultureInfo.InvariantCulture),
            reader["SubjectUserId"] is DBNull ? null : Convert.ToString(reader["SubjectUserId"], CultureInfo.InvariantCulture),
            reader["SubjectName"] is DBNull ? null : Convert.ToString(reader["SubjectName"], CultureInfo.InvariantCulture),
            Convert.ToString(reader["MessageText"], CultureInfo.InvariantCulture) ?? string.Empty,
            sentAt,
            reader["ProviderMessageId"] is DBNull ? null : Convert.ToString(reader["ProviderMessageId"], CultureInfo.InvariantCulture),
            Convert.ToString(reader["IdempotencyKey"], CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    private static string? CleanNullableId(string? value)
    {
        var clean = CleanId(value);
        return clean.Length == 0 ? null : clean;
    }
}
