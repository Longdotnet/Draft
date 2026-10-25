using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloRecoveredNewcomerData(
    string Id,
    string ZaloConnectionId,
    string GroupId,
    string ZaloUserId,
    string DisplayName,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset FirstObservedAt,
    string? ConfirmedBy,
    string? SourceNote,
    bool IsActive,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Stores admin-confirmed newcomers whose provider join event was missed while the listener was offline.
/// This deliberately does not rewrite ZaloGroupMembershipPeriods or pretend FirstSeenAt is the actual join time.
/// </summary>
internal sealed class ZaloRecoveredNewcomerStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public Task EnsureAsync(CancellationToken cancellationToken = default) =>
        PostgresSchemaInitialization.EnsureAsync(db, nameof(ZaloRecoveredNewcomerStore), EnsureCoreAsync, cancellationToken);

    private async Task EnsureCoreAsync(CancellationToken cancellationToken)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloRecoveredNewcomers" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "ZaloUserId" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "ConfirmedAt" TEXT NOT NULL,
                "FirstObservedAt" TEXT NOT NULL,
                "ConfirmedBy" TEXT NULL,
                "SourceNote" TEXT NULL,
                "IsActive" INTEGER NOT NULL DEFAULT 1,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "UX_ZaloRecoveredNewcomers_Member"
                    UNIQUE ("ZaloConnectionId", "GroupId", "ZaloUserId")
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloRecoveredNewcomers_Active"
                ON "ZaloRecoveredNewcomers" ("ZaloConnectionId", "GroupId", "IsActive", "ConfirmedAt");
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<IReadOnlyList<ZaloRecoveredNewcomerData>> GetActiveAsync(
        string connectionId,
        string groupId,
        DateTimeOffset confirmedSince,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT * FROM "ZaloRecoveredNewcomers"
            WHERE "ZaloConnectionId" = @ConnectionId
              AND "GroupId" = @GroupId
              AND "IsActive" = 1
            ORDER BY "ConfirmedAt" DESC;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@ConnectionId", Clean(connectionId, 100));
        Add(command, "@GroupId", Clean(groupId, 100));
        var result = new List<ZaloRecoveredNewcomerData>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = Read(reader);
            if (row.ConfirmedAt >= confirmedSince) result.Add(row);
        }
        return result;
    }

    public async Task<ZaloRecoveredNewcomerData> UpsertAsync(
        string connectionId,
        string groupId,
        string zaloUserId,
        string displayName,
        DateTimeOffset firstObservedAt,
        string? confirmedBy,
        string? sourceNote,
        DateTimeOffset? confirmedAt = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var now = confirmedAt ?? DateTimeOffset.UtcNow;
        const string sql = """
            INSERT INTO "ZaloRecoveredNewcomers" (
                "Id", "ZaloConnectionId", "GroupId", "ZaloUserId", "DisplayName",
                "ConfirmedAt", "FirstObservedAt", "ConfirmedBy", "SourceNote", "IsActive", "UpdatedAt")
            VALUES (
                @Id, @ConnectionId, @GroupId, @ZaloUserId, @DisplayName,
                @ConfirmedAt, @FirstObservedAt, @ConfirmedBy, @SourceNote, 1, @UpdatedAt)
            ON CONFLICT ("ZaloConnectionId", "GroupId", "ZaloUserId") DO UPDATE SET
                "DisplayName" = excluded."DisplayName",
                "ConfirmedAt" = excluded."ConfirmedAt",
                "FirstObservedAt" = excluded."FirstObservedAt",
                "ConfirmedBy" = excluded."ConfirmedBy",
                "SourceNote" = excluded."SourceNote",
                "IsActive" = 1,
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        Add(command, "@Id", Guid.NewGuid().ToString("n"));
        Add(command, "@ConnectionId", Clean(connectionId, 100));
        Add(command, "@GroupId", Clean(groupId, 100));
        Add(command, "@ZaloUserId", Clean(zaloUserId, 100));
        Add(command, "@DisplayName", Clean(displayName, 160));
        Add(command, "@ConfirmedAt", FormatDate(now));
        Add(command, "@FirstObservedAt", FormatDate(firstObservedAt));
        Add(command, "@ConfirmedBy", CleanOptional(confirmedBy, 160));
        Add(command, "@SourceNote", CleanOptional(sourceNote, 500));
        Add(command, "@UpdatedAt", FormatDate(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);

        var rows = await GetActiveAsync(connectionId, groupId, DateTimeOffset.MinValue, cancellationToken);
        return rows.Single(item => item.ZaloUserId == Clean(zaloUserId, 100));
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

    private static ZaloRecoveredNewcomerData Read(DbDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("Id")),
        reader.GetString(reader.GetOrdinal("ZaloConnectionId")),
        reader.GetString(reader.GetOrdinal("GroupId")),
        reader.GetString(reader.GetOrdinal("ZaloUserId")),
        reader.GetString(reader.GetOrdinal("DisplayName")),
        ParseDate(reader.GetValue(reader.GetOrdinal("ConfirmedAt"))),
        ParseDate(reader.GetValue(reader.GetOrdinal("FirstObservedAt"))),
        ReadNullableString(reader, "ConfirmedBy"),
        ReadNullableString(reader, "SourceNote"),
        ReadBool(reader, "IsActive"),
        ParseDate(reader.GetValue(reader.GetOrdinal("UpdatedAt"))));

    private static bool ReadBool(DbDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            bool flag => flag,
            byte number => number != 0,
            short number => number != 0,
            int number => number != 0,
            long number => number != 0,
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0
        };
    }

    private static string? ReadNullableString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
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
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var text = Clean(value, maxLength);
        return text.Length == 0 ? null : text;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
