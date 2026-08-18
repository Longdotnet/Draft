using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloAutoSessionSettingsStore(VolleyDraftDbContext db)
{
    private readonly ZaloAutoSessionStore baseStore = new(db);

    public Task EnsureAsync(CancellationToken cancellationToken = default) =>
        baseStore.EnsureAsync(cancellationToken);

    public async Task<IReadOnlyList<ZaloTrackedGroupData>> GetForAdminAsync(
        string adminUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloTrackedGroups\" WHERE \"AdminUserId\" = @AdminUserId ORDER BY \"GroupName\", \"UpdatedAt\" DESC;",
            cancellationToken);
        AddParameter(command, "@AdminUserId", adminUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloTrackedGroupData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadTrackedGroup(reader));
        return result;
    }

    public async Task<ZaloTrackedGroupData?> GetForAdminAsync(
        string adminUserId,
        string trackedGroupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloTrackedGroups\" WHERE \"Id\" = @Id AND \"AdminUserId\" = @AdminUserId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@Id", trackedGroupId);
        AddParameter(command, "@AdminUserId", adminUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTrackedGroup(reader) : null;
    }

    public async Task<ZaloTrackedGroupData?> GetByConnectionAndGroupAsync(
        string adminUserId,
        string connectionId,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloTrackedGroups\" WHERE \"AdminUserId\" = @AdminUserId AND \"ZaloConnectionId\" = @ConnectionId AND \"GroupId\" = @GroupId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@AdminUserId", adminUserId);
        AddParameter(command, "@ConnectionId", connectionId);
        AddParameter(command, "@GroupId", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTrackedGroup(reader) : null;
    }

    public async Task<ZaloTrackedGroupData> InsertIfMissingAsync(
        ZaloTrackedGroupData tracked,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        tracked.Id = string.IsNullOrWhiteSpace(tracked.Id) ? Guid.NewGuid().ToString("n") : tracked.Id;
        tracked.DefaultTeamCount = 3;
        tracked.CreatedAt = tracked.CreatedAt == default ? DateTimeOffset.UtcNow : tracked.CreatedAt;
        tracked.UpdatedAt = DateTimeOffset.UtcNow;
        const string sql = """
            INSERT INTO "ZaloTrackedGroups" (
                "Id", "AdminUserId", "ZaloConnectionId", "GroupId", "GroupName",
                "AutoSessionEnabled", "RequireOrganizerApproval", "DefaultTeamCount", "DefaultTeamSize",
                "DefaultTotalSets", "DefaultStartMinutes", "AssumePmForHourUnder12", "DefaultLocation",
                "BotEnabledForCreatedSessions", "CreatedAt", "UpdatedAt")
            VALUES (
                @Id, @AdminUserId, @ZaloConnectionId, @GroupId, @GroupName,
                @AutoSessionEnabled, @RequireOrganizerApproval, 3, @DefaultTeamSize,
                @DefaultTotalSets, @DefaultStartMinutes, @AssumePmForHourUnder12, @DefaultLocation,
                @BotEnabledForCreatedSessions, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("ZaloConnectionId", "GroupId") DO NOTHING;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        BindTrackedGroup(command, tracked);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetByConnectionAndGroupAsync(
                   tracked.AdminUserId,
                   tracked.ZaloConnectionId,
                   tracked.GroupId,
                   cancellationToken)
               ?? throw new InvalidOperationException("Tracked Zalo group was not persisted.");
    }

    public async Task<ZaloTrackedGroupData?> UpdateAsync(
        ZaloTrackedGroupData tracked,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        tracked.DefaultTeamCount = 3;
        tracked.UpdatedAt = DateTimeOffset.UtcNow;
        const string sql = """
            UPDATE "ZaloTrackedGroups"
            SET "GroupName" = @GroupName,
                "AutoSessionEnabled" = @AutoSessionEnabled,
                "RequireOrganizerApproval" = @RequireOrganizerApproval,
                "DefaultTeamCount" = 3,
                "DefaultTeamSize" = @DefaultTeamSize,
                "DefaultTotalSets" = @DefaultTotalSets,
                "DefaultStartMinutes" = @DefaultStartMinutes,
                "AssumePmForHourUnder12" = @AssumePmForHourUnder12,
                "DefaultLocation" = @DefaultLocation,
                "BotEnabledForCreatedSessions" = @BotEnabledForCreatedSessions,
                "UpdatedAt" = @UpdatedAt
            WHERE "Id" = @Id AND "AdminUserId" = @AdminUserId;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        BindTrackedGroup(command, tracked);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0) return null;
        return await GetForAdminAsync(tracked.AdminUserId, tracked.Id, cancellationToken);
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

    private static void BindTrackedGroup(DbCommand command, ZaloTrackedGroupData tracked)
    {
        AddParameter(command, "@Id", tracked.Id);
        AddParameter(command, "@AdminUserId", tracked.AdminUserId);
        AddParameter(command, "@ZaloConnectionId", tracked.ZaloConnectionId);
        AddParameter(command, "@GroupId", tracked.GroupId);
        AddParameter(command, "@GroupName", tracked.GroupName);
        AddParameter(command, "@AutoSessionEnabled", tracked.AutoSessionEnabled ? 1 : 0);
        AddParameter(command, "@RequireOrganizerApproval", tracked.RequireOrganizerApproval ? 1 : 0);
        AddParameter(command, "@DefaultTeamSize", tracked.DefaultTeamSize);
        AddParameter(command, "@DefaultTotalSets", tracked.DefaultTotalSets);
        AddParameter(command, "@DefaultStartMinutes", tracked.DefaultStartMinutes);
        AddParameter(command, "@AssumePmForHourUnder12", tracked.AssumePmForHourUnder12 ? 1 : 0);
        AddParameter(command, "@DefaultLocation", tracked.DefaultLocation);
        AddParameter(command, "@BotEnabledForCreatedSessions", tracked.BotEnabledForCreatedSessions ? 1 : 0);
        AddParameter(command, "@CreatedAt", FormatDate(tracked.CreatedAt));
        AddParameter(command, "@UpdatedAt", FormatDate(tracked.UpdatedAt));
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static ZaloTrackedGroupData ReadTrackedGroup(DbDataReader reader) => new()
    {
        Id = ReadString(reader, "Id") ?? string.Empty,
        AdminUserId = ReadString(reader, "AdminUserId") ?? string.Empty,
        ZaloConnectionId = ReadString(reader, "ZaloConnectionId") ?? string.Empty,
        GroupId = ReadString(reader, "GroupId") ?? string.Empty,
        GroupName = ReadString(reader, "GroupName") ?? string.Empty,
        AutoSessionEnabled = ReadInt(reader, "AutoSessionEnabled", 1) != 0,
        RequireOrganizerApproval = ReadInt(reader, "RequireOrganizerApproval", 1) != 0,
        DefaultTeamCount = ReadInt(reader, "DefaultTeamCount", 3),
        DefaultTeamSize = ReadInt(reader, "DefaultTeamSize", 6),
        DefaultTotalSets = ReadInt(reader, "DefaultTotalSets", 4),
        DefaultStartMinutes = ReadInt(reader, "DefaultStartMinutes", 1050),
        AssumePmForHourUnder12 = ReadInt(reader, "AssumePmForHourUnder12", 1) != 0,
        DefaultLocation = ReadString(reader, "DefaultLocation"),
        BotEnabledForCreatedSessions = ReadInt(reader, "BotEnabledForCreatedSessions", 1) != 0,
        CreatedAt = ReadDate(reader, "CreatedAt") ?? DateTimeOffset.MinValue,
        UpdatedAt = ReadDate(reader, "UpdatedAt") ?? DateTimeOffset.MinValue
    };

    private static string? ReadString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int ReadInt(DbDataReader reader, string name, int fallback = 0)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? fallback
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadDate(DbDataReader reader, string name)
    {
        var raw = ReadString(reader, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private static string? FormatDate(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);
}
