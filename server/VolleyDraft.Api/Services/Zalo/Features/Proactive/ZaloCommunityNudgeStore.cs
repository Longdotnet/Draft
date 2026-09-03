using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloCommunityNudgeHistoryData(
    string Id,
    string ConnectionId,
    string GroupId,
    string LocalDate,
    int SlotNumber,
    string NudgeType,
    string? SubjectName,
    string MessageText,
    DateTimeOffset SentAt,
    string? ProviderMessageId);

internal sealed class ZaloCommunityNudgeStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloCommunityNudgeSettings" (
                "ConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "DailyCount" INTEGER NOT NULL DEFAULT 1,
                "UpdatedBy" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL,
                PRIMARY KEY ("ConnectionId", "GroupId")
            );

            CREATE TABLE IF NOT EXISTS "ZaloCommunityNudgeHistory" (
                "Id" TEXT PRIMARY KEY,
                "ConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "LocalDate" TEXT NOT NULL,
                "SlotNumber" INTEGER NOT NULL,
                "NudgeType" TEXT NOT NULL,
                "SubjectName" TEXT NULL,
                "MessageText" TEXT NOT NULL,
                "SentAt" TEXT NOT NULL,
                "ProviderMessageId" TEXT NULL,
                UNIQUE ("ConnectionId", "GroupId", "LocalDate", "SlotNumber")
            );

            CREATE INDEX IF NOT EXISTS "IX_ZaloCommunityNudgeHistory_GroupDate"
                ON "ZaloCommunityNudgeHistory" ("ConnectionId", "GroupId", "LocalDate", "SlotNumber");
            CREATE INDEX IF NOT EXISTS "IX_ZaloCommunityNudgeHistory_Subject"
                ON "ZaloCommunityNudgeHistory" ("ConnectionId", "GroupId", "SubjectName", "SentAt");
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<int> GetDailyCountAsync(
        string connectionId,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT \"DailyCount\" FROM \"ZaloCommunityNudgeSettings\" WHERE \"ConnectionId\" = @ConnectionId AND \"GroupId\" = @GroupId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@ConnectionId", CleanId(connectionId));
        AddParameter(command, "@GroupId", CleanId(groupId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 1 : Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 1, 5);
    }

    public async Task<int> SetDailyCountAsync(
        string connectionId,
        string groupId,
        int dailyCount,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        dailyCount = Math.Clamp(dailyCount, 1, 5);
        const string sql = """
            INSERT INTO "ZaloCommunityNudgeSettings" (
                "ConnectionId", "GroupId", "DailyCount", "UpdatedBy", "UpdatedAt")
            VALUES (@ConnectionId, @GroupId, @DailyCount, @UpdatedBy, @UpdatedAt)
            ON CONFLICT ("ConnectionId", "GroupId") DO UPDATE SET
                "DailyCount" = excluded."DailyCount",
                "UpdatedBy" = excluded."UpdatedBy",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@ConnectionId", CleanId(connectionId));
        AddParameter(command, "@GroupId", CleanId(groupId));
        AddParameter(command, "@DailyCount", dailyCount);
        AddParameter(command, "@UpdatedBy", string.IsNullOrWhiteSpace(updatedBy) ? null : CleanId(updatedBy));
        AddParameter(command, "@UpdatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return dailyCount;
    }

    public async Task<IReadOnlyList<ZaloCommunityNudgeHistoryData>> GetHistoryAsync(
        string connectionId,
        string groupId,
        int take = 60,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloCommunityNudgeHistory\" WHERE \"ConnectionId\" = @ConnectionId AND \"GroupId\" = @GroupId ORDER BY \"SentAt\" DESC LIMIT @Take;",
            cancellationToken);
        AddParameter(command, "@ConnectionId", CleanId(connectionId));
        AddParameter(command, "@GroupId", CleanId(groupId));
        AddParameter(command, "@Take", Math.Clamp(take, 1, 300));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloCommunityNudgeHistoryData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadHistory(reader));
        return result;
    }

    public async Task<bool> RecordAsync(
        ZaloCommunityNudgeHistoryData item,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloCommunityNudgeHistory" (
                "Id", "ConnectionId", "GroupId", "LocalDate", "SlotNumber", "NudgeType",
                "SubjectName", "MessageText", "SentAt", "ProviderMessageId")
            VALUES (
                @Id, @ConnectionId, @GroupId, @LocalDate, @SlotNumber, @NudgeType,
                @SubjectName, @MessageText, @SentAt, @ProviderMessageId)
            ON CONFLICT ("ConnectionId", "GroupId", "LocalDate", "SlotNumber") DO NOTHING;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@Id", item.Id);
        AddParameter(command, "@ConnectionId", CleanId(item.ConnectionId));
        AddParameter(command, "@GroupId", CleanId(item.GroupId));
        AddParameter(command, "@LocalDate", item.LocalDate);
        AddParameter(command, "@SlotNumber", item.SlotNumber);
        AddParameter(command, "@NudgeType", item.NudgeType);
        AddParameter(command, "@SubjectName", item.SubjectName);
        AddParameter(command, "@MessageText", item.MessageText);
        AddParameter(command, "@SentAt", item.SentAt.ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@ProviderMessageId", item.ProviderMessageId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
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

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static ZaloCommunityNudgeHistoryData ReadHistory(DbDataReader reader)
    {
        var sentAtRaw = Convert.ToString(reader["SentAt"], CultureInfo.InvariantCulture);
        DateTimeOffset.TryParse(sentAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sentAt);
        return new ZaloCommunityNudgeHistoryData(
            Convert.ToString(reader["Id"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader["ConnectionId"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader["GroupId"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader["LocalDate"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToInt32(reader["SlotNumber"], CultureInfo.InvariantCulture),
            Convert.ToString(reader["NudgeType"], CultureInfo.InvariantCulture) ?? string.Empty,
            reader["SubjectName"] is DBNull ? null : Convert.ToString(reader["SubjectName"], CultureInfo.InvariantCulture),
            Convert.ToString(reader["MessageText"], CultureInfo.InvariantCulture) ?? string.Empty,
            sentAt,
            reader["ProviderMessageId"] is DBNull ? null : Convert.ToString(reader["ProviderMessageId"], CultureInfo.InvariantCulture));
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}
