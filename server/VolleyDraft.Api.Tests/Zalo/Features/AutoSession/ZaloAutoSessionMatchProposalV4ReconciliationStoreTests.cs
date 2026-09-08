using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionMatchProposalV4ReconciliationStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 21, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public async Task VoteDrift_PersistsNewSourceBaseline_AndPreservesOrganizerTimeCorrectionAcrossRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;

        await using (var db = new VolleyDraftDbContext(options))
        {
            var revisionStore = new ZaloAutoSessionMatchProposalV4Store(db);
            var conversation = Conversation(SourceJson());
            await revisionStore.InitializeFromPreviewAsync(Proposal(), Tracked(), conversation);
            conversation.Version = 1;
            conversation.ActiveOrganizerId = "leader-1";
            conversation.LastIntent = "ModifyDraft";
            conversation.DraftJson = DraftJson(t6Hour: 18, t6Votes: 10);
            Assert.True((await revisionStore.SaveConversationDraftAsync(conversation))!.Accepted);

            var source = Deserialize(SourceJson());
            var durable = Deserialize(conversation.DraftJson);
            var poll = Poll(t6Votes: 12);
            var revalidation = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(poll, Tracked(), source, durable, Now);
            Assert.True(revalidation.CanExecute);
            Assert.Contains(revalidation.Reconciliation.Changes, change => change.Kind == ZaloAutoSessionPollChangeKindV4.VoteCountChanged);

            conversation.Version = 2;
            var store = new ZaloAutoSessionMatchProposalV4ReconciliationStore(db);
            var saved = await store.AppendReconciliationAsync(conversation, poll, source, revalidation, "leader-1");
            Assert.NotNull(saved);
            Assert.True(saved!.Accepted);
            Assert.Equal("source_reconciled", saved.Reason);
            Assert.Equal(3, saved.Revision.Revision);
            var durableAfter = Deserialize(saved.Revision.DraftJson);
            Assert.Equal(18, durableAfter.Items.Single(item => item.OptionId == "t6").StartTime.Hour);
            Assert.Equal(12, durableAfter.Items.Single(item => item.OptionId == "t6").VoteCount);
        }

        await using var restartedDb = new VolleyDraftDbContext(options);
        var restarted = new ZaloAutoSessionMatchProposalV4ReconciliationStore(restartedDb);
        var sourceAfterRestart = await restarted.LoadSourceAsync("proposal-1", SourceJson());
        Assert.NotNull(sourceAfterRestart);
        Assert.Equal(12, sourceAfterRestart!.Draft.Items.Single(item => item.OptionId == "t6").VoteCount);
        Assert.Equal(17, sourceAfterRestart.Draft.Items.Single(item => item.OptionId == "t6").StartTime.Hour);
    }

    [Fact]
    public async Task ApprovedLocationPolicyRefresh_PersistsNewSourceBaseline_AndDoesNotLoopAfterRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;

        ZaloAutoSessionMatchProposalV4WriteResult? saved;
        var changedTracked = Tracked(location: "Sân B");
        var poll = Poll();
        await using (var db = new VolleyDraftDbContext(options))
        {
            var revisions = new ZaloAutoSessionMatchProposalV4Store(db);
            var conversation = Conversation(SourceJson());
            await revisions.InitializeFromPreviewAsync(Proposal(), Tracked(), conversation);

            var source = Deserialize(SourceJson());
            var first = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
                poll,
                changedTracked,
                source,
                source,
                Now);
            Assert.True(first.CanExecute);
            Assert.Equal("Sân B", first.Reconciliation.Draft.Location);
            Assert.Contains(first.Reconciliation.Changes, change =>
                change.Kind == ZaloAutoSessionPollChangeKindV4.ApprovedLocationChanged && !change.RequiresConfirmation);

            conversation.Version = 1;
            var store = new ZaloAutoSessionMatchProposalV4ReconciliationStore(db);
            saved = await store.AppendReconciliationAsync(conversation, poll, source, first, "leader-1");
            Assert.NotNull(saved);
            Assert.True(saved!.Accepted);
            Assert.Equal("Sân B", Deserialize(saved.Revision.DraftJson).Location);
        }

        await using var restartedDb = new VolleyDraftDbContext(options);
        var restarted = new ZaloAutoSessionMatchProposalV4ReconciliationStore(restartedDb);
        var sourceAfterRestart = await restarted.LoadSourceAsync("proposal-1", SourceJson());
        Assert.NotNull(sourceAfterRestart);
        Assert.Equal("Sân B", sourceAfterRestart!.Draft.Location);

        var durableAfter = Deserialize(saved!.Revision.DraftJson);
        var second = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll,
            changedTracked,
            sourceAfterRestart.Draft,
            durableAfter,
            Now.AddMinutes(5));
        Assert.True(second.CanExecute);
        Assert.DoesNotContain(second.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ApprovedLocationChanged);
    }

    [Fact]
    public async Task MaterialSourceTimeChange_BecomesNewBaseline_ThenSecondRevalidationDoesNotLoop()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var revisions = new ZaloAutoSessionMatchProposalV4Store(db);
        var conversation = Conversation(SourceJson());
        await revisions.InitializeFromPreviewAsync(Proposal(), Tracked(), conversation);
        conversation.Version = 1;
        conversation.ActiveOrganizerId = "leader-1";
        conversation.LastIntent = "ModifyDraft";
        conversation.DraftJson = DraftJson(t6Hour: 18, t6Votes: 10);
        Assert.True((await revisions.SaveConversationDraftAsync(conversation))!.Accepted);

        var oldSource = Deserialize(SourceJson());
        var durable = Deserialize(conversation.DraftJson);
        var changedPoll = Poll(t6Hour: 19, t6Votes: 10);
        var first = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(changedPoll, Tracked(), oldSource, durable, Now);
        Assert.True(first.RequiresOrganizerConfirmation);
        Assert.Equal(19, first.Reconciliation.Draft.Items.Single(item => item.OptionId == "t6").StartTime.Hour);

        conversation.Version = 2;
        var store = new ZaloAutoSessionMatchProposalV4ReconciliationStore(db);
        var saved = await store.AppendReconciliationAsync(conversation, changedPoll, oldSource, first, "leader-1");
        Assert.True(saved!.Accepted);

        var newSource = await store.LoadSourceAsync("proposal-1", SourceJson());
        var durableAfter = Deserialize(saved.Revision.DraftJson);
        var second = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            changedPoll,
            Tracked(),
            newSource!.Draft,
            durableAfter,
            Now);
        Assert.True(second.CanExecute);
        Assert.Empty(second.Reconciliation.Changes);
    }

    [Fact]
    public async Task SameConversationVersion_CannotAppendCompetingReconciliation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var revisions = new ZaloAutoSessionMatchProposalV4Store(db);
        var conversation = Conversation(SourceJson());
        await revisions.InitializeFromPreviewAsync(Proposal(), Tracked(), conversation);

        var source = Deserialize(SourceJson());
        var poll = Poll(t6Votes: 11);
        var revalidation = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(poll, Tracked(), source, source, Now);
        conversation.Version = 1;
        var store = new ZaloAutoSessionMatchProposalV4ReconciliationStore(db);
        var first = await store.AppendReconciliationAsync(conversation, poll, source, revalidation, "leader-1");
        var second = await store.AppendReconciliationAsync(conversation, poll, source, revalidation, "leader-2");

        Assert.True(first!.Accepted);
        Assert.False(second!.Accepted);
        Assert.Equal("stale_or_conflicting_conversation_version", second.Reason);
    }

    private static BridgePoll Poll(int t6Hour = 17, int t6Votes = 10) => new(
        "poll-1",
        $"Vote sân UTE tuần sau. Max 18 slots/sân. {t6Hour}:45-22:00",
        "leader-1",
        [
            new BridgePollOption("t6", "T6 11/9", t6Votes, []),
            new BridgePollOption("cn", "CN 13/9", 9, [])
        ],
        true,
        false,
        false,
        false,
        t6Votes + 9,
        1788750000000,
        1788750001000 + t6Votes + t6Hour,
        0);

    private static ZaloPollSessionProposalData Proposal() => new()
    {
        Id = "proposal-1",
        TrackedGroupId = "tracked-1",
        PollId = "poll-1",
        PollQuestion = "Vote sân UTE tuần sau. Max 18 slots/sân. 17:45-22:00",
        PollCreatorId = "leader-1",
        PollUpdatedAtUnixMs = 1788750000000,
        PollStructureHash = "initial-hash",
        CandidatesJson = "[]",
        ClassifierConfidence = 1,
        ClassifierReason = "deterministic",
        Status = ZaloPollSessionProposalStatus.AwaitingApproval,
        ProposalMessageId = "preview-1"
    };

    private static ZaloTrackedGroupData Tracked(string location = "Sân UTE", int teamSize = 6) => new()
    {
        Id = "tracked-1",
        AdminUserId = "admin-1",
        ZaloConnectionId = "connection-1",
        GroupId = "group-1",
        GroupName = "UTE Volley",
        DefaultTeamCount = 3,
        DefaultTeamSize = teamSize,
        DefaultLocation = location
    };

    private static ZaloAutoSessionConversationData Conversation(string draftJson) => new()
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

    private static string SourceJson() => DraftJson(t6Hour: 17, t6Votes: 10);

    private static string DraftJson(int t6Hour, int t6Votes) => JsonSerializer.Serialize(
        new ZaloAutoSessionConversationDraft(
            [
                new("t6", "T6 11/9", "T6", At(11, t6Hour), t6Votes, true),
                new("cn", "CN 13/9", "CN", At(13, 17), 9, true)
            ],
            "Sân UTE",
            6),
        JsonOptions);

    private static ZaloAutoSessionConversationDraft Deserialize(string json) =>
        JsonSerializer.Deserialize<ZaloAutoSessionConversationDraft>(json, JsonOptions)!;

    private static DateTimeOffset At(int day, int hour) =>
        new(2026, 9, day, hour, 45, 0, TimeSpan.FromHours(7));
}
