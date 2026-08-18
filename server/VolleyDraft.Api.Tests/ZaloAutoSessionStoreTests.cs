using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
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
    public async Task AddLinkAsync_ParticipatesInCurrentEfTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionStore(db);
        await store.EnsureAsync();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await store.AddLinkAsync(new ZaloAutoSessionLinkData(
                "link-rollback",
                "tracked-1",
                "poll-1",
                "option-cn",
                "session-rollback",
                DateTimeOffset.UtcNow));
            Assert.NotNull(await store.GetLinkAsync("tracked-1", "poll-1", "option-cn"));
            await transaction.RollbackAsync();
        }

        Assert.Null(await store.GetLinkAsync("tracked-1", "poll-1", "option-cn"));
    }

    [Fact]
    public async Task SeedFromExistingSessions_BackfillsManualPollImportsAsClaims()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = "admin@example.test",
            PasswordHash = "hash"
        };
        var zalo = new ZaloConnection
        {
            Id = "connection-1",
            AdminUserId = admin.Id,
            AccountZaloId = "zalo-account-1",
            DisplayName = "Zalo Admin",
            EncryptedCredentials = "encrypted"
        };
        var session = new MatchSession
        {
            Id = "session-existing",
            Name = "T6 existing",
            AdminUserId = admin.Id,
            ZaloConnectionId = zalo.Id,
            ZaloGroupId = "group-1",
            ZaloGroupName = "Volley Group"
        };
        var import = new PollImport
        {
            Id = "import-1",
            SessionId = session.Id,
            ImportedByUserId = admin.Id,
            ZaloGroupId = "group-1",
            PollId = "poll-existing",
            PollQuestion = "Bóng tuần này",
            SelectedOptionIdsJson = "[\"option-t6\",\"option-cn\"]",
            ImportedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(admin);
        db.ZaloConnections.Add(zalo);
        db.MatchSessions.Add(session);
        db.PollImports.Add(import);
        await db.SaveChangesAsync();

        var store = new ZaloAutoSessionStore(db);
        await store.SeedFromExistingSessionsAsync();

        await using var trackedCommand = connection.CreateCommand();
        trackedCommand.CommandText = "SELECT \"Id\" FROM \"ZaloTrackedGroups\" WHERE \"ZaloConnectionId\"='connection-1' AND \"GroupId\"='group-1';";
        var trackedGroupId = Convert.ToString(await trackedCommand.ExecuteScalarAsync());
        Assert.False(string.IsNullOrWhiteSpace(trackedGroupId));
        Assert.Equal(
            session.Id,
            (await store.GetLinkAsync(trackedGroupId!, "poll-existing", "option-t6"))?.SessionId);
        Assert.Equal(
            session.Id,
            (await store.GetLinkAsync(trackedGroupId!, "poll-existing", "option-cn"))?.SessionId);
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
