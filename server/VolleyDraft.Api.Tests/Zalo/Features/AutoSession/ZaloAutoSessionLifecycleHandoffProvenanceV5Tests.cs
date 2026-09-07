using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.AutoSession;

public sealed class ZaloAutoSessionLifecycleHandoffProvenanceV5Tests
{
    [Fact]
    public async Task Snapshot_from_another_proposal_does_not_satisfy_missing_or_ownership_checks()
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
        var proposalA = await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = tracked.Id,
            PollId = "poll-a",
            PollQuestion = "Kèo A",
            PollCreatorId = "captain-a",
            PollStructureHash = "hash-a",
            Status = ZaloPollSessionProposalStatus.Created
        });
        var proposalB = await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = tracked.Id,
            PollId = "poll-b",
            PollQuestion = "Kèo B",
            PollCreatorId = "captain-a",
            PollStructureHash = "hash-b",
            Status = ZaloPollSessionProposalStatus.Created
        });
        await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-a", tracked.Id, "poll-a", "option-a", "shared-session", DateTimeOffset.UtcNow));
        await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-b", tracked.Id, "poll-b", "option-b", "shared-session", DateTimeOffset.UtcNow));

        var store = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        await store.EnsureAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloAutoSessionLifecycleHandoffs"
                ("SessionId", "ProposalId", "Stage", "Owner", "NeedsWebsite", "ReasonCode", "SnapshotJson", "HandedOffAt")
            VALUES
                ({{"shared-session"}}, {{proposalA.Id}}, {{"Recruiting"}}, {{"ZaloBot"}}, {{0}}, {{"ready"}}, {{"{}"}}, {{DateTimeOffset.UtcNow.ToString("O")}});
            """);

        var missing = await store.GetMissingAsync();
        var candidate = Assert.Single(missing);
        Assert.Equal(proposalB.Id, candidate.ProposalId);
        Assert.Equal("shared-session", candidate.SessionId);

        Assert.False(await store.TryFinalizeProposalOwnershipAsync(proposalB.Id));
        Assert.False(await store.HasHandedOffOwnershipAsync(proposalB.Id));
        Assert.True(await store.TryFinalizeProposalOwnershipAsync(proposalA.Id));
        Assert.True(await store.HasHandedOffOwnershipAsync(proposalA.Id));
    }
}
