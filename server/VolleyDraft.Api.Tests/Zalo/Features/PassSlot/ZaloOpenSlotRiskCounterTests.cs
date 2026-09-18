using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOpenSlotRiskCounterTests
{
    [Fact]
    public async Task Open_offer_remains_a_risk_even_when_owner_is_not_in_current_roster()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn-1",
            "g1",
            "owner-uid",
            "Hoàng",
            "s1",
            "T6",
            "m-pass",
            DateTimeOffset.UtcNow.AddHours(1),
            null);

        // Deliberately do not create a SessionPlayer for owner-uid. This models the
        // pre-draft handoff after the owner follows NPC guidance and removes their vote,
        // while the replacement has not completed the claim yet.
        var count = await new ZaloOpenSlotRiskCounter(fixture.Db)
            .CountActiveForSessionAsync("conn-1", "g1", "s1");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Draft_readiness_keeps_pass_offer_in_telemetry_without_hiding_real_roster_blocker()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admin = new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = "admin@example.test",
            PasswordHash = "test"
        };
        var connection = new ZaloConnection
        {
            Id = "conn-1",
            AdminUserId = admin.Id,
            AccountZaloId = "account-1",
            DisplayName = "NPC",
            EncryptedCredentials = "test"
        };
        var session = new MatchSession
        {
            Id = "s1",
            Name = "T6",
            AdminUserId = admin.Id,
            ZaloConnectionId = connection.Id,
            ZaloGroupId = "g1",
            BotEnabled = true,
            StartTime = DateTimeOffset.UtcNow.AddHours(5),
            Status = SessionStatus.Setup,
            TeamCount = 3,
            TeamSize = 6
        };
        fixture.Db.Users.Add(admin);
        fixture.Db.ZaloConnections.Add(connection);
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();

        var before = await new ZaloDraftReadinessService(fixture.Db).BuildAsync(session.Id);
        Assert.NotNull(before);
        Assert.Equal(0, before!.ActivePassSlotRiskCount);
        Assert.Equal(ZaloDraftReadinessState.NoRoster, before.State);

        await new ZaloOpenSlotOfferStore(fixture.Db).OpenAsync(
            connection.Id,
            session.ZaloGroupId!,
            "owner-uid",
            "Hoàng",
            session.Id,
            session.Name,
            "m-pass",
            DateTimeOffset.UtcNow.AddHours(1),
            null);

        // The owner is intentionally absent from SessionPlayers, matching the real
        // handoff window after they remove their vote. The ledger remains visible, but
        // readiness must report the actual blocker (empty roster), not the pass offer.
        var after = await new ZaloDraftReadinessService(fixture.Db).BuildAsync(session.Id);

        Assert.NotNull(after);
        Assert.Equal(1, after!.ActivePassSlotRiskCount);
        Assert.Equal(ZaloDraftReadinessState.NoRoster, after.State);
        Assert.Equal("draft_blocked_roster_empty", after.ReasonCode);
        Assert.False(after.IsRosterReady);
        Assert.False(after.CanEscalate);
    }

    [Fact]
    public async Task Active_pass_offer_does_not_block_draft_when_authoritative_roster_is_valid()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admin = new User
        {
            Id = "draft-admin",
            DisplayName = "Admin",
            Email = "draft-admin@example.test",
            PasswordHash = "test"
        };
        var connection = new ZaloConnection
        {
            Id = "draft-conn",
            AdminUserId = admin.Id,
            AccountZaloId = "draft-account",
            DisplayName = "NPC",
            EncryptedCredentials = "test"
        };
        fixture.Db.Users.Add(admin);
        fixture.Db.ZaloConnections.Add(connection);
        await fixture.Db.SaveChangesAsync();

        var draftService = new SessionDraftService(fixture.Db);
        var created = await draftService.CreateSessionAsync(
            admin.Id,
            new CreateSessionRequest("UTE tuần sau", 3, 2));
        Assert.True(created.IsSuccess, created.Error);

        var session = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == created.Value!.Id);
        session.ZaloConnectionId = connection.Id;
        session.ZaloGroupId = "draft-group";
        session.BotEnabled = true;
        session.StartTime = DateTimeOffset.UtcNow.AddDays(1);
        await fixture.Db.SaveChangesAsync();

        var playerIds = new List<string>();
        for (var index = 1; index <= 6; index += 1)
        {
            var added = await draftService.AddPlayerAsync(
                admin.Id,
                session.Id,
                new AddPlayerRequest(
                    $"P{index}",
                    PlayerRole.New,
                    PlayerLevel.New,
                    PlayerGender.Male));
            Assert.True(added.IsSuccess, added.Error);
            playerIds.Add(added.Value!.Id);
        }

        var captains = await draftService.SetManualCaptainsAsync(
            admin.Id,
            session.Id,
            new ManualCaptainsRequest(playerIds.Take(3).ToList()));
        Assert.True(captains.IsSuccess, captains.Error);

        await new ZaloOpenSlotOfferStore(fixture.Db).OpenAsync(
            connection.Id,
            session.ZaloGroupId!,
            "owner-uid",
            "Người đang pass",
            session.Id,
            session.Name,
            "m-pass-active",
            DateTimeOffset.UtcNow.AddHours(2),
            null);

        var readiness = await new ZaloDraftReadinessService(fixture.Db).BuildAsync(session.Id);
        Assert.NotNull(readiness);
        Assert.Equal(1, readiness!.ActivePassSlotRiskCount);
        Assert.Equal(ZaloDraftReadinessState.Ready, readiness.State);
        Assert.True(readiness.IsRosterReady);
        Assert.True(readiness.CanEscalate);

        var drafted = await draftService.StartDraftAsync(admin.Id, session.Id);

        Assert.True(drafted.IsSuccess, drafted.Error);
        Assert.Equal(
            SessionStatus.Drafting,
            await fixture.Db.MatchSessions
                .Where(item => item.Id == session.Id)
                .Select(item => item.Status)
                .SingleAsync());
        Assert.Equal(
            1,
            await fixture.Db.DraftRounds.CountAsync(item => item.SessionId == session.Id));
        Assert.Equal(
            1,
            await new ZaloOpenSlotRiskCounter(fixture.Db)
                .CountActiveForSessionAsync(connection.Id, session.ZaloGroupId!, session.Id));
    }

    [Fact]
    public async Task Counter_joins_the_current_ef_transaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn-1",
            "g1",
            "owner-uid",
            "Hoàng",
            "s1",
            "T6",
            "m-pass",
            DateTimeOffset.UtcNow.AddHours(1),
            null);

        await using var transaction = await fixture.Db.Database.BeginTransactionAsync();

        var count = await new ZaloOpenSlotRiskCounter(fixture.Db)
            .CountActiveForSessionAsync("conn-1", "g1", "s1");

        Assert.Equal(1, count);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Claim_pending_and_applying_stay_risky_until_offer_completes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn-1",
            "g1",
            "owner-uid",
            "Hoàng",
            "s1",
            "T6",
            "m-pass",
            DateTimeOffset.UtcNow.AddHours(1),
            null);
        var counter = new ZaloOpenSlotRiskCounter(fixture.Db);

        Assert.True(await store.TryClaimAsync(offer, "claimant-uid", "Vivian", "m-claim"));
        Assert.Equal(1, await counter.CountActiveForSessionAsync("conn-1", "g1", "s1"));

        Assert.True(await store.TryBeginApplyAsync(offer.Id, "claimant-uid"));
        Assert.Equal(1, await counter.CountActiveForSessionAsync("conn-1", "g1", "s1"));

        Assert.True(await store.CompleteAsync(offer.Id, "claimant-uid"));
        Assert.Equal(0, await counter.CountActiveForSessionAsync("conn-1", "g1", "s1"));
    }

    [Fact]
    public async Task Risk_count_is_scoped_to_connection_group_and_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await store.OpenAsync(
            "conn-1", "g1", "owner-1", "A", "s1", "T6", "m1",
            DateTimeOffset.UtcNow.AddHours(1), null);
        await store.OpenAsync(
            "conn-1", "g1", "owner-2", "B", "s2", "CN", "m2",
            DateTimeOffset.UtcNow.AddHours(1), null);
        await store.OpenAsync(
            "conn-2", "g1", "owner-3", "C", "s1", "T6 other account", "m3",
            DateTimeOffset.UtcNow.AddHours(1), null);

        var counter = new ZaloOpenSlotRiskCounter(fixture.Db);

        Assert.Equal(1, await counter.CountActiveForSessionAsync("conn-1", "g1", "s1"));
        Assert.Equal(1, await counter.CountActiveForSessionAsync("conn-1", "g1", "s2"));
        Assert.Equal(1, await counter.CountActiveForSessionAsync("conn-2", "g1", "s1"));
        Assert.Equal(0, await counter.CountActiveForSessionAsync("conn-1", "other-group", "s1"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
