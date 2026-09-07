using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
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
