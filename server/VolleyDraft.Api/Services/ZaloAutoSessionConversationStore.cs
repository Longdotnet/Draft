using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloAutoSessionConversationState
{
    PreviewSent,
    Discussing,
    Clarifying,
    ReadyToConfirm,
    Executing,
    Created,
    Cancelled,
    Expired,
    Superseded,
    Failed
}

internal sealed record ZaloAutoSessionConversationDraftItem(
    string OptionId,
    string OptionContent,
    string DayKey,
    DateTimeOffset StartTime,
    int VoteCount,
    bool Selected = true);

internal sealed record ZaloAutoSessionConversationDraft(
    IReadOnlyList<ZaloAutoSessionConversationDraftItem> Items,
    string? Location,
    int TeamSize);

internal sealed record ZaloAutoSessionConversationProposalKey(string TrackedGroupId, string PollId);

internal sealed class ZaloAutoSessionConversationData
{
    public string Id { get; set; } = string.Empty;
    public string ProposalId { get; set; } = string.Empty;
    public string TrackedGroupId { get; set; } = string.Empty;
    public string PollId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string OriginalOrganizerId { get; set; } = string.Empty;
    public string ActiveOrganizerId { get; set; } = string.Empty;
    public ZaloAutoSessionConversationState State { get; set; } = ZaloAutoSessionConversationState.PreviewSent;
    public string InitialDraftJson { get; set; } = "{}";
    public string DraftJson { get; set; } = "{}";
    public string PreviewMessageId { get; set; } = string.Empty;
    public string CurrentBotMessageId { get; set; } = string.Empty;
    public string? LastQuestionType { get; set; }
    public string? LastIntent { get; set; }
    public int Version { get; set; }
    public int ReminderCount { get; set; }
    public DateTimeOffset? LastOrganizerMessageAt { get; set; }
    public DateTimeOffset? LastBotMessageAt { get; set; }
    public DateTimeOffset? NextFollowUpAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ZaloAutoSessionConversationStore(VolleyDraftDbContext db)
{
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionConversations" (
                "Id" TEXT PRIMARY KEY,
                "ProposalId" TEXT NOT NULL UNIQUE,
                "TrackedGroupId" TEXT NOT NULL,
                "PollId" TEXT NOT NULL,
                "GroupId" TEXT NOT NULL,
                "OriginalOrganizerId" TEXT NOT NULL,
                "ActiveOrganizerId" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "InitialDraftJson" TEXT NOT NULL,
                "DraftJson" TEXT NOT NULL,
                "PreviewMessageId" TEXT NOT NULL,
                "CurrentBotMessageId" TEXT NOT NULL,
                "LastQuestionType" TEXT NULL,
                "LastIntent" TEXT NULL,
                "Version" INTEGER NOT NULL DEFAULT 0,
                "ReminderCount" INTEGER NOT NULL DEFAULT 0,
                "LastOrganizerMessageAt" TEXT NULL,
                "LastBotMessageAt" TEXT NULL,
                "NextFollowUpAt" TEXT NULL,
                "ExpiresAt" TEXT NOT NULL,
                "LastError" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionConversationTurns" (
                "Id" TEXT PRIMARY KEY,
                "ConversationId" TEXT NOT NULL,
                "MessageId" TEXT NOT NULL,
                "Direction" TEXT NOT NULL,
                "SenderId" TEXT NOT NULL,
                "SenderName" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "Intent" TEXT NULL,
                "Interpreter" TEXT NULL,
                "Confidence" REAL NULL,
                "CreatedAt" TEXT NOT NULL,
                UNIQUE ("ConversationId", "MessageId")
            );

            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionConversations_GroupState"
                ON "ZaloAutoSessionConversations" ("GroupId", "State", "UpdatedAt");
            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionConversations_FollowUp"
                ON "ZaloAutoSessionConversations" ("NextFollowUpAt", "ExpiresAt");
            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionConversationTurns_Message"
                ON "ZaloAutoSessionConversationTurns" ("MessageId", "Direction");
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        ensured = true;
    }

    public async Task<ZaloAutoSessionConversationData> CreateFromPreviewAsync(
        ZaloPollSessionProposalData proposal,
        ZaloTrackedGroupData tracked,
        IReadOnlyList<ZaloAutoSessionCandidate> candidates,
        string previewMessageId,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var baseTime = proposal.UpdatedAt == default ? now : proposal.UpdatedAt;
        var firstReminderMinutes = Math.Clamp(
            configuration.GetValue("AutoSession:ConversationFirstReminderMinutes", 30),
            5,
            360);
        var expiryHours = Math.Clamp(
            configuration.GetValue("AutoSession:ConversationExpiryHours", 24),
            3,
            72);
        var draft = new ZaloAutoSessionConversationDraft(
            candidates.Select(item => new ZaloAutoSessionConversationDraftItem(
                item.OptionId,
                item.OptionContent,
                item.DayKey,
                item.StartTime,
                item.VoteCount,
                true)).ToList(),
            tracked.DefaultLocation,
            Math.Max(2, tracked.DefaultTeamSize));
        var json = System.Text.Json.JsonSerializer.Serialize(
            draft,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        var conversation = await CreateIfMissingAsync(
            new ZaloAutoSessionConversationData
            {
                ProposalId = proposal.Id,
                TrackedGroupId = tracked.Id,
                PollId = proposal.PollId,
                GroupId = tracked.GroupId,
                OriginalOrganizerId = NormalizeId(proposal.PollCreatorId),
                ActiveOrganizerId = NormalizeId(proposal.PollCreatorId),
                State = ZaloAutoSessionConversationState.PreviewSent,
                InitialDraftJson = json,
                DraftJson = json,
                PreviewMessageId = previewMessageId.Trim(),
                CurrentBotMessageId = previewMessageId.Trim(),
                Version = 0,
                ReminderCount = 0,
                LastBotMessageAt = baseTime,
                NextFollowUpAt = baseTime.AddMinutes(firstReminderMinutes),
                ExpiresAt = baseTime.AddHours(expiryHours),
                CreatedAt = baseTime,
                UpdatedAt = now
            },
            cancellationToken);

        await AddTurnAsync(
            conversation.Id,
            previewMessageId.Trim(),
            "Bot",
            "bot",
            "Auto Session",
            "organizer_preview",
            "Preview",
            "system",
            1,
            cancellationToken);
        return conversation;
    }

    public async Task<ZaloAutoSessionConversationData> CreateIfMissingAsync(
        ZaloAutoSessionConversationData conversation,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        conversation.Id = string.IsNullOrWhiteSpace(conversation.Id) ? Guid.NewGuid().ToString("n") : conversation.Id;
        conversation.CreatedAt = conversation.CreatedAt == default ? now : conversation.CreatedAt;
        conversation.UpdatedAt = now;

        const string sql = """
            INSERT INTO "ZaloAutoSessionConversations" (
                "Id", "ProposalId", "TrackedGroupId", "PollId", "GroupId", "OriginalOrganizerId", "ActiveOrganizerId",
                "State", "InitialDraftJson", "DraftJson", "PreviewMessageId", "CurrentBotMessageId", "LastQuestionType",
                "LastIntent", "Version", "ReminderCount", "LastOrganizerMessageAt", "LastBotMessageAt", "NextFollowUpAt",
                "ExpiresAt", "LastError", "CreatedAt", "UpdatedAt")
            VALUES (
                @Id, @ProposalId, @TrackedGroupId, @PollId, @GroupId, @OriginalOrganizerId, @ActiveOrganizerId,
                @State, @InitialDraftJson, @DraftJson, @PreviewMessageId, @CurrentBotMessageId, @LastQuestionType,
                @LastIntent, @Version, @ReminderCount, @LastOrganizerMessageAt, @LastBotMessageAt, @NextFollowUpAt,
                @ExpiresAt, @LastError, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("ProposalId") DO NOTHING;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        BindConversation(command, conversation);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetByProposalAsync(conversation.ProposalId, cancellationToken)
               ?? throw new InvalidOperationException("Auto Session conversation was not persisted.");
    }

    public async Task<ZaloAutoSessionConversationData?> GetByProposalAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloAutoSessionConversations\" WHERE \"ProposalId\" = @ProposalId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@ProposalId", proposalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    public async Task<ZaloAutoSessionConversationData?> GetByIdAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloAutoSessionConversations\" WHERE \"Id\" = @Id LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@Id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    public async Task<ZaloAutoSessionConversationData?> FindByQuotedBotMessageAsync(
        string groupId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            SELECT c.*
            FROM "ZaloAutoSessionConversations" c
            JOIN "ZaloAutoSessionConversationTurns" t ON t."ConversationId" = c."Id"
            WHERE c."GroupId" = @GroupId
              AND t."MessageId" = @MessageId
              AND t."Direction" = 'Bot'
              AND c."State" IN ('PreviewSent','Discussing','Clarifying','ReadyToConfirm')
            ORDER BY c."UpdatedAt" DESC
            LIMIT 1;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@GroupId", groupId);
        AddParameter(command, "@MessageId", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    public async Task<IReadOnlyList<ZaloAutoSessionConversationData>> GetActiveForGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloAutoSessionConversations\" WHERE \"GroupId\" = @GroupId AND \"State\" IN ('PreviewSent','Discussing','Clarifying','ReadyToConfirm') ORDER BY \"UpdatedAt\" DESC;",
            cancellationToken);
        AddParameter(command, "@GroupId", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloAutoSessionConversationData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadConversation(reader));
        return result;
    }

    public async Task<IReadOnlyList<ZaloAutoSessionConversationProposalKey>> GetConversationEligibleProposalKeysAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT \"TrackedGroupId\", \"PollId\" FROM \"ZaloPollSessionProposals\" " +
            "WHERE \"ProposalMessageId\" IS NOT NULL AND (\"Status\" = 'AwaitingApproval' OR " +
            "(\"Status\" = 'Ignored' AND \"ClassifierReason\" LIKE 'preview_only:%')) " +
            "ORDER BY \"UpdatedAt\" DESC LIMIT 100;",
            cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloAutoSessionConversationProposalKey>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ZaloAutoSessionConversationProposalKey(
                ReadString(reader, "TrackedGroupId") ?? string.Empty,
                ReadString(reader, "PollId") ?? string.Empty));
        }
        return result;
    }

    public async Task<IReadOnlyList<ZaloAutoSessionConversationData>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloAutoSessionConversations\" WHERE \"State\" IN " +
            "('PreviewSent','Discussing','Clarifying','ReadyToConfirm') ORDER BY \"UpdatedAt\" DESC LIMIT 100;",
            cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloAutoSessionConversationData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadConversation(reader));
        return result;
    }

    public async Task<IReadOnlyList<ZaloAutoSessionConversationData>> GetDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT * FROM \"ZaloAutoSessionConversations\" WHERE \"State\" IN ('PreviewSent','Discussing','Clarifying','ReadyToConfirm') AND (\"ExpiresAt\" <= @Now OR (\"NextFollowUpAt\" IS NOT NULL AND \"NextFollowUpAt\" <= @Now)) ORDER BY \"UpdatedAt\" LIMIT 100;",
            cancellationToken);
        AddParameter(command, "@Now", FormatDate(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloAutoSessionConversationData>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadConversation(reader));
        return result;
    }

    public async Task<ZaloAutoSessionConversationData> SaveAsync(
        ZaloAutoSessionConversationData conversation,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        const string sql = """
            UPDATE "ZaloAutoSessionConversations"
            SET "ActiveOrganizerId" = @ActiveOrganizerId,
                "State" = @State,
                "DraftJson" = @DraftJson,
                "CurrentBotMessageId" = @CurrentBotMessageId,
                "LastQuestionType" = @LastQuestionType,
                "LastIntent" = @LastIntent,
                "Version" = @Version,
                "ReminderCount" = @ReminderCount,
                "LastOrganizerMessageAt" = @LastOrganizerMessageAt,
                "LastBotMessageAt" = @LastBotMessageAt,
                "NextFollowUpAt" = @NextFollowUpAt,
                "ExpiresAt" = @ExpiresAt,
                "LastError" = @LastError,
                "UpdatedAt" = @UpdatedAt
            WHERE "Id" = @Id;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        BindConversation(command, conversation);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return conversation;
    }

    public async Task<bool> TryClaimExecutionAsync(
        string conversationId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "UPDATE \"ZaloAutoSessionConversations\" SET \"State\" = 'Executing', \"Version\" = \"Version\" + 1, \"UpdatedAt\" = @Now WHERE \"Id\" = @Id AND \"Version\" = @ExpectedVersion AND \"State\" = 'ReadyToConfirm';",
            cancellationToken);
        AddParameter(command, "@Now", FormatDate(DateTimeOffset.UtcNow));
        AddParameter(command, "@Id", conversationId);
        AddParameter(command, "@ExpectedVersion", expectedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> HasTurnAsync(
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT 1 FROM \"ZaloAutoSessionConversationTurns\" WHERE \"ConversationId\" = @ConversationId AND \"MessageId\" = @MessageId LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@ConversationId", conversationId);
        AddParameter(command, "@MessageId", messageId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task AddTurnAsync(
        string conversationId,
        string messageId,
        string direction,
        string senderId,
        string senderName,
        string content,
        string? intent,
        string? interpreter,
        double? confidence,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        const string sql = """
            INSERT INTO "ZaloAutoSessionConversationTurns" (
                "Id", "ConversationId", "MessageId", "Direction", "SenderId", "SenderName", "Content",
                "Intent", "Interpreter", "Confidence", "CreatedAt")
            VALUES (@Id, @ConversationId, @MessageId, @Direction, @SenderId, @SenderName, @Content,
                @Intent, @Interpreter, @Confidence, @CreatedAt)
            ON CONFLICT ("ConversationId", "MessageId") DO NOTHING;
            """;
        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "@Id", Guid.NewGuid().ToString("n"));
        AddParameter(command, "@ConversationId", conversationId);
        AddParameter(command, "@MessageId", messageId);
        AddParameter(command, "@Direction", direction);
        AddParameter(command, "@SenderId", senderId);
        AddParameter(command, "@SenderName", senderName);
        AddParameter(command, "@Content", content);
        AddParameter(command, "@Intent", intent);
        AddParameter(command, "@Interpreter", interpreter);
        AddParameter(command, "@Confidence", confidence);
        AddParameter(command, "@CreatedAt", FormatDate(DateTimeOffset.UtcNow));
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

    private static void BindConversation(DbCommand command, ZaloAutoSessionConversationData value)
    {
        AddParameter(command, "@Id", value.Id);
        AddParameter(command, "@ProposalId", value.ProposalId);
        AddParameter(command, "@TrackedGroupId", value.TrackedGroupId);
        AddParameter(command, "@PollId", value.PollId);
        AddParameter(command, "@GroupId", value.GroupId);
        AddParameter(command, "@OriginalOrganizerId", value.OriginalOrganizerId);
        AddParameter(command, "@ActiveOrganizerId", value.ActiveOrganizerId);
        AddParameter(command, "@State", value.State.ToString());
        AddParameter(command, "@InitialDraftJson", value.InitialDraftJson);
        AddParameter(command, "@DraftJson", value.DraftJson);
        AddParameter(command, "@PreviewMessageId", value.PreviewMessageId);
        AddParameter(command, "@CurrentBotMessageId", value.CurrentBotMessageId);
        AddParameter(command, "@LastQuestionType", value.LastQuestionType);
        AddParameter(command, "@LastIntent", value.LastIntent);
        AddParameter(command, "@Version", value.Version);
        AddParameter(command, "@ReminderCount", value.ReminderCount);
        AddParameter(command, "@LastOrganizerMessageAt", FormatDate(value.LastOrganizerMessageAt));
        AddParameter(command, "@LastBotMessageAt", FormatDate(value.LastBotMessageAt));
        AddParameter(command, "@NextFollowUpAt", FormatDate(value.NextFollowUpAt));
        AddParameter(command, "@ExpiresAt", FormatDate(value.ExpiresAt));
        AddParameter(command, "@LastError", value.LastError);
        AddParameter(command, "@CreatedAt", FormatDate(value.CreatedAt));
        AddParameter(command, "@UpdatedAt", FormatDate(value.UpdatedAt));
    }

    private static ZaloAutoSessionConversationData ReadConversation(DbDataReader reader) => new()
    {
        Id = ReadString(reader, "Id") ?? string.Empty,
        ProposalId = ReadString(reader, "ProposalId") ?? string.Empty,
        TrackedGroupId = ReadString(reader, "TrackedGroupId") ?? string.Empty,
        PollId = ReadString(reader, "PollId") ?? string.Empty,
        GroupId = ReadString(reader, "GroupId") ?? string.Empty,
        OriginalOrganizerId = ReadString(reader, "OriginalOrganizerId") ?? string.Empty,
        ActiveOrganizerId = ReadString(reader, "ActiveOrganizerId") ?? string.Empty,
        State = Enum.TryParse<ZaloAutoSessionConversationState>(ReadString(reader, "State"), true, out var state)
            ? state
            : ZaloAutoSessionConversationState.PreviewSent,
        InitialDraftJson = ReadString(reader, "InitialDraftJson") ?? "{}",
        DraftJson = ReadString(reader, "DraftJson") ?? "{}",
        PreviewMessageId = ReadString(reader, "PreviewMessageId") ?? string.Empty,
        CurrentBotMessageId = ReadString(reader, "CurrentBotMessageId") ?? string.Empty,
        LastQuestionType = ReadString(reader, "LastQuestionType"),
        LastIntent = ReadString(reader, "LastIntent"),
        Version = ReadInt(reader, "Version"),
        ReminderCount = ReadInt(reader, "ReminderCount"),
        LastOrganizerMessageAt = ReadDate(reader, "LastOrganizerMessageAt"),
        LastBotMessageAt = ReadDate(reader, "LastBotMessageAt"),
        NextFollowUpAt = ReadDate(reader, "NextFollowUpAt"),
        ExpiresAt = ReadDate(reader, "ExpiresAt") ?? DateTimeOffset.MinValue,
        LastError = ReadString(reader, "LastError"),
        CreatedAt = ReadDate(reader, "CreatedAt") ?? DateTimeOffset.MinValue,
        UpdatedAt = ReadDate(reader, "UpdatedAt") ?? DateTimeOffset.MinValue
    };

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
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int ReadInt(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadDate(DbDataReader reader, string name)
    {
        var raw = ReadString(reader, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatDate(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);
}
