using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOpenSlotPreDraftFinalizeConcurrencyTests
{
    [Fact]
    public async Task Finalize_pre_draft_claim_reports_success_only_after_both_ledger_transitions_win()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn",
            "g1",
            "owner",
            "Hoàng Nguyên",
            "s1",
            "T6",
            "m-open",
            DateTimeOffset.UtcNow.AddHours(2),
            DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(
            offer,
            "claimant",
            "Vivian",
            "m-claim",
            DateTimeOffset.UtcNow.AddMinutes(20)));

        var service = new ZaloOpenSlotOfferService(fixture.Db);
        var result = await service.FinalizePreDraftClaimAsync(
            offer.Id,
            "claimant",
            "T6",
            "Vivian",
            "Hoàng Nguyên");

        Assert.True(result.Handled);
        Assert.Contains("coi như chốt", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.LoadPendingClaimAsync("conn", "g1", "claimant"));
        Assert.Empty(await store.ListClaimableAsync("conn", "g1", "another"));
    }

    [Fact]
    public async Task Finalize_pre_draft_claim_does_not_lie_when_claim_CAS_lost_to_owner_cancel()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var offer = await store.OpenAsync(
            "conn",
            "g1",
            "owner",
            "Hoàng Nguyên",
            "s1",
            "T6",
            "m-open",
            DateTimeOffset.UtcNow.AddHours(2),
            DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.True(await store.TryClaimAsync(
            offer,
            "claimant",
            "Vivian",
            "m-claim",
            DateTimeOffset.UtcNow.AddMinutes(20)));

        // Models the concurrency window after roster verification but before the
        // claimant's ClaimPending -> Applying CAS: the owner wins a cancel first.
        Assert.True(await store.CancelAsync(offer.Id, "owner"));

        var service = new ZaloOpenSlotOfferService(fixture.Db);
        var result = await service.FinalizePreDraftClaimAsync(
            offer.Id,
            "claimant",
            "T6",
            "Vivian",
            "Hoàng Nguyên");

        Assert.True(result.Handled);
        Assert.DoesNotContain("coi như chốt", result.Response ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("đổi trạng thái", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.LoadPendingClaimAsync("conn", "g1", "claimant"));
        Assert.Empty(await store.ListClaimableAsync("conn", "g1", "another"));
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
