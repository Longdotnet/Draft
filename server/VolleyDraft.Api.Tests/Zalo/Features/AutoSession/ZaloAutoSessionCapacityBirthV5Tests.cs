using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionCapacityBirthV5Tests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CreateFromPreview_ExplicitCapacitySeedsInitialDraftAndDurableAuthority()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var tracked = BuildTracked(teamSize: 7);
        var proposal = BuildProposal("Vote sân UTE tuần sau. Max 18 slots/sân. 17:45-22:00");
        var candidates = BuildCandidates();

        var conversation = await new ZaloAutoSessionConversationStore(db).CreateFromPreviewAsync(
            proposal,
            tracked,
            candidates,
            "preview-1",
            new ConfigurationBuilder().Build());

        var initialDraft = JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(
            conversation.InitialDraftJson,
            JsonOptions)!;
        Assert.Equal(6, initialDraft.TeamSize);

        var latest = await new ZaloAutoSessionMatchProposalV4Store(db).GetLatestAsync(proposal.Id);
        Assert.NotNull(latest);
        var evidence = JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            latest!.EvidenceJson,
            JsonOptions)!;
        Assert.Equal("poll_title_explicit_capacity", evidence.TeamSize.Source);
        Assert.Contains("capacity=18", evidence.TeamSize.Detail, StringComparison.Ordinal);
        Assert.Contains("teamCount=3", evidence.TeamSize.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateFromPreview_NoExplicitCapacityStillUsesApprovedGroupDefault()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var tracked = BuildTracked(teamSize: 7);
        var proposal = BuildProposal("Vote sân UTE tuần sau. 17:45-22:00");

        var conversation = await new ZaloAutoSessionConversationStore(db).CreateFromPreviewAsync(
            proposal,
            tracked,
            BuildCandidates(),
            "preview-1",
            new ConfigurationBuilder().Build());

        var draft = JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(
            conversation.InitialDraftJson,
            JsonOptions)!;
        Assert.Equal(7, draft.TeamSize);

        var latest = await new ZaloAutoSessionMatchProposalV4Store(db).GetLatestAsync(proposal.Id);
        var evidence = JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            latest!.EvidenceJson,
            JsonOptions)!;
        Assert.Equal("approved_group_default", evidence.TeamSize.Source);
        Assert.Equal("ZaloTrackedGroups.DefaultTeamSize", evidence.TeamSize.Detail);
    }

    [Fact]
    public void InitialPreview_ResolvedExplicitCapacityShowsPollTruthNotGroupDefault()
    {
        var poll = BuildPoll("Vote sân UTE tuần sau. Max 18 slots/sân. 17:45-22:00");
        var capacity = ZaloAutoSessionCapacityPolicyV5.Resolve(poll.Question);

        var body = ZaloAutoSessionV2Service.BuildOrganizerPreview(
            poll,
            BuildCandidates(),
            ZaloAutoSessionCapacityPolicyV5.SupportedTeamCount,
            capacity.TeamSize,
            4,
            "Sân UTE",
            ZaloAutoSessionRolloutMode.Live);

        Assert.True(capacity.IsValid);
        Assert.Contains("/18 người", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/21 người", body, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidExplicitCapacity_GroundsRecoveryWithoutAiAndIsNotRetriedUntilPollChanges()
    {
        var poll = BuildPoll("Vote sân UTE. Max 18 slots/sân. Tối đa 21 người");
        var capacity = ZaloAutoSessionCapacityPolicyV5.Resolve(poll.Question);
        var prompt = ZaloAutoSessionV2Service.BuildCapacityConflictPrompt(poll, capacity);
        var proposal = BuildProposal(poll.Question);
        proposal.Status = ZaloPollSessionProposalStatus.Ignored;
        proposal.ClassifierReason = capacity.ErrorCode!;
        proposal.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        proposal.PollUpdatedAtUnixMs = poll.UpdatedAtUnixMs;

        Assert.False(capacity.IsValid);
        Assert.Contains("Website CHƯA được tạo", prompt, StringComparison.Ordinal);
        Assert.Contains("sửa", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không dùng AI", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.False(ZaloAutoSessionV2Service.ShouldRetryIgnoredProposal(
            proposal,
            poll,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(15)));
    }

    private static ZaloTrackedGroupData BuildTracked(int teamSize) => new()
    {
        Id = "tracked-1",
        AdminUserId = "admin-1",
        ZaloConnectionId = "connection-1",
        GroupId = "group-1",
        GroupName = "UTE Volley",
        DefaultStartMinutes = 17 * 60 + 45,
        DefaultTeamCount = 3,
        DefaultTeamSize = teamSize,
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
        ProposalMessageId = "preview-1",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static IReadOnlyList<ZaloAutoSessionCandidate> BuildCandidates() =>
    [
        new ZaloAutoSessionCandidate(
            "t6",
            "T6 11/9",
            "T6",
            new DateTimeOffset(2026, 9, 11, 17, 45, 0, TimeSpan.FromHours(7)),
            10)
    ];

    private static BridgePoll BuildPoll(string question) => new(
        "poll-1",
        question,
        "leader-1",
        [new BridgePollOption("t6", "T6 11/9", 10, ["u1"])],
        true,
        false,
        false,
        false,
        10,
        1788740000000,
        1788750000000,
        0);
}
