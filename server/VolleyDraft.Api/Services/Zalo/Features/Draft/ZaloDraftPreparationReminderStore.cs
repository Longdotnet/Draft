using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloDraftPreparationReminderState(
    string SessionId,
    string? LastBucketKey,
    int? LastSlotCount,
    int LastOpenOfferCount,
    string? LastFingerprint,
    DateTimeOffset? LastReminderAt,
    DateTimeOffset UpdatedAt);

internal sealed class ZaloDraftPreparationReminderStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloDraftPreparationReminderStates" (
                "SessionId" TEXT PRIMARY KEY,
                "LastBucketKey" TEXT NULL,
                "LastSlotCount" INTEGER NULL,
                "LastOpenOfferCount" INTEGER NOT NULL DEFAULT 0,
                "LastFingerprint" TEXT NULL,
                "LastReminderAt" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<ZaloDraftPreparationReminderState?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT "SessionId", "LastBucketKey", "LastSlotCount", "LastOpenOfferCount",
                   "LastFingerprint", "LastReminderAt", "UpdatedAt"
            FROM "ZaloDraftPreparationReminderStates"
            WHERE "SessionId" = @SessionId
            LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@SessionId", Clean(sessionId, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ZaloDraftPreparationReminderState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : ParseDate(reader.GetValue(5)),
            ParseDate(reader.GetValue(6)));
    }

    public async Task MarkHandledAsync(
        string sessionId,
        string bucketKey,
        int slotCount,
        int openOfferCount,
        string? fingerprint,
        DateTimeOffset? reminderAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        const string sql = """
            INSERT INTO "ZaloDraftPreparationReminderStates" (
                "SessionId", "LastBucketKey", "LastSlotCount", "LastOpenOfferCount",
                "LastFingerprint", "LastReminderAt", "UpdatedAt")
            VALUES (
                @SessionId, @LastBucketKey, @LastSlotCount, @LastOpenOfferCount,
                @LastFingerprint, @LastReminderAt, @UpdatedAt)
            ON CONFLICT ("SessionId") DO UPDATE SET
                "LastBucketKey" = excluded."LastBucketKey",
                "LastSlotCount" = excluded."LastSlotCount",
                "LastOpenOfferCount" = excluded."LastOpenOfferCount",
                "LastFingerprint" = excluded."LastFingerprint",
                "LastReminderAt" = excluded."LastReminderAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@SessionId", Clean(sessionId, 100));
        AddParameter(command, "@LastBucketKey", Clean(bucketKey, 40));
        AddParameter(command, "@LastSlotCount", slotCount);
        AddParameter(command, "@LastOpenOfferCount", Math.Max(0, openOfferCount));
        AddParameter(command, "@LastFingerprint", string.IsNullOrWhiteSpace(fingerprint) ? null : Clean(fingerprint, 160));
        AddParameter(command, "@LastReminderAt", reminderAt is null ? null : FormatDate(reminderAt.Value));
        AddParameter(command, "@UpdatedAt", FormatDate(now));
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

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
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

    private static string Clean(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
