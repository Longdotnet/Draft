using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class FailedShareRollbackStateFuzzTests
{
    private const string Fingerprint = "slot-lifecycle:failed-share-must-not-leak-roster-state";

    [Fact]
    public async Task Failed_share_then_unrelated_save_must_not_resurrect_absent_partner()
    {
        await using var target = new FailedShareTarget();
        var scenario = new StatefulFuzzCase<FailedShareAction>(
            "failed-share-roster-resurrection-minimized",
            20260913,
            [
                new(FailedShareActionKind.InvalidShare),
                new(FailedShareActionKind.UnrelatedSave)
            ]);

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Stateful_failure_restart_and_save_mutations_preserve_failed_share_atomicity()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var random = new StableFuzzRandom(seed);
            var prefix = new List<FailedShareAction>();
            var noiseCount = 1 + random.NextInt(5);
            for (var index = 0; index < noiseCount; index += 1)
            {
                prefix.Add(new FailedShareAction(
                    random.NextBool()
                        ? FailedShareActionKind.Restart
                        : FailedShareActionKind.UnrelatedSave));
            }

            // Keep the known failure shape at the tail while mutating the preceding request/context
            // lifecycle. The permanent minimized reproducer above remains the two-action core.
            prefix.Add(new FailedShareAction(FailedShareActionKind.InvalidShare));
            prefix.Add(new FailedShareAction(FailedShareActionKind.UnrelatedSave));

            await using var target = new FailedShareTarget();
            var scenario = new StatefulFuzzCase<FailedShareAction>(
                $"failed-share-roster-resurrection-{seed}",
                seed,
                prefix);
            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            if (!result.Failed)
                continue;

            await using var isolatedTarget = new StatefulFuzzIsolatedTarget<FailedShareState, FailedShareAction>(
                static () => new FailedShareTarget());
            var promotion = await StatefulFuzzPromotion.PrepareAsync(
                scenario,
                isolatedTarget,
                confirmationRuns: 3);

            Assert.True(promotion.IsPromotable, $"{Describe(result)}; unstable failure");
            Assert.Equal(Fingerprint, promotion.FailureFingerprint);
            Assert.False(result.Failed, $"{Describe(result)}; minimizedReproducer={promotion.SerializePermanentReproducer()}");
        }
    }

    private static string Describe(StatefulFuzzRunResult<FailedShareAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum FailedShareActionKind
    {
        InvalidShare,
        UnrelatedSave,
        Restart
    }

    internal sealed record FailedShareAction(FailedShareActionKind Kind);

    internal sealed class FailedShareState : IAsyncDisposable
    {
        private readonly string connectionString = $"Data Source=failed-share-fuzz-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection anchorConnection;
        private DbContextOptions<VolleyDraftDbContext> options = null!;

        public FailedShareState()
        {
            anchorConnection = new SqliteConnection(connectionString);
            anchorConnection.Open();
            options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;
            Db = new VolleyDraftDbContext(options);
            SeedAsync().GetAwaiter().GetResult();
        }

        public VolleyDraftDbContext Db { get; private set; }
        public string AdminId { get; private set; } = string.Empty;
        public string SessionId { get; private set; } = string.Empty;
        public string ReturningId { get; private set; } = string.Empty;
        public int FailedShareCount { get; set; }
        public bool? DurableReturningPresent { get; set; }

        public async Task RestartAsync()
        {
            await Db.DisposeAsync();
            Db = new VolleyDraftDbContext(options);
        }

        public async Task CaptureDurablePresenceAsync(CancellationToken cancellationToken)
        {
            await using var verifier = new VolleyDraftDbContext(options);
            DurableReturningPresent = await verifier.SessionPlayers
                .AsNoTracking()
                .Where(player => player.Id == ReturningId)
                .Select(player => player.IsPresent)
                .SingleAsync(cancellationToken);
        }

        private async Task SeedAsync()
        {
            await Db.Database.EnsureCreatedAsync();
            AdminId = $"fuzz-failed-share-admin-{Guid.NewGuid():N}";
            Db.Users.Add(new User
            {
                Id = AdminId,
                DisplayName = "Fuzz Failed Share Admin",
                Email = $"{AdminId}@example.test",
                PasswordHash = "test"
            });
            await Db.SaveChangesAsync();

            var service = new SessionDraftService(Db);
            var created = await service.CreateSessionAsync(AdminId, new CreateSessionRequest("Failed share fuzz", 3, 2));
            if (!created.IsSuccess)
                throw new InvalidOperationException(created.Error);
            SessionId = created.Value!.Id;

            var anchor = await service.AddPlayerAsync(
                AdminId,
                SessionId,
                new AddPlayerRequest("Anchor", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
            if (!anchor.IsSuccess)
                throw new InvalidOperationException(anchor.Error);

            var returning = await service.AddPlayerAsync(
                AdminId,
                SessionId,
                new AddPlayerRequest("Returning", PlayerRole.New, PlayerLevel.New, PlayerGender.Male, IsPresent: false));
            if (!returning.IsSuccess)
                throw new InvalidOperationException(returning.Error);
            ReturningId = returning.Value!.Id;
            await CaptureDurablePresenceAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await anchorConnection.DisposeAsync();
        }
    }

    internal sealed class FailedShareTarget :
        IStatefulFuzzTarget<FailedShareState, FailedShareAction>,
        IAsyncDisposable
    {
        public string Name => "failed-share-rollback-state";
        public FailedShareState? LastState { get; private set; }

        public FailedShareState CreateState(StatefulFuzzCase<FailedShareAction> scenario)
        {
            LastState = new FailedShareState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            FailedShareState state,
            FailedShareAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (action.Kind == FailedShareActionKind.Restart)
            {
                await state.RestartAsync();
                await state.CaptureDurablePresenceAsync(cancellationToken);
                return;
            }

            var service = new SessionDraftService(state.Db);
            if (action.Kind == FailedShareActionKind.InvalidShare)
            {
                var result = await service.SharePreDraftSlotAsync(
                    state.AdminId,
                    state.SessionId,
                    "Anchor",
                    [
                        new ShareSlotParticipantInput("Returning"),
                        new ShareSlotParticipantInput("Anchor")
                    ]);
                if (result.IsSuccess)
                    throw new InvalidOperationException("Invalid self-share unexpectedly succeeded.");
                state.FailedShareCount += 1;
                return;
            }

            var updated = await service.UpdateSessionAsync(
                state.AdminId,
                state.SessionId,
                new UpdateSessionRequest($"Unrelated save {actionIndex}", 4));
            if (!updated.IsSuccess)
                throw new InvalidOperationException(updated.Error);
            await state.CaptureDurablePresenceAsync(cancellationToken);
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(FailedShareState state)
        {
            if (state.FailedShareCount > 0 && state.DurableReturningPresent == true)
            {
                yield return new StatefulInvariantViolation(
                    "slot-lifecycle",
                    "failed-share-must-not-leak-roster-state",
                    "A rejected share command leaked tracked IsPresent=true and a later unrelated SaveChanges persisted the absent member.",
                    Fingerprint);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
