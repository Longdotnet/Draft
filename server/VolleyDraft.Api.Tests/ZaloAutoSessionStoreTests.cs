using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionStoreTests
{
    [Fact]
    public async Task EnsureAsync_CreatesTrackingProposalAndIdempotencyTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionStore(db);

        await store.EnsureAsync();

        foreach (var table in new[] { "ZaloTrackedGroups", "ZaloPollSessionProposals", "ZaloAutoSessionLinks" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
            command.Parameters.AddWithValue("@name", table);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task AddLinkAsync_SamePollOptionCanOnlyClaimOneSession()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionStore(db);
        await store.EnsureAsync();

        await store.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-1",
            "tracked-1",
            "poll-1",
            "option-t6",
            "session-first",
            DateTimeOffset.UtcNow));
        await store.AddLinkAsync(new ZaloAutoSessionLinkData(
            "link-2",
            "tracked-1",
            "poll-1",
            "option-t6",
            "session-second",
            DateTimeOffset.UtcNow));

        var claimed = await store.GetLinkAsync("tracked-1", "poll-1", "option-t6");

        Assert.NotNull(claimed);
        Assert.Equal("session-first", claimed!.SessionId);
    }

    [Fact]
    public async Task UpsertProposal_StructureChangeReplacesPendingProposalFacts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionStore(db);
        await store.EnsureAsync();

        var proposal = await store.UpsertProposalAsync(new ZaloPollSessionProposalData
        {
            TrackedGroupId = "tracked-1",
            PollId = "poll-1",
            PollQuestion = "Bóng tuần này",
            PollCreatorId = "captain-1",
            PollStructureHash = "hash-a",
            CandidatesJson = "[]",
            ClassifierConfidence = 0.9,
            ClassifierReason = "rule",
            Status = ZaloPollSessionProposalStatus.AwaitingApproval,
            ProposalMessageId = "message-a"
        });

        proposal.PollQuestion = "Bóng tuần này - lịch mới";
        proposal.PollStructureHash = "hash-b";
        proposal.ProposalMessageId = null;
        proposal.Status = ZaloPollSessionProposalStatus.AwaitingApproval;
        var updated = await store.UpsertProposalAsync(proposal);

        Assert.Equal(proposal.Id, updated.Id);
        Assert.Equal("hash-b", updated.PollStructureHash);
        Assert.Equal("Bóng tuần này - lịch mới", updated.PollQuestion);
        Assert.Null(updated.ProposalMessageId);
    }
}
