using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed class ZaloOverbookStateData
{
    public string SessionId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int GraceMinutes { get; set; } = 10;
    public int ReminderIntervalMinutes { get; set; } = 60;
    public int MaxReminders { get; set; } = 5;
    public ZaloOverbookMessageSource MessageSource { get; set; } = ZaloOverbookMessageSource.AdminPool;
    public List<string> FriendlyMessages { get; set; } = [];
    public List<string> SeriousMessages { get; set; } = [];
    public List<string> StrictMessages { get; set; } = [];
    public List<string> FirstObservedVoterIds { get; set; } = [];
    public List<string> LastObservedVoterIds { get; set; } = [];
    public List<string> SuggestedTargetVoterIds { get; set; } = [];
    public List<string> CurrentTargetVoterIds { get; set; } = [];
    public List<string> ConfirmedTargetVoterIds { get; set; } = [];
    public bool NeedsConfirmation { get; set; }
    public string OrderConfidence { get; set; } = "Unknown";
    public string? CurrentPollId { get; set; }
    public List<string> CurrentSelectedOptionIds { get; set; } = [];
    public long LastPollUpdatedAtUnixMs { get; set; }
    public int EffectiveSlotCount { get; set; }
    public int RawVoterCount { get; set; }
    public int ExcessSlotCount { get; set; }
    public int ReminderCount { get; set; }
    public DateTimeOffset? LastReminderAt { get; set; }
    public DateTimeOffset? NextReminderAt { get; set; }
    public string? IncidentKey { get; set; }
    public List<string> UsedMessageKeys { get; set; } = [];
    public string? LastMessageKey { get; set; }
    public string? LastActorId { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class ZaloOverbookStateStore(VolleyDraftDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloOverbookStates" (
                "SessionId" TEXT PRIMARY KEY,
                "Enabled" INTEGER NOT NULL DEFAULT 0,
                "GraceMinutes" INTEGER NOT NULL DEFAULT 10,
                "ReminderIntervalMinutes" INTEGER NOT NULL DEFAULT 60,
                "MaxReminders" INTEGER NOT NULL DEFAULT 5,
                "MessageSource" TEXT NOT NULL DEFAULT 'AdminPool',
                "FriendlyMessagesJson" TEXT NOT NULL DEFAULT '[]',
                "SeriousMessagesJson" TEXT NOT NULL DEFAULT '[]',
                "StrictMessagesJson" TEXT NOT NULL DEFAULT '[]',
                "FirstObservedVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                "LastObservedVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                "SuggestedTargetVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                "CurrentTargetVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                "ConfirmedTargetVoterIdsJson" TEXT NOT NULL DEFAULT '[]',
                "NeedsConfirmation" INTEGER NOT NULL DEFAULT 0,
                "OrderConfidence" TEXT NOT NULL DEFAULT 'Unknown',
                "CurrentPollId" TEXT NULL,
                "CurrentSelectedOptionIdsJson" TEXT NOT NULL DEFAULT '[]',
                "LastPollUpdatedAtUnixMs" INTEGER NOT NULL DEFAULT 0,
                "EffectiveSlotCount" INTEGER NOT NULL DEFAULT 0,
                "RawVoterCount" INTEGER NOT NULL DEFAULT 0,
                "ExcessSlotCount" INTEGER NOT NULL DEFAULT 0,
                "ReminderCount" INTEGER NOT NULL DEFAULT 0,
                "LastReminderAt" TEXT NULL,
                "NextReminderAt" TEXT NULL,
                "IncidentKey" TEXT NULL,
                "UsedMessageKeysJson" TEXT NOT NULL DEFAULT '[]',
                "LastMessageKey" TEXT NULL,
                "LastActorId" TEXT NULL,
                "LastObservedAt" TEXT NULL,
                "LastError" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        ensured = true;
    }

    public async Task<ZaloOverbookStateData?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloOverbookStates\" WHERE \"SessionId\" = @sessionId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<ZaloOverbookStateData>> GetEnabledAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloOverbookStates\" WHERE \"Enabled\" = 1 ORDER BY \"UpdatedAt\";",
            cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloOverbookStateData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task SaveAsync(ZaloOverbookStateData state, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        const string sql = """
            INSERT INTO "ZaloOverbookStates" (
                "SessionId", "Enabled", "GraceMinutes", "ReminderIntervalMinutes", "MaxReminders",
                "MessageSource", "FriendlyMessagesJson", "SeriousMessagesJson", "StrictMessagesJson",
                "FirstObservedVoterIdsJson", "LastObservedVoterIdsJson", "SuggestedTargetVoterIdsJson",
                "CurrentTargetVoterIdsJson", "ConfirmedTargetVoterIdsJson", "NeedsConfirmation", "OrderConfidence",
                "CurrentPollId", "CurrentSelectedOptionIdsJson", "LastPollUpdatedAtUnixMs", "EffectiveSlotCount",
                "RawVoterCount", "ExcessSlotCount", "ReminderCount", "LastReminderAt", "NextReminderAt",
                "IncidentKey", "UsedMessageKeysJson", "LastMessageKey", "LastActorId", "LastObservedAt", "LastError", "UpdatedAt")
            VALUES (
                @SessionId, @Enabled, @GraceMinutes, @ReminderIntervalMinutes, @MaxReminders,
                @MessageSource, @FriendlyMessagesJson, @SeriousMessagesJson, @StrictMessagesJson,
                @FirstObservedVoterIdsJson, @LastObservedVoterIdsJson, @SuggestedTargetVoterIdsJson,
                @CurrentTargetVoterIdsJson, @ConfirmedTargetVoterIdsJson, @NeedsConfirmation, @OrderConfidence,
                @CurrentPollId, @CurrentSelectedOptionIdsJson, @LastPollUpdatedAtUnixMs, @EffectiveSlotCount,
                @RawVoterCount, @ExcessSlotCount, @ReminderCount, @LastReminderAt, @NextReminderAt,
                @IncidentKey, @UsedMessageKeysJson, @LastMessageKey, @LastActorId, @LastObservedAt, @LastError, @UpdatedAt)
            ON CONFLICT ("SessionId") DO UPDATE SET
                "Enabled" = excluded."Enabled",
                "GraceMinutes" = excluded."GraceMinutes",
                "ReminderIntervalMinutes" = excluded."ReminderIntervalMinutes",
                "MaxReminders" = excluded."MaxReminders",
                "MessageSource" = excluded."MessageSource",
                "FriendlyMessagesJson" = excluded."FriendlyMessagesJson",
                "SeriousMessagesJson" = excluded."SeriousMessagesJson",
                "StrictMessagesJson" = excluded."StrictMessagesJson",
                "FirstObservedVoterIdsJson" = excluded."FirstObservedVoterIdsJson",
                "LastObservedVoterIdsJson" = excluded."LastObservedVoterIdsJson",
                "SuggestedTargetVoterIdsJson" = excluded."SuggestedTargetVoterIdsJson",
                "CurrentTargetVoterIdsJson" = excluded."CurrentTargetVoterIdsJson",
                "ConfirmedTargetVoterIdsJson" = excluded."ConfirmedTargetVoterIdsJson",
                "NeedsConfirmation" = excluded."NeedsConfirmation",
                "OrderConfidence" = excluded."OrderConfidence",
                "CurrentPollId" = excluded."CurrentPollId",
                "CurrentSelectedOptionIdsJson" = excluded."CurrentSelectedOptionIdsJson",
                "LastPollUpdatedAtUnixMs" = excluded."LastPollUpdatedAtUnixMs",
                "EffectiveSlotCount" = excluded."EffectiveSlotCount",
                "RawVoterCount" = excluded."RawVoterCount",
                "ExcessSlotCount" = excluded."ExcessSlotCount",
                "ReminderCount" = excluded."ReminderCount",
                "LastReminderAt" = excluded."LastReminderAt",
                "NextReminderAt" = excluded."NextReminderAt",
                "IncidentKey" = excluded."IncidentKey",
                "UsedMessageKeysJson" = excluded."UsedMessageKeysJson",
                "LastMessageKey" = excluded."LastMessageKey",
                "LastActorId" = excluded."LastActorId",
                "LastObservedAt" = excluded."LastObservedAt",
                "LastError" = excluded."LastError",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@SessionId", state.SessionId);
        AddParameter(command, "@Enabled", state.Enabled ? 1 : 0);
        AddParameter(command, "@GraceMinutes", state.GraceMinutes);
        AddParameter(command, "@ReminderIntervalMinutes", state.ReminderIntervalMinutes);
        AddParameter(command, "@MaxReminders", state.MaxReminders);
        AddParameter(command, "@MessageSource", state.MessageSource.ToString());
        AddParameter(command, "@FriendlyMessagesJson", Serialize(state.FriendlyMessages));
        AddParameter(command, "@SeriousMessagesJson", Serialize(state.SeriousMessages));
        AddParameter(command, "@StrictMessagesJson", Serialize(state.StrictMessages));
        AddParameter(command, "@FirstObservedVoterIdsJson", Serialize(state.FirstObservedVoterIds));
        AddParameter(command, "@LastObservedVoterIdsJson", Serialize(state.LastObservedVoterIds));
        AddParameter(command, "@SuggestedTargetVoterIdsJson", Serialize(state.SuggestedTargetVoterIds));
        AddParameter(command, "@CurrentTargetVoterIdsJson", Serialize(state.CurrentTargetVoterIds));
        AddParameter(command, "@ConfirmedTargetVoterIdsJson", Serialize(state.ConfirmedTargetVoterIds));
        AddParameter(command, "@NeedsConfirmation", state.NeedsConfirmation ? 1 : 0);
        AddParameter(command, "@OrderConfidence", state.OrderConfidence);
        AddParameter(command, "@CurrentPollId", state.CurrentPollId);
        AddParameter(command, "@CurrentSelectedOptionIdsJson", Serialize(state.CurrentSelectedOptionIds));
        AddParameter(command, "@LastPollUpdatedAtUnixMs", state.LastPollUpdatedAtUnixMs);
        AddParameter(command, "@EffectiveSlotCount", state.EffectiveSlotCount);
        AddParameter(command, "@RawVoterCount", state.RawVoterCount);
        AddParameter(command, "@ExcessSlotCount", state.ExcessSlotCount);
        AddParameter(command, "@ReminderCount", state.ReminderCount);
        AddParameter(command, "@LastReminderAt", FormatDate(state.LastReminderAt));
        AddParameter(command, "@NextReminderAt", FormatDate(state.NextReminderAt));
        AddParameter(command, "@IncidentKey", state.IncidentKey);
        AddParameter(command, "@UsedMessageKeysJson", Serialize(state.UsedMessageKeys));
        AddParameter(command, "@LastMessageKey", state.LastMessageKey);
        AddParameter(command, "@LastActorId", state.LastActorId);
        AddParameter(command, "@LastObservedAt", FormatDate(state.LastObservedAt));
        AddParameter(command, "@LastError", state.LastError);
        AddParameter(command, "@UpdatedAt", FormatDate(state.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static ZaloOverbookStateData Read(DbDataReader reader) => new()
    {
        SessionId = ReadString(reader, "SessionId") ?? string.Empty,
        Enabled = ReadInt(reader, "Enabled") != 0,
        GraceMinutes = ReadInt(reader, "GraceMinutes", 10),
        ReminderIntervalMinutes = ReadInt(reader, "ReminderIntervalMinutes", 60),
        MaxReminders = ReadInt(reader, "MaxReminders", 5),
        MessageSource = Enum.TryParse<ZaloOverbookMessageSource>(ReadString(reader, "MessageSource"), true, out var source)
            ? source
            : ZaloOverbookMessageSource.AdminPool,
        FriendlyMessages = Deserialize(ReadString(reader, "FriendlyMessagesJson")),
        SeriousMessages = Deserialize(ReadString(reader, "SeriousMessagesJson")),
        StrictMessages = Deserialize(ReadString(reader, "StrictMessagesJson")),
        FirstObservedVoterIds = Deserialize(ReadString(reader, "FirstObservedVoterIdsJson")),
        LastObservedVoterIds = Deserialize(ReadString(reader, "LastObservedVoterIdsJson")),
        SuggestedTargetVoterIds = Deserialize(ReadString(reader, "SuggestedTargetVoterIdsJson")),
        CurrentTargetVoterIds = Deserialize(ReadString(reader, "CurrentTargetVoterIdsJson")),
        ConfirmedTargetVoterIds = Deserialize(ReadString(reader, "ConfirmedTargetVoterIdsJson")),
        NeedsConfirmation = ReadInt(reader, "NeedsConfirmation") != 0,
        OrderConfidence = ReadString(reader, "OrderConfidence") ?? "Unknown",
        CurrentPollId = ReadString(reader, "CurrentPollId"),
        CurrentSelectedOptionIds = Deserialize(ReadString(reader, "CurrentSelectedOptionIdsJson")),
        LastPollUpdatedAtUnixMs = ReadLong(reader, "LastPollUpdatedAtUnixMs"),
        EffectiveSlotCount = ReadInt(reader, "EffectiveSlotCount"),
        RawVoterCount = ReadInt(reader, "RawVoterCount"),
        ExcessSlotCount = ReadInt(reader, "ExcessSlotCount"),
        ReminderCount = ReadInt(reader, "ReminderCount"),
        LastReminderAt = ReadDate(reader, "LastReminderAt"),
        NextReminderAt = ReadDate(reader, "NextReminderAt"),
        IncidentKey = ReadString(reader, "IncidentKey"),
        UsedMessageKeys = Deserialize(ReadString(reader, "UsedMessageKeysJson")),
        LastMessageKey = ReadString(reader, "LastMessageKey"),
        LastActorId = ReadString(reader, "LastActorId"),
        LastObservedAt = ReadDate(reader, "LastObservedAt"),
        LastError = ReadString(reader, "LastError"),
        UpdatedAt = ReadDate(reader, "UpdatedAt") ?? DateTimeOffset.UtcNow
    };

    private static string Serialize(IReadOnlyList<string> values) => JsonSerializer.Serialize(values, JsonOptions);

    private static List<string> Deserialize(string? json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json ?? "[]", JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? ReadString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int ReadInt(DbDataReader reader, string name, int fallback = 0)
    {
        var value = ReadString(reader, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static long ReadLong(DbDataReader reader, string name)
    {
        var value = ReadString(reader, name);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static DateTimeOffset? ReadDate(DbDataReader reader, string name)
    {
        var value = ReadString(reader, name);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static string? FormatDate(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
