using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.AutoSession;

public sealed class ZaloAutoSessionLifecycleHandoffRecoveryV5Tests
{
    [Fact]
    public async Task Missing_created_handoff_is_rediscovered_after_store_restart()
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
        var proposal = await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = tracked.Id,
            PollId = "poll-1",
            PollQuestion = "Kèo tuần sau",
            PollCreatorId = "captain-a",
            PollStructureHash = "hash-1",
            Status = ZaloPollSessionProposalStatus.Created
        });
        await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-1",
            tracked.Id,
            "poll-1",
            "option-1",
            "session-1",
            DateTimeOffset.UtcNow));

        // A fresh store models the process/request that originally attempted the post-commit
        // handoff being gone. Durable Created + link state must be sufficient for recovery.
        var restartedStore = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        var missing = await restartedStore.GetMissingAsync();

        var candidate = Assert.Single(missing);
        Assert.Equal(proposal.Id, candidate.ProposalId);
        Assert.Equal("admin-a", candidate.AdminUserId);
        Assert.Equal("session-1", candidate.SessionId);
    }

    [Fact]
    public async Task Existing_handoff_snapshot_is_not_retried()
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
        var proposal = await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = tracked.Id,
            PollId = "poll-1",
            PollQuestion = "Kèo tuần sau",
            PollCreatorId = "captain-a",
            PollStructureHash = "hash-1",
            Status = ZaloPollSessionProposalStatus.Created
        });
        await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-1",
            tracked.Id,
            "poll-1",
            "option-1",
            "session-1",
            DateTimeOffset.UtcNow));

        var handoffs = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        await handoffs.EnsureAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloAutoSessionLifecycleHandoffs"
                ("SessionId", "ProposalId", "Stage", "Owner", "NeedsWebsite", "ReasonCode", "SnapshotJson", "HandedOffAt")
            VALUES
                ({{"session-1"}}, {{proposal.Id}}, {{"Recruiting"}}, {{"ZaloBot"}}, {{0}}, {{"ready"}}, {{"{}"}}, {{DateTimeOffset.UtcNow.ToString("O")}});
            """);

        Assert.Empty(await new ZaloAutoSessionLifecycleHandoffStoreV5(db).GetMissingAsync());
    }

    [Fact]
    public async Task Non_created_proposal_is_not_reconciliation_candidate()
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
        await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = tracked.Id,
            PollId = "poll-1",
            PollQuestion = "Kèo tuần sau",
            PollCreatorId = "captain-a",
            PollStructureHash = "hash-1",
            Status = ZaloPollSessionProposalStatus.AwaitingApproval
        });
        await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-1",
            tracked.Id,
            "poll-1",
            "option-1",
            "session-1",
            DateTimeOffset.UtcNow));

        Assert.Empty(await new ZaloAutoSessionLifecycleHandoffStoreV5(db).GetMissingAsync());
    }

    [Fact]
    public async Task Reconciliation_batch_prioritizes_never_then_least_recently_attempted_sessions()
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
        foreach (var index in new[] { 1, 2 })
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

        var store = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        var firstAttempt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await store.MarkAttemptAsync("session-1", firstAttempt);

        var neverAttempted = Assert.Single(await store.GetMissingAsync(limit: 1));
        Assert.Equal("session-2", neverAttempted.SessionId);

        await store.MarkAttemptAsync("session-2", DateTimeOffset.UtcNow);
        var leastRecent = Assert.Single(await store.GetMissingAsync(limit: 1));
        Assert.Equal("session-1", leastRecent.SessionId);
    }

    [Fact]
    public async Task Proposal_ownership_is_not_handed_off_until_every_linked_session_has_snapshot()
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
        var proposal = await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = tracked.Id,
            PollId = "poll-1",
            PollQuestion = "Kèo tuần sau",
            PollCreatorId = "captain-a",
            PollStructureHash = "hash-1",
            Status = ZaloPollSessionProposalStatus.Created
        });
        foreach (var index in new[] { 1, 2 })
        {
            await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
                $"link-{index}", tracked.Id, "poll-1", $"option-{index}", $"session-{index}", DateTimeOffset.UtcNow));
        }

        var store = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        await store.EnsureAsync();
        await InsertHandoffAsync(db, proposal.Id, "session-1");

        Assert.False(await store.TryFinalizeProposalOwnershipAsync(proposal.Id));
        Assert.False(await store.HasHandedOffOwnershipAsync(proposal.Id));

        await InsertHandoffAsync(db, proposal.Id, "session-2");

        Assert.True(await store.TryFinalizeProposalOwnershipAsync(proposal.Id));
        Assert.True(await store.HasHandedOffOwnershipAsync(proposal.Id));
    }

    [Fact]
    public async Task Restart_reconciliation_backfills_handed_off_ownership_without_retrying_sessions()
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
        var proposal = await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = tracked.Id,
            PollId = "poll-1",
            PollQuestion = "Kèo tuần sau",
            PollCreatorId = "captain-a",
            PollStructureHash = "hash-1",
            Status = ZaloPollSessionProposalStatus.Created
        });
        await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-1", tracked.Id, "poll-1", "option-1", "session-1", DateTimeOffset.UtcNow));

        var oldStore = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        await oldStore.EnsureAsync();
        await InsertHandoffAsync(db, proposal.Id, "session-1");

        var restartedStore = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
        Assert.Empty(await restartedStore.GetMissingAsync());
        Assert.False(await restartedStore.HasHandedOffOwnershipAsync(proposal.Id));

        Assert.Equal(1, await restartedStore.ReconcileCompletedOwnershipsAsync());
        Assert.True(await restartedStore.HasHandedOffOwnershipAsync(proposal.Id));
    }

    private static Task InsertHandoffAsync(VolleyDraftDbContext db, string proposalId, string sessionId) =>
        db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloAutoSessionLifecycleHandoffs"
                ("SessionId", "ProposalId", "Stage", "Owner", "NeedsWebsite", "ReasonCode", "SnapshotJson", "HandedOffAt")
            VALUES
                ({{sessionId}}, {{proposalId}}, {{"Recruiting"}}, {{"ZaloBot"}}, {{0}}, {{"ready"}}, {{"{}"}}, {{DateTimeOffset.UtcNow.ToString("O")}});
            """);
}
