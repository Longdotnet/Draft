using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloRecruitmentRosterObservation(
    string SessionId,
    int StableEffectiveSlotCount,
    int StablePresentPlayerCount,
    string StableFingerprint,
    int? PendingDropFromCount,
    int? PendingDropToCount,
    DateTimeOffset? PendingDropStartedAt,
    DateTimeOffset LastObservedAt,
    DateTimeOffset? LastDropAt,
    DateTimeOffset? LastDropNotifiedAt,
    int? LastDropFromCount,
    int? LastDropToCount,
    DateTimeOffset UpdatedAt)
{
    public bool HasUnnotifiedDrop =>
        LastDropAt is not null && LastDropNotifiedAt is null &&
        LastDropFromCount is not null && LastDropToCount is not null;
}

internal sealed class ZaloRecruitmentRosterObservationStore(VolleyDraftDbContext db)
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task<ZaloRecruitmentRosterObservation?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            SELECT "SessionId", "StableEffectiveSlotCount", "StablePresentPlayerCount", "StableFingerprint",
                   "PendingDropFromCount", "PendingDropToCount", "PendingDropStartedAt", "LastObservedAt",
                   "LastDropAt", "LastDropNotifiedAt", "LastDropFromCount", "LastDropToCount", "UpdatedAt"
            FROM "ZaloRecruitmentRosterObservations"
            WHERE "SessionId" = @SessionId LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@SessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task SaveAsync(
        ZaloRecruitmentRosterObservation state,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloRecruitmentRosterObservations" (
                "SessionId", "StableEffectiveSlotCount", "StablePresentPlayerCount", "StableFingerprint",
                "PendingDropFromCount", "PendingDropToCount", "PendingDropStartedAt", "LastObservedAt",
                "LastDropAt", "LastDropNotifiedAt", "LastDropFromCount", "LastDropToCount", "UpdatedAt")
            VALUES (
                @SessionId, @StableSlots, @StablePresent, @Fingerprint,
                @PendingFrom, @PendingTo, @PendingStartedAt, @LastObservedAt,
                @LastDropAt, @LastDropNotifiedAt, @LastDropFrom, @LastDropTo, @UpdatedAt)
            ON CONFLICT ("SessionId") DO UPDATE SET
                "StableEffectiveSlotCount" = excluded."StableEffectiveSlotCount",
                "StablePresentPlayerCount" = excluded."StablePresentPlayerCount",
                "StableFingerprint" = excluded."StableFingerprint",
                "PendingDropFromCount" = excluded."PendingDropFromCount",
                "PendingDropToCount" = excluded."PendingDropToCount",
                "PendingDropStartedAt" = excluded."PendingDropStartedAt",
                "LastObservedAt" = excluded."LastObservedAt",
                "LastDropAt" = excluded."LastDropAt",
                "LastDropNotifiedAt" = excluded."LastDropNotifiedAt",
                "LastDropFromCount" = excluded."LastDropFromCount",
                "LastDropToCount" = excluded."LastDropToCount",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@SessionId", state.SessionId);
        Add(command, "@StableSlots", state.StableEffectiveSlotCount);
        Add(command, "@StablePresent", state.StablePresentPlayerCount);
        Add(command, "@Fingerprint", state.StableFingerprint);
        Add(command, "@PendingFrom", state.PendingDropFromCount);
        Add(command, "@PendingTo", state.PendingDropToCount);
        Add(command, "@PendingStartedAt", FormatNullableDate(state.PendingDropStartedAt));
        Add(command, "@LastObservedAt", FormatDate(state.LastObservedAt));
        Add(command, "@LastDropAt", FormatNullableDate(state.LastDropAt));
        Add(command, "@LastDropNotifiedAt", FormatNullableDate(state.LastDropNotifiedAt));
        Add(command, "@LastDropFrom", state.LastDropFromCount);
        Add(command, "@LastDropTo", state.LastDropToCount);
        Add(command, "@UpdatedAt", FormatDate(state.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = "DELETE FROM \"ZaloRecruitmentRosterObservations\" WHERE \"SessionId\" = @SessionId;";
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@SessionId", sessionId);
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
                CREATE TABLE IF NOT EXISTS "ZaloRecruitmentRosterObservations" (
                    "SessionId" TEXT NOT NULL PRIMARY KEY,
                    "StableEffectiveSlotCount" INTEGER NOT NULL,
                    "StablePresentPlayerCount" INTEGER NOT NULL,
                    "StableFingerprint" TEXT NOT NULL,
                    "PendingDropFromCount" INTEGER NULL,
                    "PendingDropToCount" INTEGER NULL,
                    "PendingDropStartedAt" TEXT NULL,
                    "LastObservedAt" TEXT NOT NULL,
                    "LastDropAt" TEXT NULL,
                    "LastDropNotifiedAt" TEXT NULL,
                    "LastDropFromCount" INTEGER NULL,
                    "LastDropToCount" INTEGER NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
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

    private static ZaloRecruitmentRosterObservation Read(DbDataReader reader) => new(
        reader.GetString(0),
        Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
        Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
        reader.GetString(3),
        NullableInt(reader, 4),
        NullableInt(reader, 5),
        NullableDate(reader, 6),
        ParseDate(reader.GetValue(7)),
        NullableDate(reader, 8),
        NullableDate(reader, 9),
        NullableInt(reader, 10),
        NullableInt(reader, 11),
        ParseDate(reader.GetValue(12)));

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static int? NullableInt(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static DateTimeOffset? NullableDate(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetValue(ordinal));
    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? FormatNullableDate(DateTimeOffset? value) => value is null ? null : FormatDate(value.Value);
    private static DateTimeOffset ParseDate(object value) =>
        DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
