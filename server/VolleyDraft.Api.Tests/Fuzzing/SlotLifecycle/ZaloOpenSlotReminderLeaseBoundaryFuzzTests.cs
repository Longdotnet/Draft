using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloOpenSlotReminderLeaseBoundaryFuzzTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Reminder_lease_is_reacquirable_at_the_exact_durable_expiry_boundary()
    {
        await using var target = new ReminderLeaseBoundaryTarget();
        var scenario = new StatefulFuzzCase<LeaseAction>(
            "slot-reminder-lease-exact-expiry",
            20260909,
            [
                new(LeaseActionKind.AcquireInitial, 0),
                new(LeaseActionKind.TryTakeover, -1),
                new(LeaseActionKind.TryTakeover, 0)
            ]);

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Temporal_mutations_preserve_closed_open_lease_boundary()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var random = new StableFuzzRandom(seed);
            var deltaTicks = random.Pick(new long[] { -1, 0, 1, TimeSpan.TicksPerMillisecond });
            await using var target = new ReminderLeaseBoundaryTarget();
            var scenario = new StatefulFuzzCase<LeaseAction>(
                $"slot-reminder-lease-boundary-{seed}",
                seed,
                [
                    new(LeaseActionKind.AcquireInitial, 0),
                    new(LeaseActionKind.TryTakeover, deltaTicks)
                ]);

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static string Describe(StatefulFuzzRunResult<LeaseAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => $"{action.Kind}:{action.DeltaTicks}"))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum LeaseActionKind
    {
        AcquireInitial,
        TryTakeover
    }

    internal sealed record LeaseAction(LeaseActionKind Kind, long DeltaTicks);

    internal sealed class LeaseState : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        public LeaseState()
        {
            connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            Db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            Db.Database.EnsureCreated();
            Store = new ZaloOpenSlotOfferStore(Db);
        }

        public VolleyDraftDbContext Db { get; }
        public ZaloOpenSlotOfferStore Store { get; }
        public ZaloOpenSlotOfferSnapshot? Offer { get; set; }
        public DateTimeOffset LeaseStartedAt { get; } = new(2026, 9, 9, 5, 0, 0, TimeSpan.Zero);
        public bool? LastTakeoverResult { get; set; }
        public long? LastTakeoverDeltaTicks { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal sealed class ReminderLeaseBoundaryTarget :
        IStatefulFuzzTarget<LeaseState, LeaseAction>,
        IAsyncDisposable
    {
        public string Name => "slot-reminder-lease-boundary";
        public LeaseState? LastState { get; private set; }

        public LeaseState CreateState(StatefulFuzzCase<LeaseAction> scenario)
        {
            LastState = new LeaseState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            LeaseState state,
            LeaseAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            state.Offer ??= await state.Store.OpenAsync(
                "connection-a",
                "group-a",
                "owner-a",
                "Owner A",
                "session-a",
                "T6",
                "source-message",
                DateTimeOffset.UtcNow.AddHours(2),
                DateTimeOffset.UtcNow.AddMinutes(45),
                cancellationToken);

            if (action.Kind == LeaseActionKind.AcquireInitial)
            {
                var acquired = await state.Store.TryAcquireReminderLeaseAsync(
                    state.Offer,
                    "lease-a",
                    state.LeaseStartedAt,
                    LeaseDuration,
                    cancellationToken);
                if (!acquired)
                    throw new InvalidOperationException("Initial reminder lease must be acquired for the seed state.");
                return;
            }

            var takeoverAt = state.LeaseStartedAt
                .Add(LeaseDuration)
                .AddTicks(action.DeltaTicks);
            state.LastTakeoverDeltaTicks = action.DeltaTicks;
            state.LastTakeoverResult = await state.Store.TryAcquireReminderLeaseAsync(
                state.Offer,
                "lease-b",
                takeoverAt,
                LeaseDuration,
                cancellationToken);
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(LeaseState state)
        {
            if (state.LastTakeoverResult is not { } actual || state.LastTakeoverDeltaTicks is not { } deltaTicks)
                yield break;

            var expected = deltaTicks >= 0;
            if (actual == expected)
                yield break;

            yield return new StatefulInvariantViolation(
                "slot-lifecycle",
                "reminder-lease-expiry-boundary",
                $"A reminder lease must be unavailable before expiry and reacquirable at or after expiry; deltaTicks={deltaTicks}, acquired={actual}.",
                "slot-lifecycle:reminder-lease-expiry-boundary");
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
