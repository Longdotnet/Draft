using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloAutoSessionTrustedOrganizerData(
    string Id,
    string TrackedGroupId,
    string ZaloUserId,
    string DisplayName,
    bool Enabled,
    string AddedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed class ZaloAutoSessionTrustedOrganizerStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionTrustedOrganizers" (
                "Id" TEXT PRIMARY KEY,
                "TrackedGroupId" TEXT NOT NULL,
                "ZaloUserId" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL DEFAULT '',
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "AddedByUserId" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                UNIQUE ("TrackedGroupId", "ZaloUserId")
            );

            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionTrustedOrganizers_Enabled"
                ON "ZaloAutoSessionTrustedOrganizers" ("TrackedGroupId", "Enabled");
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<IReadOnlyList<ZaloAutoSessionTrustedOrganizerData>> GetAsync(
        string trackedGroupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT "Id", "TrackedGroupId", "ZaloUserId", "DisplayName", "Enabled",
                   "AddedByUserId", "CreatedAt", "UpdatedAt"
            FROM "ZaloAutoSessionTrustedOrganizers"
            WHERE "TrackedGroupId" = @TrackedGroupId
            ORDER BY "Enabled" DESC, "DisplayName", "ZaloUserId";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloAutoSessionTrustedOrganizerData>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ZaloAutoSessionTrustedOrganizerData(
                reader.GetString(0),
                reader.GetString(1),
                NormalizeId(reader.GetString(2)),
                reader.GetString(3),
                ReadBool(reader, 4),
                reader.GetString(5),
                ParseDate(reader.GetValue(6)),
                ParseDate(reader.GetValue(7))));
        }
        return result;
    }

    public async Task<IReadOnlySet<string>> GetEnabledIdsAsync(
        string trackedGroupId,
        CancellationToken cancellationToken = default)
    {
        var rows = await GetAsync(trackedGroupId, cancellationToken);
        return rows
            .Where(item => item.Enabled)
            .Select(item => NormalizeId(item.ZaloUserId))
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<ZaloAutoSessionTrustedOrganizerData> SetAsync(
        string trackedGroupId,
        string zaloUserId,
        string? displayName,
        bool enabled,
        string addedByUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        zaloUserId = NormalizeId(zaloUserId);
        if (zaloUserId.Length == 0) throw new ArgumentException("Zalo user id is required.", nameof(zaloUserId));
        var now = DateTimeOffset.UtcNow;
        var name = string.IsNullOrWhiteSpace(displayName) ? zaloUserId : displayName.Trim();
        if (name.Length > 200) name = name[..200];

        const string sql = """
            INSERT INTO "ZaloAutoSessionTrustedOrganizers" (
                "Id", "TrackedGroupId", "ZaloUserId", "DisplayName", "Enabled",
                "AddedByUserId", "CreatedAt", "UpdatedAt")
            VALUES (
                @Id, @TrackedGroupId, @ZaloUserId, @DisplayName, @Enabled,
                @AddedByUserId, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("TrackedGroupId", "ZaloUserId") DO UPDATE SET
                "DisplayName" = excluded."DisplayName",
                "Enabled" = excluded."Enabled",
                "AddedByUserId" = excluded."AddedByUserId",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@Id", Guid.NewGuid().ToString("n"));
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        AddParameter(command, "@ZaloUserId", zaloUserId);
        AddParameter(command, "@DisplayName", name);
        AddParameter(command, "@Enabled", enabled ? 1 : 0);
        AddParameter(command, "@AddedByUserId", addedByUserId);
        AddParameter(command, "@CreatedAt", FormatDate(now));
        AddParameter(command, "@UpdatedAt", FormatDate(now));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await GetAsync(trackedGroupId, cancellationToken))
            .Single(item => string.Equals(item.ZaloUserId, zaloUserId, StringComparison.Ordinal));
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

    private static bool ReadBool(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool boolValue => boolValue,
            byte byteValue => byteValue != 0,
            short shortValue => shortValue != 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0
        };
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(object value) =>
        value is DateTimeOffset offset
            ? offset
            : DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }
}
