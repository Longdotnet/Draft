using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloDraftReminderTagPreferenceData(
    string Id,
    string TrackedGroupId,
    string ZaloConnectionId,
    string GroupId,
    string ZaloUserId,
    string DisplayName,
    bool Enabled,
    string UpdatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed class ZaloDraftReminderTagPreferenceStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloDraftReminderTagPreferences" (
                "Id" TEXT PRIMARY KEY,
                "TrackedGroupId" TEXT NOT NULL,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "ZaloUserId" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL DEFAULT '',
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "UpdatedByUserId" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                UNIQUE ("TrackedGroupId", "ZaloUserId")
            );

            CREATE INDEX IF NOT EXISTS "IX_ZaloDraftReminderTagPreferences_Group"
                ON "ZaloDraftReminderTagPreferences" ("ZaloConnectionId", "GroupId", "Enabled");
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<IReadOnlyList<ZaloDraftReminderTagPreferenceData>> GetForTrackedGroupAsync(
        string trackedGroupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT "Id", "TrackedGroupId", "ZaloConnectionId", "GroupId", "ZaloUserId",
                   "DisplayName", "Enabled", "UpdatedByUserId", "CreatedAt", "UpdatedAt"
            FROM "ZaloDraftReminderTagPreferences"
            WHERE "TrackedGroupId" = @TrackedGroupId
            ORDER BY "Enabled" DESC, "DisplayName", "ZaloUserId";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        return await ReadAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ZaloDraftReminderTagPreferenceData>> GetForGroupAsync(
        string zaloConnectionId,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT "Id", "TrackedGroupId", "ZaloConnectionId", "GroupId", "ZaloUserId",
                   "DisplayName", "Enabled", "UpdatedByUserId", "CreatedAt", "UpdatedAt"
            FROM "ZaloDraftReminderTagPreferences"
            WHERE "ZaloConnectionId" = @ZaloConnectionId AND "GroupId" = @GroupId
            ORDER BY "Enabled" DESC, "DisplayName", "ZaloUserId";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@ZaloConnectionId", Clean(zaloConnectionId, 100));
        AddParameter(command, "@GroupId", NormalizeId(groupId));
        return await ReadAsync(command, cancellationToken);
    }

    public async Task<ZaloDraftReminderTagPreferenceData> SetAsync(
        string trackedGroupId,
        string zaloConnectionId,
        string groupId,
        string zaloUserId,
        string? displayName,
        bool enabled,
        string updatedByUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        trackedGroupId = Clean(trackedGroupId, 100);
        zaloConnectionId = Clean(zaloConnectionId, 100);
        groupId = NormalizeId(groupId);
        zaloUserId = NormalizeId(zaloUserId);
        updatedByUserId = Clean(updatedByUserId, 100);
        if (trackedGroupId.Length == 0 || zaloConnectionId.Length == 0 || groupId.Length == 0 ||
            zaloUserId.Length == 0 || updatedByUserId.Length == 0)
            throw new ArgumentException("Tracked group, Zalo group/user and updater are required.");

        var now = DateTimeOffset.UtcNow;
        var name = string.IsNullOrWhiteSpace(displayName) ? zaloUserId : displayName.Trim();
        if (name.Length > 200) name = name[..200];

        const string sql = """
            INSERT INTO "ZaloDraftReminderTagPreferences" (
                "Id", "TrackedGroupId", "ZaloConnectionId", "GroupId", "ZaloUserId",
                "DisplayName", "Enabled", "UpdatedByUserId", "CreatedAt", "UpdatedAt")
            VALUES (
                @Id, @TrackedGroupId, @ZaloConnectionId, @GroupId, @ZaloUserId,
                @DisplayName, @Enabled, @UpdatedByUserId, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("TrackedGroupId", "ZaloUserId") DO UPDATE SET
                "ZaloConnectionId" = excluded."ZaloConnectionId",
                "GroupId" = excluded."GroupId",
                "DisplayName" = excluded."DisplayName",
                "Enabled" = excluded."Enabled",
                "UpdatedByUserId" = excluded."UpdatedByUserId",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@Id", Guid.NewGuid().ToString("n"));
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        AddParameter(command, "@ZaloConnectionId", zaloConnectionId);
        AddParameter(command, "@GroupId", groupId);
        AddParameter(command, "@ZaloUserId", zaloUserId);
        AddParameter(command, "@DisplayName", name);
        AddParameter(command, "@Enabled", enabled ? 1 : 0);
        AddParameter(command, "@UpdatedByUserId", updatedByUserId);
        AddParameter(command, "@CreatedAt", FormatDate(now));
        AddParameter(command, "@UpdatedAt", FormatDate(now));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await GetForTrackedGroupAsync(trackedGroupId, cancellationToken))
            .Single(item => string.Equals(item.ZaloUserId, zaloUserId, StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<ZaloDraftReminderTagPreferenceData>> ReadAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloDraftReminderTagPreferenceData>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ZaloDraftReminderTagPreferenceData(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                NormalizeId(reader.GetString(3)),
                NormalizeId(reader.GetString(4)),
                reader.GetString(5),
                ReadBool(reader, 6),
                reader.GetString(7),
                ParseDate(reader.GetValue(8)),
                ParseDate(reader.GetValue(9))));
        }
        return result;
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
            : DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
