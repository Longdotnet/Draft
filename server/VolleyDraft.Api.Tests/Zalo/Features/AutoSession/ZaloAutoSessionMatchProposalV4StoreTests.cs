using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionMatchProposalV4StoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task InitializeFromPreview_PersistsPollExplicitCapacityAndApprovedDefaultProvenance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionMatchProposalV4Store(db);
        var proposal = BuildProposal();
        var tracked = BuildTracked();
        var conversation = BuildConversation(DraftJson());

        var result = await store.InitializeFromPreviewAsync(proposal, tracked, conversation);

        Assert.NotNull(result);
        Assert.True(result!.Accepted);
        Assert.Equal(1, result.Revision.Revision);
        Assert.Equal(0, result.Revision.ConversationVersion);
        Assert.Equal(proposal.PollQuestion, result.Revision.SourcePollQuestion);
        Assert.Equal(proposal.PollUpdatedAtUnixMs, result.Revision.SourcePollUpdatedAtUnixMs);
        Assert.Equal(proposal.PollStructureHash, result.Revision.SourcePollStructureHash);

        var evidence = JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            result.Revision.EvidenceJson,
            JsonOptions);
        Assert.NotNull(evidence);
        Assert.Equal(4, evidence!.SchemaVersion);
        Assert.Equal("approved_group_default", evidence.Location.Source);
        Assert.Equal("poll_title_explicit_capacity", evidence.TeamSize.Source);
        Assert.Contains("capacity=18", evidence.TeamSize.Detail, StringComparison.Ordinal);
        Assert.Equal("poll_option", evidence.OptionIdentity["t6"].Source);
        Assert.Equal("poll_title_explicit_time", evidence.StartTimes["t6"].Source);
        Assert.Equal("poll_option_default_selected", evidence.Selections["cn"].Source);
    }

    [Fact]
    public async Task OrganizerCorrection_AppendsRevisionAndPreservesUnchangedProvenance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionMatchProposalV4Store(db);
        var conversation = BuildConversation(DraftJson());
        await store.InitializeFromPreviewAsync(BuildProposal(), BuildTracked(), conversation);

        conversation.Version = 1;
        conversation.ActiveOrganizerId = "leader-1";
        conversation.LastIntent = "ModifyDraft";
        conversation.DraftJson = DraftJson(location: "Sân A", t6Hour: 18);
        var result = await store.SaveConversationDraftAsync(conversation);

        Assert.NotNull(result);
        Assert.True(result!.Accepted);
        Assert.Equal("revision_appended", result.Reason);
        Assert.Equal(2, result.Revision.Revision);
        Assert.Equal(1, result.Revision.ConversationVersion);
        Assert.Equal("leader-1", result.Revision.ActorZaloUserId);

        var evidence = JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            result.Revision.EvidenceJson,
            JsonOptions)!;
        Assert.Equal("organizer_correction", evidence.Location.Source);
        Assert.Equal("leader-1", evidence.Location.ActorZaloUserId);
        Assert.Equal("ModifyDraft", evidence.Location.Intent);
        Assert.Equal("organizer_correction", evidence.StartTimes["t6"].Source);
        Assert.Equal("leader-1", evidence.StartTimes["t6"].ActorZaloUserId);
        Assert.Equal("poll_title_explicit_time", evidence.StartTimes["cn"].Source);
        Assert.Equal("poll_title_explicit_capacity", evidence.TeamSize.Source);
        Assert.Equal("poll_option", evidence.OptionIdentity["t6"].Source);
    }

    [Theory]
    [InlineData("T6 changed", "T6")]
    [InlineData("T6 11/9", "CN")]
    public async Task AuthoritativeOptionIdentityChange_IsRejected(
        string optionContent,
        string dayKey)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionMatchProposalV4Store(db);
        var conversation = BuildConversation(DraftJson());
        await store.InitializeFromPreviewAsync(BuildProposal(), BuildTracked(), conversation);

        var original = DeserializeDraft(DraftJson());
        var changedItems = original.Items.Select(item => item.OptionId == "t6"
            ? item with { OptionContent = optionContent, DayKey = dayKey }
            : item).ToList();
        conversation.Version = 1;
        conversation.DraftJson = SerializeDraft(original with { Items = changedItems });

        var result = await store.SaveConversationDraftAsync(conversation);
        var latest = await store.GetLatestAsync(conversation.ProposalId);

        Assert.NotNull(result);
        Assert.False(result!.Accepted);
        Assert.Equal("authoritative_option_identity_changed", result.Reason);
        Assert.NotNull(latest);
        Assert.Equal(1, latest!.Revision);
    }

    [Fact]
    public async Task EqualConversationVersion_WithDifferentPayload_FailsClosed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionMatchProposalV4Store(db);
        var conversation = BuildConversation(DraftJson());
        await store.InitializeFromPreviewAsync(BuildProposal(), BuildTracked(), conversation);

        conversation.Version = 1;
        conversation.DraftJson = DraftJson(location: "Sân A");
        var first = await store.SaveConversationDraftAsync(conversation);
        Assert.True(first!.Accepted);
        Assert.Equal(2, first.Revision.Revision);

        conversation.DraftJson = DraftJson(location: "Sân B");
        var conflicting = await store.SaveConversationDraftAsync(conversation);

        Assert.NotNull(conflicting);
        Assert.False(conflicting!.Accepted);
        Assert.Equal("stale_or_conflicting_conversation_version", conflicting.Reason);
        Assert.Equal(2, conflicting.Revision.Revision);
    }

    [Fact]
    public async Task RestartRecovery_LoadsLatestDurableRevision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;

        await using (var firstDb = new VolleyDraftDbContext(options))
        {
            var store = new ZaloAutoSessionMatchProposalV4Store(firstDb);
            var conversation = BuildConversation(DraftJson());
            await store.InitializeFromPreviewAsync(BuildProposal(), BuildTracked(), conversation);
            conversation.Version = 3;
            conversation.ActiveOrganizerId = "leader-2";
            conversation.LastIntent = "ModifyDraft";
            conversation.DraftJson = DraftJson(location: "Sân restart", cnSelected: false);
            var saved = await store.SaveConversationDraftAsync(conversation);
            Assert.True(saved!.Accepted);
            Assert.Equal(2, saved.Revision.Revision);
        }

        await using var secondDb = new VolleyDraftDbContext(options);
        var afterRestart = new ZaloAutoSessionMatchProposalV4Store(secondDb);
        var latest = await afterRestart.GetLatestAsync("proposal-1");

        Assert.NotNull(latest);
        Assert.Equal(2, latest!.Revision);
        Assert.Equal(3, latest.ConversationVersion);
        var draft = DeserializeDraft(latest.DraftJson);
        Assert.Equal("Sân restart", draft.Location);
        Assert.False(draft.Items.Single(item => item.OptionId == "cn").Selected);
    }

    [Fact]
    public async Task ConversationStore_GetById_HydratesLatestV4DraftAfterLegacyCacheDrift()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var tracked = await new ZaloAutoSessionSettingsStore(db).InsertIfMissingAsync(BuildTracked());
        var proposal = BuildProposal();
        proposal.TrackedGroupId = tracked.Id;
        proposal = await new ZaloAutoSessionStore(db).UpsertProposalAsync(proposal);
        var conversationStore = new ZaloAutoSessionConversationStore(db);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var candidates = new[]
        {
            new ZaloAutoSessionCandidate("t6", "T6 11/9", "T6", At(11, 17), 10),
            new ZaloAutoSessionCandidate("cn", "CN 13/9", "CN", At(13, 17), 9)
        };
        var conversation = await conversationStore.CreateFromPreviewAsync(
            proposal,
            tracked,
            candidates,
            "preview-1",
            configuration);
        var initialJson = conversation.InitialDraftJson;

        conversation.Version = 1;
        conversation.ActiveOrganizerId = "leader-1";
        conversation.LastIntent = "ModifyDraft";
        conversation.DraftJson = DraftJson(location: "Sân durable", t6Hour: 18);
        await conversationStore.SaveAsync(conversation);

        await using (var tamper = connection.CreateCommand())
        {
            tamper.CommandText = "UPDATE \"ZaloAutoSessionConversations\" SET \"DraftJson\"=@draft WHERE \"Id\"=@id;";
            tamper.Parameters.AddWithValue("@draft", initialJson);
            tamper.Parameters.AddWithValue("@id", conversation.Id);
            await tamper.ExecuteNonQueryAsync();
        }

        var recovered = await conversationStore.GetByIdAsync(conversation.Id);

        Assert.NotNull(recovered);
        var recoveredDraft = DeserializeDraft(recovered!.DraftJson);
        Assert.Equal("Sân durable", recoveredDraft.Location);
        Assert.Equal(18, recoveredDraft.Items.Single(item => item.OptionId == "t6").StartTime.Hour);
        var latest = await new ZaloAutoSessionMatchProposalV4Store(db).GetLatestAsync(proposal.Id);
        Assert.Equal(2, latest!.Revision);
    }

    private static ZaloPollSessionProposalData BuildProposal() => new()
    {
        Id = "proposal-1",
        TrackedGroupId = "tracked-1",
        PollId = "poll-1",
        PollQuestion = "Vote sân UTE tuần sau. Max 18 slots/sân. 17:45-22:00",
        PollCreatorId = "leader-1",
        PollUpdatedAtUnixMs = 1788750000000,
        PollStructureHash = "poll-hash-1",
        CandidatesJson = "[]",
        ClassifierConfidence = 1,
        ClassifierReason = "deterministic",
        Status = ZaloPollSessionProposalStatus.AwaitingApproval,
        ProposalMessageId = "preview-1"
    };

    private static ZaloTrackedGroupData BuildTracked() => new()
    {
        Id = "tracked-1",
        AdminUserId = "admin-1",
        ZaloConnectionId = "connection-1",
        GroupId = "group-1",
        GroupName = "UTE Volley",
        DefaultTeamCount = 3,
        DefaultTeamSize = 6,
        DefaultLocation = "Sân UTE"
    };

    private static ZaloAutoSessionConversationData BuildConversation(string draftJson) => new()
    {
        Id = "conversation-1",
        ProposalId = "proposal-1",
        TrackedGroupId = "tracked-1",
        PollId = "poll-1",
        GroupId = "group-1",
        OriginalOrganizerId = "leader-1",
        ActiveOrganizerId = "leader-1",
        State = ZaloAutoSessionConversationState.ReadyToConfirm,
        InitialDraftJson = draftJson,
        DraftJson = draftJson,
        PreviewMessageId = "preview-1",
        CurrentBotMessageId = "preview-1",
        Version = 0,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static string DraftJson(
        string? location = "Sân UTE",
        int t6Hour = 17,
        bool cnSelected = true)
    {
        var draft = new ZaloAutoSessionConversationDraft(
            [
                new ZaloAutoSessionConversationDraftItem(
                    "t6", "T6 11/9", "T6", At(11, t6Hour), 10, true),
                new ZaloAutoSessionConversationDraftItem(
                    "cn", "CN 13/9", "CN", At(13, 17), 9, cnSelected)
            ],
            location,
            6);
        return SerializeDraft(draft);
    }

    private static DateTimeOffset At(int day, int hour) =>
        new(2026, 9, day, hour, 45, 0, TimeSpan.FromHours(7));

    private static string SerializeDraft(ZaloAutoSessionConversationDraft draft) =>
        JsonSerializer.Serialize(draft, JsonOptions);

    private static ZaloAutoSessionConversationDraft DeserializeDraft(string json) =>
        JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(json, JsonOptions)!;
}
