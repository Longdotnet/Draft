using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionCanonicalClockProvenanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("17:30")]
    [InlineData("17h30")]
    [InlineData("17g30")]
    [InlineData("17 giờ 30")]
    public void Provenance_clock_detection_uses_the_canonical_session_grammar(string clock)
    {
        Assert.True(ZaloSessionResolver.ContainsExplicitSessionTime($"T6 11/9 {clock}"));
    }

    [Theory]
    [InlineData("17:30")]
    [InlineData("17h30")]
    [InlineData("17g30")]
    [InlineData("17 giờ 30")]
    public async Task Explicit_option_clock_is_never_downgraded_to_group_default(string clock)
    {
        var evidence = await BuildEvidenceAsync("Vote tuần sau", $"T6 11/9 {clock}");

        Assert.Equal("poll_option_explicit_time", evidence.StartTimes["t6"].Source);
    }

    [Theory]
    [InlineData("17:30")]
    [InlineData("17h30")]
    [InlineData("17g30")]
    [InlineData("17 giờ 30")]
    public async Task Explicit_poll_title_clock_is_never_downgraded_to_group_default(string clock)
    {
        var evidence = await BuildEvidenceAsync($"Vote tuần sau, đánh {clock}", "T6 11/9");

        Assert.Equal("poll_title_explicit_time", evidence.StartTimes["t6"].Source);
    }

    private static async Task<ZaloAutoSessionProposalEvidenceV4> BuildEvidenceAsync(
        string pollQuestion,
        string optionContent)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);

        var result = await new ZaloAutoSessionMatchProposalV4Store(db)
            .InitializeFromPreviewAsync(
                BuildProposal(pollQuestion),
                BuildTracked(),
                BuildConversation(optionContent));

        Assert.NotNull(result);
        return JsonSerializer.Deserialize<ZaloAutoSessionProposalEvidenceV4>(
            result!.Revision.EvidenceJson,
            JsonOptions)!;
    }

    private static ZaloTrackedGroupData BuildTracked() => new()
    {
        Id = "tracked-clock",
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
        Id = "proposal-clock",
        TrackedGroupId = "tracked-clock",
        PollId = "poll-clock",
        PollQuestion = question,
        PollCreatorId = "leader-1",
        PollUpdatedAtUnixMs = 1788750000000,
        PollStructureHash = "poll-hash-clock",
        CandidatesJson = "[]",
        ClassifierConfidence = 1,
        ClassifierReason = "deterministic",
        Status = ZaloPollSessionProposalStatus.AwaitingApproval,
        ProposalMessageId = "preview-clock"
    };

    private static ZaloAutoSessionConversationData BuildConversation(string optionContent)
    {
        var draft = new ZaloAutoSessionConversationDraft(
            [new ZaloAutoSessionConversationDraftItem(
                "t6",
                optionContent,
                "T6",
                new DateTimeOffset(2026, 9, 11, 17, 30, 0, TimeSpan.FromHours(7)),
                10,
                true)],
            "Sân UTE",
            6);
        var json = JsonSerializer.Serialize(draft, JsonOptions);
        return new ZaloAutoSessionConversationData
        {
            Id = "conversation-clock",
            ProposalId = "proposal-clock",
            TrackedGroupId = "tracked-clock",
            PollId = "poll-clock",
            GroupId = "group-1",
            OriginalOrganizerId = "leader-1",
            ActiveOrganizerId = "leader-1",
            State = ZaloAutoSessionConversationState.ReadyToConfirm,
            InitialDraftJson = json,
            DraftJson = json,
            PreviewMessageId = "preview-clock",
            CurrentBotMessageId = "preview-clock",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
