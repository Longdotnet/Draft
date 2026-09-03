using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionRolloutGuardTests
{
    [Fact]
    public async Task SupersedePending_GroupScopeInvalidatesOldLiveConfirmation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var settingsStore = new ZaloAutoSessionSettingsStore(db);
        var autoStore = new ZaloAutoSessionStore(db);
        var tracked = await settingsStore.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin-a",
            ZaloConnectionId = "connection-a",
            GroupId = "group-a",
            GroupName = "UTE"
        });
        var proposal = await autoStore.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            Id = "proposal-a",
            TrackedGroupId = tracked.Id,
            PollId = "poll-a",
            PollQuestion = "Bóng tuần này",
            PollCreatorId = "captain-a",
            PollStructureHash = "hash-a",
            CandidatesJson = "[]",
            ClassifierConfidence = 0.95,
            ClassifierReason = "test",
            Status = ZaloPollSessionProposalStatus.AwaitingApproval,
            ProposalMessageId = "message-a"
        });

        var changed = await ZaloAutoSessionRolloutGuard.SupersedePendingAsync(
            db,
            tracked.Id,
            "rollout_changed_to_previewonly");
        var updated = await autoStore.GetProposalAsync(tracked.Id, proposal.PollId);

        Assert.Equal(1, changed);
        Assert.NotNull(updated);
        Assert.Equal(ZaloPollSessionProposalStatus.Superseded, updated!.Status);
        Assert.Equal("rollout_changed_to_previewonly", updated.LastError);
    }
}
