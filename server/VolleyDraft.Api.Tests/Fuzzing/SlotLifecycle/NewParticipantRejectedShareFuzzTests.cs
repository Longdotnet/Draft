using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class NewParticipantRejectedShareFuzzTests
{
    private const string NewIdentityFingerprint = "slot-lifecycle:failed-share-must-not-leak-new-identity";

    [Fact]
    public async Task Rejected_plus_two_share_must_not_persist_new_profile_or_player_after_later_save()
    {
        await using var target = new NewParticipantRollbackTarget();
        var scenario = new StatefulFuzzCase<RollbackAction>(
            "failed-share-new-identity-minimized",
            20260913,
            [
                new(RollbackActionKind.InvalidShare),
                new(RollbackActionKind.UnrelatedSave)
            ]);

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Rejected_share_must_roll_back_existing_profile_enrichment()
    {
        await using var state = new RollbackState(seedExistingProfile: true);
        var before = await state.Db.PlayerProfiles.AsNoTracking()
            .SingleAsync(profile => profile.ZaloUserId == state.PartnerUid);

        var service = new SessionDraftService(state.Db);
        var rejected = await service.SharePreDraftSlotAsync(
            state.AdminId,
            state.SessionId,
            "Anchor",
            [
                new ShareSlotParticipantInput("Existing Profile", state.PartnerUid, "https://example.test/new-avatar.jpg"),
                new ShareSlotParticipantInput("Anchor")
            ]);

        Assert.False(rejected.IsSuccess);
        await service.UpdateSessionAsync(
            state.AdminId,
            state.SessionId,
            new UpdateSessionRequest("Unrelated save after rejected profile enrichment", 4));
        await state.RestartAsync();

        var after = await state.Db.PlayerProfiles.AsNoTracking()
            .SingleAsync(profile => profile.ZaloUserId == state.PartnerUid);
        Assert.Equal(before.AvatarUrl, after.AvatarUrl);
        Assert.Equal(before.DefaultRole, after.DefaultRole);
        Assert.Equal(before.DefaultLevel, after.DefaultLevel);
        Assert.Equal(before.LastSyncedAt, after.LastSyncedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.False(await state.Db.SessionPlayers.AsNoTracking()
            .AnyAsync(player => player.SessionId == state.SessionId && player.PlayerProfileId == after.Id));
    }

    [Fact]
    public async Task Stateful_restart_and_save_mutations_preserve_rejected_new_identity_atomicity()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var random = new StableFuzzRandom(seed);
            var actions = new List<RollbackAction>();
            var prefixCount = random.NextInt(5);
            for (var index = 0; index < prefixCount; index += 1)
            {
                actions.Add(new RollbackAction(
                    random.NextBool()
                        ? RollbackActionKind.Restart
                        : RollbackActionKind.UnrelatedSave));
            }

            actions.Add(new RollbackAction(RollbackActionKind.InvalidShare));
            if (random.NextBool())
                actions.Add(new RollbackAction(RollbackActionKind.Restart));
            actions.Add(new RollbackAction(RollbackActionKind.UnrelatedSave));

            await using var target = new NewParticipantRollbackTarget();
            var scenario = new StatefulFuzzCase<RollbackAction>(
                $"failed-share-new-identity-{seed}",
                seed,
                actions);
            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            if (!result.Failed)
                continue;

            await using var isolatedTarget = new StatefulFuzzIsolatedTarget<RollbackState, RollbackAction>(
                static () => new NewParticipantRollbackTarget());
            var promotion = await StatefulFuzzPromotion.PrepareAsync(
                scenario,
                isolatedTarget,
                confirmationRuns: 3);

            Assert.True(promotion.IsPromotable, $"{Describe(result)}; unstable failure");
            Assert.Equal(NewIdentityFingerprint, promotion.FailureFingerprint);
            Assert.False(result.Failed, $"{Describe(result)}; minimizedReproducer={promotion.SerializePermanentReproducer()}");
        }
    }

    private static string Describe(StatefulFuzzRunResult<RollbackAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum RollbackActionKind
    {
        InvalidShare,
        UnrelatedSave,
        Restart
    }

    internal sealed record RollbackAction(RollbackActionKind Kind);

    internal sealed class RollbackState : IAsyncDisposable
    {
        private readonly string connectionString = $"Data Source=failed-share-new-identity-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection anchorConnection;
        private DbContextOptions<VolleyDraftDbContext> options = null!;

        public RollbackState(bool seedExistingProfile = false)
        {
            anchorConnection = new SqliteConnection(connectionString);
            anchorConnection.Open();
            options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;
            Db = new VolleyDraftDbContext(options);
            SeedAsync(seedExistingProfile).GetAwaiter().GetResult();
        }

        public VolleyDraftDbContext Db { get; private set; }
        public string AdminId { get; private set; } = string.Empty;
        public string SessionId { get; private set; } = string.Empty;
        public string PartnerUid { get; } = $"uid-new-partner-{Guid.NewGuid():N}";
        public int FailedShareCount { get; set; }
        public bool DurableProfileExists { get; private set; }
        public bool DurablePlayerExists { get; private set; }

        public async Task RestartAsync()
        {
            await Db.DisposeAsync();
            Db = new VolleyDraftDbContext(options);
        }

        public async Task CaptureDurableIdentityAsync(CancellationToken cancellationToken)
        {
            await using var verifier = new VolleyDraftDbContext(options);
            DurableProfileExists = await verifier.PlayerProfiles.AsNoTracking()
                .AnyAsync(profile => profile.ZaloUserId == PartnerUid, cancellationToken);
            DurablePlayerExists = await verifier.SessionPlayers.AsNoTracking()
                .AnyAsync(player => player.SessionId == SessionId && player.PlayerProfile != null && player.PlayerProfile.ZaloUserId == PartnerUid, cancellationToken);
        }

        private async Task SeedAsync(bool seedExistingProfile)
        {
            await Db.Database.EnsureCreatedAsync();
            AdminId = $"fuzz-new-identity-admin-{Guid.NewGuid():N}";
            Db.Users.Add(new User
            {
                Id = AdminId,
                DisplayName = "Fuzz New Identity Admin",
                Email = $"{AdminId}@example.test",
                PasswordHash = "test"
            });
            if (seedExistingProfile)
            {
                Db.PlayerProfiles.Add(new PlayerProfile
                {
                    ZaloUserId = PartnerUid,
                    DisplayName = "Existing Profile",
                    AvatarUrl = null,
                    Gender = null,
                    DefaultRole = null,
                    DefaultLevel = null,
                    LastSyncedAt = DateTimeOffset.UnixEpoch,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    UpdatedAt = DateTimeOffset.UnixEpoch
                });
            }
            await Db.SaveChangesAsync();

            var service = new SessionDraftService(Db);
            var created = await service.CreateSessionAsync(AdminId, new CreateSessionRequest("New identity rollback fuzz", 3, 2));
            if (!created.IsSuccess)
                throw new InvalidOperationException(created.Error);
            SessionId = created.Value!.Id;

            var anchor = await service.AddPlayerAsync(
                AdminId,
                SessionId,
                new AddPlayerRequest("Anchor", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
            if (!anchor.IsSuccess)
                throw new InvalidOperationException(anchor.Error);

            await CaptureDurableIdentityAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await anchorConnection.DisposeAsync();
        }
    }

    internal sealed class NewParticipantRollbackTarget :
        IStatefulFuzzTarget<RollbackState, RollbackAction>,
        IAsyncDisposable
    {
        public string Name => "failed-share-new-identity-rollback";
        public RollbackState? LastState { get; private set; }

        public RollbackState CreateState(StatefulFuzzCase<RollbackAction> scenario)
        {
            LastState = new RollbackState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            RollbackState state,
            RollbackAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action.Kind == RollbackActionKind.Restart)
            {
                await state.RestartAsync();
                await state.CaptureDurableIdentityAsync(cancellationToken);
                return;
            }

            var service = new SessionDraftService(state.Db);
            if (action.Kind == RollbackActionKind.InvalidShare)
            {
                var result = await service.SharePreDraftSlotAsync(
                    state.AdminId,
                    state.SessionId,
                    "Anchor",
                    [
                        new ShareSlotParticipantInput("New Partner", state.PartnerUid, "https://example.test/new-partner.jpg"),
                        new ShareSlotParticipantInput("Anchor")
                    ]);
                if (result.IsSuccess)
                    throw new InvalidOperationException("Invalid +2 self-share unexpectedly succeeded.");
                state.FailedShareCount += 1;
                return;
            }

            var updated = await service.UpdateSessionAsync(
                state.AdminId,
                state.SessionId,
                new UpdateSessionRequest($"Unrelated save {actionIndex}", 4));
            if (!updated.IsSuccess)
                throw new InvalidOperationException(updated.Error);
            await state.CaptureDurableIdentityAsync(cancellationToken);
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(RollbackState state)
        {
            if (state.FailedShareCount > 0 && (state.DurableProfileExists || state.DurablePlayerExists))
            {
                yield return new StatefulInvariantViolation(
                    "slot-lifecycle",
                    "failed-share-must-not-leak-new-identity",
                    "A rejected +2 share leaked a new PlayerProfile/SessionPlayer into durable state after restart or unrelated save.",
                    NewIdentityFingerprint);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
