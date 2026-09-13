using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class SharedSlotStaleContextRaceFuzzTests
{
    private const string Fingerprint = "slot-lifecycle:shared-player-must-have-one-authoritative-slot";

    [Fact]
    public async Task Stale_context_cannot_attach_one_player_to_two_shared_slots()
    {
        await using var state = await RaceState.CreateAsync();

        await using var stale = state.CreateContext();
        var stalePartner = await stale.SessionPlayers
            .SingleAsync(player => player.Id == state.PartnerId);
        Assert.False(stalePartner.IsInsideSharedSlot);

        await using (var winner = state.CreateContext())
        {
            var winnerService = new SessionDraftService(winner);
            var first = await winnerService.SharePreDraftSlotAsync(
                state.AdminId,
                state.SessionId,
                "Anchor A",
                [new ShareSlotParticipantInput("Partner")]);
            Assert.True(first.IsSuccess, first.Error);
        }

        // The second request owns an older DbContext snapshot, as can happen across
        // overlapping API/worker requests. EF identity resolution keeps the already
        // tracked Partner value instead of refreshing IsInsideSharedSlot from storage.
        var staleService = new SessionDraftService(stale);
        var second = await staleService.SharePreDraftSlotAsync(
            state.AdminId,
            state.SessionId,
            "Anchor B",
            [new ShareSlotParticipantInput("Partner")]);

        await using var verifier = state.CreateContext();
        var partnerSharedSlotCount = await verifier.DraftSlotPlayers
            .AsNoTracking()
            .Where(link => link.SessionPlayerId == state.PartnerId &&
                           link.DraftSlot.SessionId == state.SessionId &&
                           link.DraftSlot.Type == DraftSlotType.Shared)
            .Select(link => link.DraftSlotId)
            .Distinct()
            .CountAsync();

        Assert.False(
            second.IsSuccess,
            $"{Fingerprint}: stale request was accepted after another request already claimed the partner.");
        Assert.True(
            partnerSharedSlotCount == 1,
            $"{Fingerprint}: expected exactly one authoritative shared slot, found {partnerSharedSlotCount}.");
    }

    private sealed class RaceState : IAsyncDisposable
    {
        private readonly string connectionString = $"Data Source=shared-slot-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

            AdminId = $"fuzz-shared-slot-race-admin-{Guid.NewGuid():N}";
            db.Users.Add(new User
            {
                Id = AdminId,
                DisplayName = "Fuzz Shared Slot Race Admin",
                Email = $"{AdminId}@example.test",
                PasswordHash = "test"
            });
            await db.SaveChangesAsync();

            var service = new SessionDraftService(db);
            var created = await service.CreateSessionAsync(
                AdminId,
                new CreateSessionRequest("Shared slot stale race fuzz", 3, 2));
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
                if (name == "Partner")
                    PartnerId = added.Value!.Id;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await anchorConnection.DisposeAsync();
        }
    }
}
