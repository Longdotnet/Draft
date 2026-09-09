using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.AutoSession;

public sealed class ZaloAutoSessionLifecycleReconciliationBudgetV5Tests
{
    [Fact]
    public void Scheduler_lifecycle_budget_is_bounded_below_verifier_window()
    {
        Assert.Equal(12, ZaloAutoSessionLifecycleReconciliationPolicyV5.MaxCandidatesPerCycle);
        Assert.Equal(TimeSpan.FromSeconds(60), ZaloAutoSessionLifecycleReconciliationPolicyV5.CycleBudget);
        Assert.True(ZaloAutoSessionLifecycleReconciliationPolicyV5.CycleBudget < TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task Default_reconciliation_batch_does_not_load_an_unbounded_historical_backlog()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);

        var tracked = await new ZaloAutoSessionSettingsStore(db).InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin-a",
            ZaloConnectionId = "connection-a",
            GroupId = "group-a",
            GroupName = "Bóng UTE"
        });
        var autoSessions = new ZaloAutoSessionStore(db);
        for (var index = 1; index <= 13; index++)
        {
            await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
            {
                TrackedGroupId = tracked.Id,
                PollId = $"poll-{index}",
                PollQuestion = $"Kèo {index}",
                PollCreatorId = "captain-a",
                PollStructureHash = $"hash-{index}",
                Status = ZaloPollSessionProposalStatus.Created
            });
            await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
                $"link-{index}",
                tracked.Id,
                $"poll-{index}",
                $"option-{index}",
                $"session-{index}",
                DateTimeOffset.UtcNow.AddMinutes(index)));
        }

        var candidates = await new ZaloAutoSessionLifecycleHandoffStoreV5(db).GetMissingAsync();

        Assert.Equal(ZaloAutoSessionLifecycleReconciliationPolicyV5.MaxCandidatesPerCycle, candidates.Count);
        Assert.DoesNotContain(candidates, candidate => candidate.SessionId == "session-13");
    }
}
