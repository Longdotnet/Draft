using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloAutoSessionRolloutMode
{
    Disabled,
    PreviewOnly,
    Live
}

internal enum ZaloAutoSessionLearningStatus
{
    Pending,
    Approved,
    Rejected
}

internal sealed record ZaloAutoSessionRuntimeData(
    bool GlobalEnabled,
    DateTimeOffset UpdatedAt,
    string? UpdatedByUserId);

internal sealed record ZaloAutoSessionHealthData(
    string TrackedGroupId,
    DateTimeOffset? LastPollEventAt,
    DateTimeOffset? LastReconcileAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastErrorAt,
    string? LastError,
    int ConsecutiveFailures,
    DateTimeOffset? NextRetryAt);

internal sealed record ZaloAutoSessionLearningSignalData(
    string Id,
    string TrackedGroupId,
    string ProposalId,
    string PollId,
    string OptionId,
    string OrganizerZaloUserId,
    string SignalType,
    string? DayKey,
    DateTimeOffset? OriginalStartTime,
    DateTimeOffset? ActualStartTime,
    string? SuggestedRuleType,
    int? SuggestedMinutes,
    ZaloAutoSessionLearningStatus Status,
    string? ReviewNote,
    string? ReviewedByUserId,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed class ZaloAutoSessionV2Store(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionRuntimeSettings" (
                "Id" TEXT PRIMARY KEY,
                "GlobalEnabled" INTEGER NOT NULL DEFAULT 1,
                "UpdatedAt" TEXT NOT NULL,
                "UpdatedByUserId" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionGroupPolicies" (
                "TrackedGroupId" TEXT PRIMARY KEY,
                "RolloutMode" TEXT NOT NULL DEFAULT 'Live',
                "UpdatedAt" TEXT NOT NULL,
                "UpdatedByUserId" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionHealth" (
                "TrackedGroupId" TEXT PRIMARY KEY,
                "LastPollEventAt" TEXT NULL,
                "LastReconcileAt" TEXT NULL,
                "LastSuccessAt" TEXT NULL,
                "LastErrorAt" TEXT NULL,
                "LastError" TEXT NULL,
                "ConsecutiveFailures" INTEGER NOT NULL DEFAULT 0,
                "NextRetryAt" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionLearningSignals" (
                "Id" TEXT PRIMARY KEY,
                "TrackedGroupId" TEXT NOT NULL,
                "ProposalId" TEXT NOT NULL,
                "PollId" TEXT NOT NULL,
                "OptionId" TEXT NOT NULL,
                "OrganizerZaloUserId" TEXT NOT NULL,
                "SignalType" TEXT NOT NULL,
                "DayKey" TEXT NULL,
                "OriginalStartTime" TEXT NULL,
                "ActualStartTime" TEXT NULL,
                "SuggestedRuleType" TEXT NULL,
                "SuggestedMinutes" INTEGER NULL,
                "Status" TEXT NOT NULL DEFAULT 'Pending',
                "ReviewNote" TEXT NULL,
                "ReviewedByUserId" TEXT NULL,
                "ReviewedAt" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                UNIQUE ("ProposalId", "OptionId", "SignalType")
            );

            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionHealth_Retry"
                ON "ZaloAutoSessionHealth" ("NextRetryAt");
            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionLearningSignals_GroupStatus"
                ON "ZaloAutoSessionLearningSignals" ("TrackedGroupId", "Status", "UpdatedAt");
            """;
        await using (var command = await CreateCommandAsync(sql, cancellationToken))
            await command.ExecuteNonQueryAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        await using (var command = await CreateCommandAsync(
                         "INSERT INTO \"ZaloAutoSessionRuntimeSettings\" (\"Id\", \"GlobalEnabled\", \"UpdatedAt\") VALUES ('global', 1, @UpdatedAt) ON CONFLICT (\"Id\") DO NOTHING;",
                         cancellationToken))
        {
            AddParameter(command, "@UpdatedAt", FormatDate(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        ensured = true;
    }

    public async Task<ZaloAutoSessionRuntimeData> GetRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT \"GlobalEnabled\", \"UpdatedAt\", \"UpdatedByUserId\" FROM \"ZaloAutoSessionRuntimeSettings\" WHERE \"Id\" = 'global' LIMIT 1;",
            cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new(true, DateTimeOffset.UtcNow, null);
        return new(
            ReadInt(reader, "GlobalEnabled", 1) != 0,
            ReadDate(reader, "UpdatedAt") ?? DateTimeOffset.MinValue,
            ReadString(reader, "UpdatedByUserId"));
    }

    public async Task<ZaloAutoSessionRuntimeData> SetGlobalEnabledAsync(
        bool enabled,
        string adminUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using var command = await CreateCommandAsync(
            "UPDATE \"ZaloAutoSessionRuntimeSettings\" SET \"GlobalEnabled\" = @Enabled, \"UpdatedAt\" = @UpdatedAt, \"UpdatedByUserId\" = @AdminUserId WHERE \"Id\" = 'global';",
            cancellationToken);
        AddParameter(command, "@Enabled", enabled ? 1 : 0);
        AddParameter(command, "@UpdatedAt", FormatDate(now));
        AddParameter(command, "@AdminUserId", adminUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new(enabled, now, adminUserId);
    }

    public async Task<ZaloAutoSessionRolloutMode> GetRolloutModeAsync(
        string trackedGroupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT \"RolloutMode\" FROM \"ZaloAutoSessionGroupPolicies\" WHERE \"TrackedGroupId\" = @TrackedGroupId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        var raw = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return Enum.TryParse<ZaloAutoSessionRolloutMode>(raw, true, out var parsed)
            ? parsed
            : ZaloAutoSessionRolloutMode.Live;
    }

    public async Task<ZaloAutoSessionRolloutMode> SetRolloutModeAsync(
        string trackedGroupId,
        ZaloAutoSessionRolloutMode mode,
        string adminUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        const string sql = """
            INSERT INTO "ZaloAutoSessionGroupPolicies" ("TrackedGroupId", "RolloutMode", "UpdatedAt", "UpdatedByUserId")
            VALUES (@TrackedGroupId, @RolloutMode, @UpdatedAt, @AdminUserId)
            ON CONFLICT ("TrackedGroupId") DO UPDATE SET
                "RolloutMode" = excluded."RolloutMode",
                "UpdatedAt" = excluded."UpdatedAt",
                "UpdatedByUserId" = excluded."UpdatedByUserId";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        AddParameter(command, "@RolloutMode", mode.ToString());
        AddParameter(command, "@UpdatedAt", FormatDate(now));
        AddParameter(command, "@AdminUserId", adminUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return mode;
    }

    public async Task<ZaloAutoSessionHealthData> GetHealthAsync(
        string trackedGroupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloAutoSessionHealth\" WHERE \"TrackedGroupId\" = @TrackedGroupId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadHealth(reader)
            : new(trackedGroupId, null, null, null, null, null, 0, null);
    }

    public async Task RecordPollEventAsync(string trackedGroupId, CancellationToken cancellationToken = default)
    {
        await EnsureHealthRowAsync(trackedGroupId, cancellationToken);
        await UpdateHealthTimeAsync(trackedGroupId, "LastPollEventAt", DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task RecordReconcileAsync(string trackedGroupId, CancellationToken cancellationToken = default)
    {
        await EnsureHealthRowAsync(trackedGroupId, cancellationToken);
        await UpdateHealthTimeAsync(trackedGroupId, "LastReconcileAt", DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task RecordSuccessAsync(string trackedGroupId, CancellationToken cancellationToken = default)
    {
        await EnsureHealthRowAsync(trackedGroupId, cancellationToken);
        await using var command = await CreateCommandAsync(
            "UPDATE \"ZaloAutoSessionHealth\" SET \"LastSuccessAt\" = @Now, \"LastError\" = NULL, \"ConsecutiveFailures\" = 0, \"NextRetryAt\" = NULL WHERE \"TrackedGroupId\" = @TrackedGroupId;",
            cancellationToken);
        AddParameter(command, "@Now", FormatDate(DateTimeOffset.UtcNow));
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ZaloAutoSessionHealthData> RecordErrorAsync(
        string trackedGroupId,
        string error,
        CancellationToken cancellationToken = default)
    {
        var current = await GetHealthAsync(trackedGroupId, cancellationToken);
        var failures = Math.Clamp(current.ConsecutiveFailures + 1, 1, 20);
        var seconds = Math.Min(15 * 60, 30 * (1 << Math.Min(failures - 1, 5)));
        var now = DateTimeOffset.UtcNow;
        var nextRetry = now.AddSeconds(seconds);
        await EnsureHealthRowAsync(trackedGroupId, cancellationToken);
        await using var command = await CreateCommandAsync(
            "UPDATE \"ZaloAutoSessionHealth\" SET \"LastErrorAt\" = @Now, \"LastError\" = @Error, \"ConsecutiveFailures\" = @Failures, \"NextRetryAt\" = @NextRetryAt WHERE \"TrackedGroupId\" = @TrackedGroupId;",
            cancellationToken);
        AddParameter(command, "@Now", FormatDate(now));
        AddParameter(command, "@Error", Truncate(error, 1000));
        AddParameter(command, "@Failures", failures);
        AddParameter(command, "@NextRetryAt", FormatDate(nextRetry));
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new(trackedGroupId, current.LastPollEventAt, current.LastReconcileAt, current.LastSuccessAt, now, Truncate(error, 1000), failures, nextRetry);
    }

    public async Task<bool> IsRetryDueAsync(string trackedGroupId, CancellationToken cancellationToken = default)
    {
        var health = await GetHealthAsync(trackedGroupId, cancellationToken);
        return health.NextRetryAt is null || health.NextRetryAt <= DateTimeOffset.UtcNow;
    }

    public async Task AddLearningSignalAsync(
        ZaloAutoSessionLearningSignalData signal,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloAutoSessionLearningSignals" (
                "Id", "TrackedGroupId", "ProposalId", "PollId", "OptionId", "OrganizerZaloUserId",
                "SignalType", "DayKey", "OriginalStartTime", "ActualStartTime", "SuggestedRuleType",
                "SuggestedMinutes", "Status", "ReviewNote", "ReviewedByUserId", "ReviewedAt", "CreatedAt", "UpdatedAt")
            VALUES (
                @Id, @TrackedGroupId, @ProposalId, @PollId, @OptionId, @OrganizerZaloUserId,
                @SignalType, @DayKey, @OriginalStartTime, @ActualStartTime, @SuggestedRuleType,
                @SuggestedMinutes, @Status, @ReviewNote, @ReviewedByUserId, @ReviewedAt, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("ProposalId", "OptionId", "SignalType") DO NOTHING;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        BindLearning(command, signal);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ZaloAutoSessionLearningSignalData>> GetLearningSignalsAsync(
        string trackedGroupId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        limit = Math.Clamp(limit, 1, 100);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloAutoSessionLearningSignals\" WHERE \"TrackedGroupId\" = @TrackedGroupId ORDER BY CASE WHEN \"Status\" = 'Pending' THEN 0 ELSE 1 END, \"UpdatedAt\" DESC LIMIT @Limit;",
            cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        AddParameter(command, "@Limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloAutoSessionLearningSignalData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadLearning(reader));
        return result;
    }

    public async Task<ZaloAutoSessionLearningSignalData?> ReviewLearningSignalAsync(
        string trackedGroupId,
        string signalId,
        ZaloAutoSessionLearningStatus status,
        string adminUserId,
        string? note,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        if (status == ZaloAutoSessionLearningStatus.Pending) return null;
        var now = DateTimeOffset.UtcNow;
        await using var command = await CreateCommandAsync(
            "UPDATE \"ZaloAutoSessionLearningSignals\" SET \"Status\" = @Status, \"ReviewNote\" = @Note, \"ReviewedByUserId\" = @AdminUserId, \"ReviewedAt\" = @Now, \"UpdatedAt\" = @Now WHERE \"Id\" = @Id AND \"TrackedGroupId\" = @TrackedGroupId;",
            cancellationToken);
        AddParameter(command, "@Status", status.ToString());
        AddParameter(command, "@Note", Truncate(note, 500));
        AddParameter(command, "@AdminUserId", adminUserId);
        AddParameter(command, "@Now", FormatDate(now));
        AddParameter(command, "@Id", signalId);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        var items = await GetLearningSignalsAsync(trackedGroupId, 100, cancellationToken);
        return items.FirstOrDefault(item => item.Id == signalId);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetApprovedDayTimeRulesAsync(
        string trackedGroupId,
        CancellationToken cancellationToken = default)
    {
        var signals = await GetLearningSignalsAsync(trackedGroupId, 100, cancellationToken);
        return signals
            .Where(item => item.Status == ZaloAutoSessionLearningStatus.Approved &&
                           string.Equals(item.SuggestedRuleType, "default_day_time", StringComparison.Ordinal) &&
                           !string.IsNullOrWhiteSpace(item.DayKey) &&
                           item.SuggestedMinutes is not null)
            .GroupBy(item => item.DayKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.ReviewedAt ?? item.UpdatedAt).First().SuggestedMinutes!.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureHealthRowAsync(string trackedGroupId, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "INSERT INTO \"ZaloAutoSessionHealth\" (\"TrackedGroupId\", \"ConsecutiveFailures\") VALUES (@TrackedGroupId, 0) ON CONFLICT (\"TrackedGroupId\") DO NOTHING;",
            cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateHealthTimeAsync(
        string trackedGroupId,
        string column,
        DateTimeOffset value,
        CancellationToken cancellationToken)
    {
        if (column is not ("LastPollEventAt" or "LastReconcileAt"))
            throw new ArgumentOutOfRangeException(nameof(column));
        await using var command = await CreateCommandAsync(
            $"UPDATE \"ZaloAutoSessionHealth\" SET \"{column}\" = @Value WHERE \"TrackedGroupId\" = @TrackedGroupId;",
            cancellationToken);
        AddParameter(command, "@Value", FormatDate(value));
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static void BindLearning(DbCommand command, ZaloAutoSessionLearningSignalData signal)
    {
        AddParameter(command, "@Id", signal.Id);
        AddParameter(command, "@TrackedGroupId", signal.TrackedGroupId);
        AddParameter(command, "@ProposalId", signal.ProposalId);
        AddParameter(command, "@PollId", signal.PollId);
        AddParameter(command, "@OptionId", signal.OptionId);
        AddParameter(command, "@OrganizerZaloUserId", signal.OrganizerZaloUserId);
        AddParameter(command, "@SignalType", signal.SignalType);
        AddParameter(command, "@DayKey", signal.DayKey);
        AddParameter(command, "@OriginalStartTime", FormatDate(signal.OriginalStartTime));
        AddParameter(command, "@ActualStartTime", FormatDate(signal.ActualStartTime));
        AddParameter(command, "@SuggestedRuleType", signal.SuggestedRuleType);
        AddParameter(command, "@SuggestedMinutes", signal.SuggestedMinutes);
        AddParameter(command, "@Status", signal.Status.ToString());
        AddParameter(command, "@ReviewNote", signal.ReviewNote);
        AddParameter(command, "@ReviewedByUserId", signal.ReviewedByUserId);
        AddParameter(command, "@ReviewedAt", FormatDate(signal.ReviewedAt));
        AddParameter(command, "@CreatedAt", FormatDate(signal.CreatedAt));
        AddParameter(command, "@UpdatedAt", FormatDate(signal.UpdatedAt));
    }

    private static ZaloAutoSessionHealthData ReadHealth(DbDataReader reader) => new(
        ReadString(reader, "TrackedGroupId") ?? string.Empty,
        ReadDate(reader, "LastPollEventAt"),
        ReadDate(reader, "LastReconcileAt"),
        ReadDate(reader, "LastSuccessAt"),
        ReadDate(reader, "LastErrorAt"),
        ReadString(reader, "LastError"),
        ReadInt(reader, "ConsecutiveFailures"),
        ReadDate(reader, "NextRetryAt"));

    private static ZaloAutoSessionLearningSignalData ReadLearning(DbDataReader reader) => new(
        ReadString(reader, "Id") ?? string.Empty,
        ReadString(reader, "TrackedGroupId") ?? string.Empty,
        ReadString(reader, "ProposalId") ?? string.Empty,
        ReadString(reader, "PollId") ?? string.Empty,
        ReadString(reader, "OptionId") ?? string.Empty,
        ReadString(reader, "OrganizerZaloUserId") ?? string.Empty,
        ReadString(reader, "SignalType") ?? string.Empty,
        ReadString(reader, "DayKey"),
        ReadDate(reader, "OriginalStartTime"),
        ReadDate(reader, "ActualStartTime"),
        ReadString(reader, "SuggestedRuleType"),
        ReadNullableInt(reader, "SuggestedMinutes"),
        Enum.TryParse<ZaloAutoSessionLearningStatus>(ReadString(reader, "Status"), true, out var status) ? status : ZaloAutoSessionLearningStatus.Pending,
        ReadString(reader, "ReviewNote"),
        ReadString(reader, "ReviewedByUserId"),
        ReadDate(reader, "ReviewedAt"),
        ReadDate(reader, "CreatedAt") ?? DateTimeOffset.MinValue,
        ReadDate(reader, "UpdatedAt") ?? DateTimeOffset.MinValue);

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? ReadString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int ReadInt(DbDataReader reader, string name, int fallback = 0)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? fallback : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int? ReadNullableInt(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadDate(DbDataReader reader, string name)
    {
        var raw = ReadString(reader, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
    }

    private static string FormatDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static string? FormatDate(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture);
    private static string? Truncate(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
