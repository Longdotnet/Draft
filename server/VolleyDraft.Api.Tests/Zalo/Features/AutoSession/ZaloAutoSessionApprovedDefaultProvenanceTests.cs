using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionApprovedDefaultProvenanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Vote tuần sau", "T6 11/9 18h", "poll_option_explicit_time")]
    [InlineData("Vote tuần sau. 17:45-22:00", "T6 11/9", "poll_title_explicit_time")]
    [InlineData("Vote tuần sau", "T6 11/9", "approved_group_default")]
    public async Task InitialStartTimeEvidence_RecordsTheActualDeterministicAuthority(
        string pollQuestion,
        string optionContent,
        string expectedSource)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var tracked = BuildTracked();
        var proposal = BuildProposal(pollQuestion);
        var conversation = BuildConversation(optionContent);

        var result = await new ZaloAutoSessionMatchProposalV4Store(db)
            .InitializeFromPreviewAsync(proposal, tracked, conversation);

        Assert.NotNull(result);
        var evidence = JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            result!.Revision.EvidenceJson,
            JsonOptions)!;
        Assert.Equal(expectedSource, evidence.StartTimes["t6"].Source);
        if (expectedSource == "approved_group_default")
            Assert.Equal("ZaloTrackedGroups.DefaultStartMinutes=1080", evidence.StartTimes["t6"].Detail);
    }

    [Fact]
    public async Task InitialEvidence_ApprovesOnlyDefaultsThatStillMatchThePersistedDraft()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);

        var result = await new ZaloAutoSessionMatchProposalV4Store(db)
            .InitializeFromPreviewAsync(
                BuildProposal("Vote tuần sau"),
                BuildTracked(),
                BuildConversation("T6 11/9"));

        var evidence = JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            result!.Revision.EvidenceJson,
            JsonOptions)!;

        Assert.Equal("approved_group_default", evidence.Location.Source);
        Assert.Equal("ZaloTrackedGroups.DefaultLocation", evidence.Location.Detail);
        Assert.Equal("approved_group_default", evidence.TeamSize.Source);
        Assert.Equal("ZaloTrackedGroups.DefaultTeamSize", evidence.TeamSize.Detail);
        Assert.Equal("approved_group_default", evidence.StartTimes["t6"].Source);
    }

    [Fact]
    public async Task LazyMigration_DoesNotBlessDefaultsThatChangedAfterPreview()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var tracked = BuildTracked();
        var conversation = BuildConversation("T6 11/9");

        // The durable V3 preview still contains the old approved values, but the admin has
        // since changed group policy before V4 lazily initializes this proposal.
        tracked.DefaultLocation = "Sân mới";
        tracked.DefaultTeamSize = 7;
        tracked.DefaultStartMinutes = 18 * 60 + 30;

        var result = await new ZaloAutoSessionMatchProposalV4Store(db)
            .InitializeFromPreviewAsync(BuildProposal("Vote tuần sau"), tracked, conversation);

        var evidence = JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            result!.Revision.EvidenceJson,
            JsonOptions)!;

        Assert.Equal("stale_or_unapproved_default", evidence.Location.Source);
        Assert.Contains("Sân UTE", evidence.Location.Detail);
        Assert.Contains("Sân mới", evidence.Location.Detail);
        Assert.Equal("stale_or_unapproved_default", evidence.TeamSize.Source);
        Assert.Contains("draft=6", evidence.TeamSize.Detail);
        Assert.Contains("current=7", evidence.TeamSize.Detail);
        Assert.Equal("stale_or_unapproved_default", evidence.StartTimes["t6"].Source);
        Assert.Contains("draft=1080", evidence.StartTimes["t6"].Detail);
        Assert.Contains("current=1110", evidence.StartTimes["t6"].Detail);
    }

    [Fact]
    public async Task ExplicitPollTime_RemainsAuthoritativeWhenGroupDefaultChanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var tracked = BuildTracked();
        tracked.DefaultStartMinutes = 18 * 60 + 30;

        var result = await new ZaloAutoSessionMatchProposalV4Store(db)
            .InitializeFromPreviewAsync(
                BuildProposal("Vote tuần sau"),
                tracked,
                BuildConversation("T6 11/9 18h"));

        var evidence = JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            result!.Revision.EvidenceJson,
            JsonOptions)!;
        Assert.Equal("poll_option_explicit_time", evidence.StartTimes["t6"].Source);
    }

    [Fact]
    public async Task OrganizerTimeCorrection_ReplacesApprovedDefaultAuthority()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionMatchProposalV4Store(db);
        var conversation = BuildConversation("T6 11/9");
        await store.InitializeFromPreviewAsync(BuildProposal("Vote tuần sau"), BuildTracked(), conversation);

        var draft = JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(conversation.DraftJson, JsonOptions)!;
        conversation.Version = 1;
        conversation.LastIntent = "ModifyDraft";
        conversation.ActiveOrganizerId = "leader-1";
        conversation.DraftJson = JsonSerializer.Serialize(
            draft with
            {
                Items = draft.Items.Select(item => item with { StartTime = item.StartTime.AddMinutes(30) }).ToList()
            },
            JsonOptions);

        var result = await store.SaveConversationDraftAsync(conversation);
        var evidence = JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            result!.Revision.EvidenceJson,
            JsonOptions)!;

        Assert.Equal("organizer_correction", evidence.StartTimes["t6"].Source);
        Assert.Equal("leader-1", evidence.StartTimes["t6"].ActorZaloUserId);
        Assert.Equal("ModifyDraft", evidence.StartTimes["t6"].Intent);
    }

    private static ZaloTrackedGroupData BuildTracked() => new()
    {
        Id = "tracked-1",
        AdminUserId = "admin-1",
        ZaloConnectionId = "connection-1",
        GroupId = "group-1",
        GroupName = "UTE Volley",
        DefaultStartMinutes = 18 * 60,
        DefaultTeamCount = 3,
        DefaultTeamSize = 6,
        DefaultLocation = "Sân UTE"
    };

    private static ZaloPollSessionProposalData BuildProposal(string question) => new()
    {
        Id = "proposal-1",
        TrackedGroupId = "tracked-1",
        PollId = "poll-1",
        PollQuestion = question,
        PollCreatorId = "leader-1",
        PollUpdatedAtUnixMs = 1788750000000,
        PollStructureHash = "poll-hash-1",
        CandidatesJson = "[]",
        ClassifierConfidence = 1,
        ClassifierReason = "deterministic",
        Status = ZaloPollSessionProposalStatus.AwaitingApproval,
        ProposalMessageId = "preview-1"
    };

    private static ZaloAutoSessionConversationData BuildConversation(string optionContent)
    {
        var draft = new ZaloAutoSessionConversationDraft(
            [new ZaloAutoSessionConversationDraftItem(
                "t6",
                optionContent,
                "T6",
                new DateTimeOffset(2026, 9, 11, 18, 0, 0, TimeSpan.FromHours(7)),
                10,
                true)],
            "Sân UTE",
            6);
        var json = JsonSerializer.Serialize(draft, JsonOptions);
        return new ZaloAutoSessionConversationData
        {
            Id = "conversation-1",
            ProposalId = "proposal-1",
            TrackedGroupId = "tracked-1",
            PollId = "poll-1",
            GroupId = "group-1",
            OriginalOrganizerId = "leader-1",
            ActiveOrganizerId = "leader-1",
            State = ZaloAutoSessionConversationState.ReadyToConfirm,
            InitialDraftJson = json,
            DraftJson = json,
            PreviewMessageId = "preview-1",
            CurrentBotMessageId = "preview-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
