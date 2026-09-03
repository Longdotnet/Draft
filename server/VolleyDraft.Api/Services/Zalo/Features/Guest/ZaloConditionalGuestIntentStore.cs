using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloConditionalGuestIntentStatus
{
    Active,
    Executed,
    SkippedConditionFalse,
    Cancelled,
    Expired
}

internal sealed record ZaloConditionalGuestIntentSnapshot(
    string Id,
    string SessionId,
    string GroupId,
    string SponsorZaloUserId,
    string SponsorDisplayName,
    string SourceMessageId,
    string RecruitmentMessageId,
    DateTimeOffset RequestedTriggerAt,
    DateTimeOffset ExecuteNotBeforeAt,
    int MinimumMissingSlots,
    int Quantity,
    string GuestsJson,
    ZaloConditionalGuestIntentStatus Status,
    string? LastError,
    DateTimeOffset? ExecutedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed class ZaloConditionalGuestIntentStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task<ZaloConditionalGuestIntentSnapshot> CreateOrReuseAsync(
        string sessionId,
        string groupId,
        string sponsorZaloUserId,
        string sponsorDisplayName,
        string sourceMessageId,
        string recruitmentMessageId,
        DateTimeOffset requestedTriggerAt,
        DateTimeOffset executeNotBeforeAt,
        int minimumMissingSlots,
        int quantity,
        string guestsJson,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var existing = await LoadBySourceMessageAsync(sourceMessageId, cancellationToken);
        if (existing is not null) return existing;

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("n");
        const string sql = """
            INSERT INTO "ZaloConditionalGuestIntents" (
                "Id", "SessionId", "GroupId", "SponsorZaloUserId", "SponsorDisplayName",
                "SourceMessageId", "RecruitmentMessageId", "RequestedTriggerAt", "ExecuteNotBeforeAt",
                "MinimumMissingSlots", "Quantity", "GuestsJson", "Status", "LastError",
                "ExecutedAt", "CreatedAt", "UpdatedAt")
            VALUES (
                @Id, @SessionId, @GroupId, @SponsorId, @SponsorName,
                @SourceMessageId, @RecruitmentMessageId, @RequestedTriggerAt, @ExecuteNotBeforeAt,
                @MinimumMissingSlots, @Quantity, @GuestsJson, 'Active', NULL,
                NULL, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("SourceMessageId") DO NOTHING;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Id", id);
        Add(command, "@SessionId", Clean(sessionId, 100));
        Add(command, "@GroupId", Clean(groupId, 100));
        Add(command, "@SponsorId", Clean(sponsorZaloUserId, 100));
        Add(command, "@SponsorName", Clean(sponsorDisplayName, 160));
        Add(command, "@SourceMessageId", Clean(sourceMessageId, 180));
        Add(command, "@RecruitmentMessageId", Clean(recruitmentMessageId, 180));
        Add(command, "@RequestedTriggerAt", FormatDate(requestedTriggerAt));
        Add(command, "@ExecuteNotBeforeAt", FormatDate(executeNotBeforeAt));
        Add(command, "@MinimumMissingSlots", Math.Clamp(minimumMissingSlots, 1, 2));
        Add(command, "@Quantity", Math.Clamp(quantity, 1, 2));
        Add(command, "@GuestsJson", Json(guestsJson));
        Add(command, "@CreatedAt", FormatDate(now));
        Add(command, "@UpdatedAt", FormatDate(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await LoadBySourceMessageAsync(sourceMessageId, cancellationToken))!;
    }

    public async Task<IReadOnlyList<ZaloConditionalGuestIntentSnapshot>> LoadDueAsync(
        DateTimeOffset now,
        int max = 50,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            SELECT "Id", "SessionId", "GroupId", "SponsorZaloUserId", "SponsorDisplayName",
                   "SourceMessageId", "RecruitmentMessageId", "RequestedTriggerAt", "ExecuteNotBeforeAt",
                   "MinimumMissingSlots", "Quantity", "GuestsJson", "Status", "LastError",
                   "ExecutedAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloConditionalGuestIntents"
            WHERE "Status" = 'Active' AND "ExecuteNotBeforeAt" <= @Now;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Now", FormatDate(now));
        var rows = new List<ZaloConditionalGuestIntentSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows.OrderBy(item => item.ExecuteNotBeforeAt).Take(Math.Clamp(max, 1, 100)).ToArray();
    }

    public async Task<ZaloConditionalGuestIntentSnapshot?> LoadBySourceMessageAsync(
        string sourceMessageId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            SELECT "Id", "SessionId", "GroupId", "SponsorZaloUserId", "SponsorDisplayName",
                   "SourceMessageId", "RecruitmentMessageId", "RequestedTriggerAt", "ExecuteNotBeforeAt",
                   "MinimumMissingSlots", "Quantity", "GuestsJson", "Status", "LastError",
                   "ExecutedAt", "CreatedAt", "UpdatedAt"
            FROM "ZaloConditionalGuestIntents"
            WHERE "SourceMessageId" = @SourceMessageId LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@SourceMessageId", Clean(sourceMessageId, 180));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task SetStatusAsync(
        string id,
        ZaloConditionalGuestIntentStatus status,
        string? lastError,
        DateTimeOffset? executedAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloConditionalGuestIntents"
            SET "Status" = @Status, "LastError" = @LastError, "ExecutedAt" = @ExecutedAt, "UpdatedAt" = @UpdatedAt
            WHERE "Id" = @Id;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Status", status.ToString());
        Add(command, "@LastError", CleanOptional(lastError, 1000));
        Add(command, "@ExecutedAt", executedAt is null ? null : FormatDate(executedAt.Value));
        Add(command, "@UpdatedAt", FormatDate(DateTimeOffset.UtcNow));
        Add(command, "@Id", Clean(id, 100));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetRetryErrorAsync(
        string id,
        string error,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            UPDATE "ZaloConditionalGuestIntents"
            SET "LastError" = @LastError, "UpdatedAt" = @UpdatedAt
            WHERE "Id" = @Id AND "Status" = 'Active';
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@LastError", Clean(error, 1000));
        Add(command, "@UpdatedAt", FormatDate(DateTimeOffset.UtcNow));
        Add(command, "@Id", Clean(id, 100));
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                CREATE TABLE IF NOT EXISTS "ZaloConditionalGuestIntents" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "SessionId" TEXT NOT NULL,
                    "GroupId" TEXT NOT NULL,
                    "SponsorZaloUserId" TEXT NOT NULL,
                    "SponsorDisplayName" TEXT NOT NULL,
                    "SourceMessageId" TEXT NOT NULL,
                    "RecruitmentMessageId" TEXT NOT NULL,
                    "RequestedTriggerAt" TEXT NOT NULL,
                    "ExecuteNotBeforeAt" TEXT NOT NULL,
                    "MinimumMissingSlots" INTEGER NOT NULL,
                    "Quantity" INTEGER NOT NULL,
                    "GuestsJson" TEXT NOT NULL DEFAULT '[]',
                    "Status" TEXT NOT NULL DEFAULT 'Active',
                    "LastError" TEXT NULL,
                    "ExecutedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZaloConditionalGuestIntents_SourceMessage"
                    ON "ZaloConditionalGuestIntents" ("SourceMessageId");
                CREATE INDEX IF NOT EXISTS "IX_ZaloConditionalGuestIntents_Due"
                    ON "ZaloConditionalGuestIntents" ("Status", "ExecuteNotBeforeAt");
                CREATE INDEX IF NOT EXISTS "IX_ZaloConditionalGuestIntents_Session"
                    ON "ZaloConditionalGuestIntents" ("SessionId", "Status");
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

    private static ZaloConditionalGuestIntentSnapshot Read(DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), ParseDate(reader.GetValue(7)), ParseDate(reader.GetValue(8)),
        Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture),
        Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture), reader.GetString(11),
        Enum.TryParse<ZaloConditionalGuestIntentStatus>(reader.GetString(12), out var status) ? status : ZaloConditionalGuestIntentStatus.Active,
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : ParseDate(reader.GetValue(14)),
        ParseDate(reader.GetValue(15)), ParseDate(reader.GetValue(16)));

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
        return text.Length == 0 ? "[]" : text.Length <= 12000 ? text : text[..12000];
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
