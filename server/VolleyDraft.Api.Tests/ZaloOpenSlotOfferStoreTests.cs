using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOpenSlotOfferStoreTests
{
    [Fact]
    public async Task Open_offer_is_claimable_by_another_member_but_not_owner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);

        await store.OpenAsync(
            "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m1",
            DateTimeOffset.UtcNow.AddHours(1));

        Assert.Empty(await store.ListClaimableAsync("g1", "owner"));
        var offers = await store.ListClaimableAsync("g1", "claimant");
        var offer = Assert.Single(offers);
        Assert.Equal("Hoàng Nguyên", offer.OwnerDisplayName);
        Assert.Equal(ZaloOpenSlotOfferStatus.Open, offer.Status);
    }

    [Fact]
    public async Task First_claim_wins_and_release_makes_offer_claimable_again()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "g1", "owner", "Hoàng", "s1", "T6", "m1",
            DateTimeOffset.UtcNow.AddHours(1));

        Assert.True(await store.TryClaimAsync(offer, "u1", "Vivian", "claim-1"));
        Assert.False(await store.TryClaimAsync(offer, "u2", "Long", "claim-2"));

        var pending = await store.LoadPendingClaimAsync("g1", "u1");
        Assert.NotNull(pending);
        Assert.Equal(ZaloOpenSlotOfferStatus.ClaimPending, pending!.Status);

        Assert.True(await store.ReleaseClaimAsync(offer.Id, "u1"));
        var reopened = Assert.Single(await store.ListClaimableAsync("g1", "u2"));
        Assert.Equal(offer.Id, reopened.Id);
    }

    [Fact]
    public async Task Applying_then_complete_removes_offer_from_active_claim_paths()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "g1", "owner", "Hoàng", "s1", "T6", "m1",
            DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(await store.TryClaimAsync(offer, "u1", "Vivian", "claim-1"));

        Assert.True(await store.TryBeginApplyAsync(offer.Id, "u1"));
        Assert.True(await store.CompleteAsync(offer.Id, "u1"));

        Assert.Null(await store.LoadPendingClaimAsync("g1", "u1"));
        Assert.Empty(await store.ListClaimableAsync("g1", "u2"));
        Assert.Empty(await store.ListOwnedActiveAsync("g1", "owner"));
    }

    [Fact]
    public async Task Reopening_same_owner_session_clears_old_claim_and_increments_version()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var first = await store.OpenAsync(
            "g1", "owner", "Hoàng", "s1", "T6", "m1",
            DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(first, "u1", "Vivian", "claim-1"));

        var reopened = await store.OpenAsync(
            "g1", "owner", "Hoàng Nguyên", "s1", "T6", "m2",
            DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal(first.Id, reopened.Id);
        Assert.True(reopened.Version > first.Version);
        Assert.Equal(ZaloOpenSlotOfferStatus.Open, reopened.Status);
        Assert.Null(reopened.ClaimantZaloUserId);
        Assert.Null(await store.LoadPendingClaimAsync("g1", "u1"));
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
