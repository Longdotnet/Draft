using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloPollSessionProposalStatus
{
    Ignored,
    AwaitingApproval,
    Approved,
    Rejected,
    Created,
    Superseded,
    Failed
}

internal sealed class ZaloTrackedGroupData
{
    public string Id { get; set; } = string.Empty;
    public string AdminUserId { get; set; } = string.Empty;
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public bool AutoSessionEnabled { get; set; } = true;
    public bool RequireOrganizerApproval { get; set; } = true;
    public int DefaultTeamCount { get; set; } = 3;
    public int DefaultTeamSize { get; set; } = 6;
    public int DefaultTotalSets { get; set; } = 4;
    public int DefaultStartMinutes { get; set; } = 17 * 60 + 30;
    public bool AssumePmForHourUnder12 { get; set; } = true;
    public string? DefaultLocation { get; set; }
    public bool BotEnabledForCreatedSessions { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class ZaloPollSessionProposalData
{
    public string Id { get; set; } = string.Empty;
    public string TrackedGroupId { get; set; } = string.Empty;
    public string PollId { get; set; } = string.Empty;
    public string PollQuestion { get; set; } = string.Empty;
    public string PollCreatorId { get; set; } = string.Empty;
    public long PollUpdatedAtUnixMs { get; set; }
    public string PollStructureHash { get; set; } = string.Empty;
    public string CandidatesJson { get; set; } = "[]";
    public double ClassifierConfidence { get; set; }
    public string ClassifierReason { get; set; } = string.Empty;
    public ZaloPollSessionProposalStatus Status { get; set; }
    public string? ProposalMessageId { get; set; }
    public string? ApprovedByZaloUserId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed record ZaloAutoSessionLinkData(
    string Id,
    string TrackedGroupId,
    string PollId,
    string OptionId,
    string SessionId,
    DateTimeOffset CreatedAt);

internal sealed class ZaloAutoSessionStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloTrackedGroups" (
                "Id" TEXT PRIMARY KEY,
                "AdminUserId" TEXT NOT NULL,
                "ZaloConnectionId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "GroupName" TEXT NOT NULL,
                "AutoSessionEnabled" INTEGER NOT NULL DEFAULT 1,
                "RequireOrganizerApproval" INTEGER NOT NULL DEFAULT 1,
                "DefaultTeamCount" INTEGER NOT NULL DEFAULT 3,
                "DefaultTeamSize" INTEGER NOT NULL DEFAULT 6,
                "DefaultTotalSets" INTEGER NOT NULL DEFAULT 4,
                "DefaultStartMinutes" INTEGER NOT NULL DEFAULT 1050,
                "AssumePmForHourUnder12" INTEGER NOT NULL DEFAULT 1,
                "DefaultLocation" TEXT NULL,
                "BotEnabledForCreatedSessions" INTEGER NOT NULL DEFAULT 1,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                UNIQUE ("ZaloConnectionId", "GroupId")
            );

            CREATE TABLE IF NOT EXISTS "ZaloPollSessionProposals" (
                "Id" TEXT PRIMARY KEY,
                "TrackedGroupId" TEXT NOT NULL,
                "PollId" TEXT NOT NULL,
                "PollQuestion" TEXT NOT NULL,
                "PollCreatorId" TEXT NOT NULL,
                "PollUpdatedAtUnixMs" BIGINT NOT NULL DEFAULT 0,
                "PollStructureHash" TEXT NOT NULL,
                "CandidatesJson" TEXT NOT NULL DEFAULT '[]',
                "ClassifierConfidence" REAL NOT NULL DEFAULT 0,
                "ClassifierReason" TEXT NOT NULL DEFAULT '',
                "Status" TEXT NOT NULL,
                "ProposalMessageId" TEXT NULL,
                "ApprovedByZaloUserId" TEXT NULL,
                "ApprovedAt" TEXT NULL,
                "LastError" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                UNIQUE ("TrackedGroupId", "PollId")
            );

            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionLinks" (
                "Id" TEXT PRIMARY KEY,
                "TrackedGroupId" TEXT NOT NULL,
                "PollId" TEXT NOT NULL,
                "OptionId" TEXT NOT NULL,
                "SessionId" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                UNIQUE ("TrackedGroupId", "PollId", "OptionId")
            );

            CREATE INDEX IF NOT EXISTS "IX_ZaloTrackedGroups_Active"
                ON "ZaloTrackedGroups" ("ZaloConnectionId", "AutoSessionEnabled");
            CREATE INDEX IF NOT EXISTS "IX_ZaloPollSessionProposals_Status"
                ON "ZaloPollSessionProposals" ("Status", "UpdatedAt");
            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionLinks_SessionId"
                ON "ZaloAutoSessionLinks" ("SessionId");
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<int> SeedFromExistingSessionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var linked = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.ZaloConnectionId != null && session.ZaloGroupId != null)
            .Select(session => new
            {
                session.AdminUserId,
                ZaloConnectionId = session.ZaloConnectionId!,
                GroupId = session.ZaloGroupId!,
                session.ZaloGroupName,
                session.Location,
                session.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        var seeded = 0;
        foreach (var group in linked.GroupBy(item => new { item.ZaloConnectionId, item.GroupId }))
        {
            var latest = group.OrderByDescending(item => item.UpdatedAt).First();
            var now = DateTimeOffset.UtcNow;
            const string sql = """
                INSERT INTO "ZaloTrackedGroups" (
                    "Id", "AdminUserId", "ZaloConnectionId", "GroupId", "GroupName",
                    "AutoSessionEnabled", "RequireOrganizerApproval", "DefaultTeamCount", "DefaultTeamSize",
                    "DefaultTotalSets", "DefaultStartMinutes", "AssumePmForHourUnder12", "DefaultLocation",
                    "BotEnabledForCreatedSessions", "CreatedAt", "UpdatedAt")
                VALUES (
                    @Id, @AdminUserId, @ZaloConnectionId, @GroupId, @GroupName,
                    1, 1, 3, 6, 4, 1050, 1, @DefaultLocation, 1, @CreatedAt, @UpdatedAt)
                ON CONFLICT ("ZaloConnectionId", "GroupId") DO UPDATE SET
                    "AdminUserId" = excluded."AdminUserId",
                    "GroupName" = excluded."GroupName",
                    "DefaultLocation" = COALESCE("ZaloTrackedGroups"."DefaultLocation", excluded."DefaultLocation"),
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            await using var command = await CreateCommandAsync(sql, cancellationToken);
            AddParameter(command, "@Id", Guid.NewGuid().ToString("n"));
            AddParameter(command, "@AdminUserId", latest.AdminUserId);
            AddParameter(command, "@ZaloConnectionId", latest.ZaloConnectionId);
            AddParameter(command, "@GroupId", latest.GroupId);
            AddParameter(command, "@GroupName", string.IsNullOrWhiteSpace(latest.ZaloGroupName) ? latest.GroupId : latest.ZaloGroupName);
            AddParameter(command, "@DefaultLocation", latest.Location);
            AddParameter(command, "@CreatedAt", FormatDate(now));
            AddParameter(command, "@UpdatedAt", FormatDate(now));
            seeded += await command.ExecuteNonQueryAsync(cancellationToken) > 0 ? 1 : 0;
        }

        await SeedLinksFromExistingPollImportsAsync(cancellationToken);
        return seeded;
    }

    private async Task SeedLinksFromExistingPollImportsAsync(CancellationToken cancellationToken)
    {
        var imports = await db.PollImports
            .AsNoTracking()
            .Where(import => import.Session.ZaloConnectionId != null && import.Session.ZaloGroupId != null)
            .Select(import => new
            {
                import.SessionId,
                import.PollId,
                import.SelectedOptionIdsJson,
                import.ImportedAt,
                ZaloConnectionId = import.Session.ZaloConnectionId!,
                GroupId = import.Session.ZaloGroupId!
            })
            .ToListAsync(cancellationToken);

        foreach (var import in imports.OrderByDescending(item => item.ImportedAt))
        {
            var trackedGroupId = await GetTrackedGroupIdAsync(
                import.ZaloConnectionId,
                import.GroupId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(trackedGroupId)) continue;
            foreach (var optionId in ParseStringList(import.SelectedOptionIdsJson))
            {
                await AddLinkAsync(new ZaloAutoSessionLinkData(
                    Guid.NewGuid().ToString("n"),
                    trackedGroupId,
                    import.PollId,
                    optionId,
                    import.SessionId,
                    import.ImportedAt),
                    cancellationToken);
            }
        }
    }

    private async Task<string?> GetTrackedGroupIdAsync(
        string connectionId,
        string groupId,
        CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync(
            "SELECT \"Id\" FROM \"ZaloTrackedGroups\" WHERE \"ZaloConnectionId\" = @ConnectionId AND \"GroupId\" = @GroupId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@ConnectionId", connectionId);
        AddParameter(command, "@GroupId", groupId);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<string>> GetActiveGroupIdsAsync(
        IReadOnlyList<string> connectionIds,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connectionId in connectionIds.Distinct(StringComparer.Ordinal))
        {
            await using var command = await CreateCommandAsync(
                "SELECT \"GroupId\" FROM \"ZaloTrackedGroups\" WHERE \"ZaloConnectionId\" = @ConnectionId AND \"AutoSessionEnabled\" = 1;",
                cancellationToken);
            AddParameter(command, "@ConnectionId", connectionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var groupId = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(groupId)) result.Add(groupId);
            }
        }
        return result.ToList();
    }

    public async Task<IReadOnlyList<ZaloTrackedGroupData>> GetActiveTrackedGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloTrackedGroups\" WHERE \"AutoSessionEnabled\" = 1 ORDER BY \"UpdatedAt\" DESC;",
            cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloTrackedGroupData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadTrackedGroup(reader));
        return result;
    }

    public async Task<IReadOnlyList<ZaloTrackedGroupData>> GetActiveTrackedGroupsForAccountAsync(
        string accountId,
        string? groupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var connectionIds = await db.ZaloConnections
            .AsNoTracking()
            .Where(connection => connection.AccountZaloId == accountId)
            .Select(connection => connection.Id)
            .ToListAsync(cancellationToken);
        if (connectionIds.Count == 0) return [];

        var result = new List<ZaloTrackedGroupData>();
        foreach (var connectionId in connectionIds)
        {
            var sql = string.IsNullOrWhiteSpace(groupId)
                ? "SELECT * FROM \"ZaloTrackedGroups\" WHERE \"ZaloConnectionId\" = @ConnectionId AND \"AutoSessionEnabled\" = 1;"
                : "SELECT * FROM \"ZaloTrackedGroups\" WHERE \"ZaloConnectionId\" = @ConnectionId AND \"GroupId\" = @GroupId AND \"AutoSessionEnabled\" = 1;";
            await using var command = await CreateCommandAsync(sql, cancellationToken);
            AddParameter(command, "@ConnectionId", connectionId);
            if (!string.IsNullOrWhiteSpace(groupId)) AddParameter(command, "@GroupId", groupId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadTrackedGroup(reader));
        }
        return result;
    }

    public async Task<ZaloTrackedGroupData?> GetTrackedGroupAsync(
        string trackedGroupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloTrackedGroups\" WHERE \"Id\" = @Id LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@Id", trackedGroupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTrackedGroup(reader) : null;
    }

    public async Task<ZaloPollSessionProposalData?> GetProposalAsync(
        string trackedGroupId,
        string pollId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloPollSessionProposals\" WHERE \"TrackedGroupId\" = @TrackedGroupId AND \"PollId\" = @PollId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        AddParameter(command, "@PollId", pollId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProposal(reader) : null;
    }

    public async Task<IReadOnlyList<ZaloPollSessionProposalData>> GetPendingProposalsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloPollSessionProposals\" WHERE \"Status\" = 'AwaitingApproval' AND \"ProposalMessageId\" IS NOT NULL ORDER BY \"UpdatedAt\" DESC LIMIT 100;",
            cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloPollSessionProposalData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadProposal(reader));
        return result;
    }

    public async Task<ZaloPollSessionProposalData> UpsertProposalAsync(
        ZaloPollSessionProposalData proposal,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        proposal.Id = string.IsNullOrWhiteSpace(proposal.Id) ? Guid.NewGuid().ToString("n") : proposal.Id;
        proposal.CreatedAt = proposal.CreatedAt == default ? DateTimeOffset.UtcNow : proposal.CreatedAt;
        proposal.UpdatedAt = DateTimeOffset.UtcNow;
        const string sql = """
            INSERT INTO "ZaloPollSessionProposals" (
                "Id", "TrackedGroupId", "PollId", "PollQuestion", "PollCreatorId", "PollUpdatedAtUnixMs",
                "PollStructureHash", "CandidatesJson", "ClassifierConfidence", "ClassifierReason", "Status",
                "ProposalMessageId", "ApprovedByZaloUserId", "ApprovedAt", "LastError", "CreatedAt", "UpdatedAt")
            VALUES (
                @Id, @TrackedGroupId, @PollId, @PollQuestion, @PollCreatorId, @PollUpdatedAtUnixMs,
                @PollStructureHash, @CandidatesJson, @ClassifierConfidence, @ClassifierReason, @Status,
                @ProposalMessageId, @ApprovedByZaloUserId, @ApprovedAt, @LastError, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("TrackedGroupId", "PollId") DO UPDATE SET
                "PollQuestion" = excluded."PollQuestion",
                "PollCreatorId" = excluded."PollCreatorId",
                "PollUpdatedAtUnixMs" = excluded."PollUpdatedAtUnixMs",
                "PollStructureHash" = excluded."PollStructureHash",
                "CandidatesJson" = excluded."CandidatesJson",
                "ClassifierConfidence" = excluded."ClassifierConfidence",
                "ClassifierReason" = excluded."ClassifierReason",
                "Status" = excluded."Status",
                "ProposalMessageId" = excluded."ProposalMessageId",
                "ApprovedByZaloUserId" = excluded."ApprovedByZaloUserId",
                "ApprovedAt" = excluded."ApprovedAt",
                "LastError" = excluded."LastError",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@Id", proposal.Id);
        AddParameter(command, "@TrackedGroupId", proposal.TrackedGroupId);
        AddParameter(command, "@PollId", proposal.PollId);
        AddParameter(command, "@PollQuestion", proposal.PollQuestion);
        AddParameter(command, "@PollCreatorId", proposal.PollCreatorId);
        AddParameter(command, "@PollUpdatedAtUnixMs", proposal.PollUpdatedAtUnixMs);
        AddParameter(command, "@PollStructureHash", proposal.PollStructureHash);
        AddParameter(command, "@CandidatesJson", proposal.CandidatesJson);
        AddParameter(command, "@ClassifierConfidence", proposal.ClassifierConfidence);
        AddParameter(command, "@ClassifierReason", proposal.ClassifierReason);
        AddParameter(command, "@Status", proposal.Status.ToString());
        AddParameter(command, "@ProposalMessageId", proposal.ProposalMessageId);
        AddParameter(command, "@ApprovedByZaloUserId", proposal.ApprovedByZaloUserId);
        AddParameter(command, "@ApprovedAt", FormatDate(proposal.ApprovedAt));
        AddParameter(command, "@LastError", proposal.LastError);
        AddParameter(command, "@CreatedAt", FormatDate(proposal.CreatedAt));
        AddParameter(command, "@UpdatedAt", FormatDate(proposal.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetProposalAsync(proposal.TrackedGroupId, proposal.PollId, cancellationToken) ?? proposal;
    }

    public async Task<ZaloAutoSessionLinkData?> GetLinkAsync(
        string trackedGroupId,
        string pollId,
        string optionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloAutoSessionLinks\" WHERE \"TrackedGroupId\" = @TrackedGroupId AND \"PollId\" = @PollId AND \"OptionId\" = @OptionId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@TrackedGroupId", trackedGroupId);
        AddParameter(command, "@PollId", pollId);
        AddParameter(command, "@OptionId", optionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ZaloAutoSessionLinkData(
            ReadString(reader, "Id") ?? string.Empty,
            ReadString(reader, "TrackedGroupId") ?? string.Empty,
            ReadString(reader, "PollId") ?? string.Empty,
            ReadString(reader, "OptionId") ?? string.Empty,
            ReadString(reader, "SessionId") ?? string.Empty,
            ReadDate(reader, "CreatedAt") ?? DateTimeOffset.MinValue);
    }

    public async Task AddLinkAsync(
        ZaloAutoSessionLinkData link,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloAutoSessionLinks" (
                "Id", "TrackedGroupId", "PollId", "OptionId", "SessionId", "CreatedAt")
            VALUES (@Id, @TrackedGroupId, @PollId, @OptionId, @SessionId, @CreatedAt)
            ON CONFLICT ("TrackedGroupId", "PollId", "OptionId") DO NOTHING;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@Id", link.Id);
        AddParameter(command, "@TrackedGroupId", link.TrackedGroupId);
        AddParameter(command, "@PollId", link.PollId);
        AddParameter(command, "@OptionId", link.OptionId);
        AddParameter(command, "@SessionId", link.SessionId);
        AddParameter(command, "@CreatedAt", FormatDate(link.CreatedAt));
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

    private static ZaloPollSessionProposalData ReadProposal(DbDataReader reader) => new()
    {
        Id = ReadString(reader, "Id") ?? string.Empty,
        TrackedGroupId = ReadString(reader, "TrackedGroupId") ?? string.Empty,
        PollId = ReadString(reader, "PollId") ?? string.Empty,
        PollQuestion = ReadString(reader, "PollQuestion") ?? string.Empty,
        PollCreatorId = ReadString(reader, "PollCreatorId") ?? string.Empty,
        PollUpdatedAtUnixMs = ReadLong(reader, "PollUpdatedAtUnixMs"),
        PollStructureHash = ReadString(reader, "PollStructureHash") ?? string.Empty,
        CandidatesJson = ReadString(reader, "CandidatesJson") ?? "[]",
        ClassifierConfidence = ReadDouble(reader, "ClassifierConfidence"),
        ClassifierReason = ReadString(reader, "ClassifierReason") ?? string.Empty,
        Status = Enum.TryParse<ZaloPollSessionProposalStatus>(ReadString(reader, "Status"), true, out var status)
            ? status
            : ZaloPollSessionProposalStatus.Failed,
        ProposalMessageId = ReadString(reader, "ProposalMessageId"),
        ApprovedByZaloUserId = ReadString(reader, "ApprovedByZaloUserId"),
        ApprovedAt = ReadDate(reader, "ApprovedAt"),
        LastError = ReadString(reader, "LastError"),
        CreatedAt = ReadDate(reader, "CreatedAt") ?? DateTimeOffset.MinValue,
        UpdatedAt = ReadDate(reader, "UpdatedAt") ?? DateTimeOffset.MinValue
    };

    private static IReadOnlyList<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int ReadInt(DbDataReader reader, string name, int fallback = 0)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal)) return fallback;
        return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static long ReadLong(DbDataReader reader, string name, long fallback = 0)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal)) return fallback;
        return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(DbDataReader reader, string name, double fallback = 0)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal)) return fallback;
        return Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadDate(DbDataReader reader, string name)
    {
        var raw = ReadString(reader, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private static string? FormatDate(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture);
}
