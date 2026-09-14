using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class CreateSharedSlotStaleContextRaceFuzzTests
{
    private const string Fingerprint = "slot-lifecycle:create-shared-slot-stale-context-double-membership";

    [Fact]
    public async Task Stale_direct_create_cannot_attach_one_player_to_two_shared_slots()
    {
        await using var state = await RaceState.CreateAsync();

        await using var stale = state.CreateContext();
        var stalePartner = await stale.SessionPlayers.SingleAsync(player => player.Id == state.PartnerId);
        var staleAnchorB = await stale.SessionPlayers.SingleAsync(player => player.Id == state.AnchorBId);
        Assert.False(stalePartner.IsInsideSharedSlot);
        Assert.False(staleAnchorB.IsInsideSharedSlot);

        await using (var winner = state.CreateContext())
        {
            var winnerService = new SessionDraftService(winner);
            var first = await winnerService.CreateSharedSlotAsync(
                state.AdminId,
                state.SessionId,
                new CreateSharedSlotRequest([state.AnchorAId, state.PartnerId], PlayerRole.FullStack));
            Assert.True(first.IsSuccess, first.Error);
        }

        // The stale request already tracks Partner=false. EF identity resolution preserves
        // that stale value when CreateSharedSlotAsync queries the roster again, so a plain
        // tracked-state check must not be allowed to authorize a second durable membership.
        var staleService = new SessionDraftService(stale);
        var second = await staleService.CreateSharedSlotAsync(
            state.AdminId,
            state.SessionId,
            new CreateSharedSlotRequest([state.AnchorBId, state.PartnerId], PlayerRole.FullStack));

        Assert.False(
            second.IsSuccess,
            $"{Fingerprint}: stale direct create was accepted after another request already claimed the partner.");

        // A losing multi-row claim may have tentatively claimed Anchor B before discovering that
        // Partner was already owned. Saving unrelated caller work afterwards must not resurrect
        // that rolled-back claim through stale tracked state.
        staleAnchorB.IsCaptainEligible = !staleAnchorB.IsCaptainEligible;
        await stale.SaveChangesAsync();

        await using var verifier = state.CreateContext();
        var partnerSharedSlotCount = await verifier.DraftSlotPlayers
            .AsNoTracking()
            .Where(link => link.SessionPlayerId == state.PartnerId &&
                           link.DraftSlot.SessionId == state.SessionId &&
                           link.DraftSlot.Type == DraftSlotType.Shared)
            .Select(link => link.DraftSlotId)
            .Distinct()
            .CountAsync();
        var durableAnchorB = await verifier.SessionPlayers
            .AsNoTracking()
            .SingleAsync(player => player.Id == state.AnchorBId);

        Assert.True(
            partnerSharedSlotCount == 1,
            $"{Fingerprint}: expected exactly one authoritative shared slot, found {partnerSharedSlotCount}.");
        Assert.False(
            durableAnchorB.IsInsideSharedSlot,
            $"{Fingerprint}: losing request leaked a rolled-back Anchor B shared-slot claim into a later save.");
    }

    private sealed class RaceState : IAsyncDisposable
    {
        private readonly string connectionString = $"Data Source=create-shared-slot-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection anchorConnection;
        private readonly DbContextOptions<VolleyDraftDbContext> options;

        private RaceState()
        {
            anchorConnection = new SqliteConnection(connectionString);
            anchorConnection.Open();
            options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;
        }

        public string AdminId { get; private set; } = string.Empty;
        public string SessionId { get; private set; } = string.Empty;
        public string AnchorAId { get; private set; } = string.Empty;
        public string AnchorBId { get; private set; } = string.Empty;
        public string PartnerId { get; private set; } = string.Empty;

        public static async Task<RaceState> CreateAsync()
        {
            var state = new RaceState();
            await state.SeedAsync();
            return state;
        }

        public VolleyDraftDbContext CreateContext() => new(options);

        private async Task SeedAsync()
        {
            await using var db = CreateContext();
            await db.Database.EnsureCreatedAsync();

            AdminId = $"fuzz-create-shared-slot-race-admin-{Guid.NewGuid():N}";
            db.Users.Add(new User
            {
                Id = AdminId,
                DisplayName = "Fuzz Create Shared Slot Race Admin",
                Email = $"{AdminId}@example.test",
                PasswordHash = "test"
            });
            await db.SaveChangesAsync();

            var service = new SessionDraftService(db);
            var created = await service.CreateSessionAsync(
                AdminId,
                new CreateSessionRequest("Create shared slot stale race fuzz", 3, 2));
            if (!created.IsSuccess)
                throw new InvalidOperationException(created.Error);
            SessionId = created.Value!.Id;

            foreach (var (name, gender) in new[]
                     {
                         ("Anchor A", PlayerGender.Male),
                         ("Anchor B", PlayerGender.Female),
                         ("Partner", PlayerGender.Male)
                     })
            {
                var added = await service.AddPlayerAsync(
                    AdminId,
                    SessionId,
                    new AddPlayerRequest(name, PlayerRole.New, PlayerLevel.New, gender));
                if (!added.IsSuccess)
                    throw new InvalidOperationException(added.Error);

                if (name == "Anchor A") AnchorAId = added.Value!.Id;
                else if (name == "Anchor B") AnchorBId = added.Value!.Id;
                else PartnerId = added.Value!.Id;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await anchorConnection.DisposeAsync();
        }
    }
}
