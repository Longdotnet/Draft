using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionProposalStatusRaceFuzzTests
{
    private const string HistoricalFingerprint = "auto-session:created-proposal-is-terminal";
    private static readonly ProposalAction[] MinimizedReproducer =
    [
        new(ProposalActionKind.WinnerCommitsCreated),
        new(ProposalActionKind.LoserPersistsFailure)
    ];

    [Fact]
    public async Task Minimized_late_failure_reproducer_preserves_created_truth()
    {
        var scenario = new StatefulFuzzCase<ProposalAction>(
            $"{HistoricalFingerprint}-minimized",
            20260909,
            MinimizedReproducer);
        await using var target = new ProposalStatusRaceTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        Assert.Equal(ZaloPollSessionProposalStatus.Created, target.LastState!.PersistedStatus);
    }

    [Fact]
    public async Task Stateful_interleavings_preserve_created_against_stale_failure_cleanup()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                MinimizedReproducer,
                seed,
                CreateAction,
                operationCount: 6);
            var scenario = new StatefulFuzzCase<ProposalAction>(
                $"auto-session-proposal-status-race-{seed}",
                seed,
                actions);
            await using var target = new ProposalStatusRaceTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static ProposalAction CreateAction(StableFuzzRandom random) =>
        new((ProposalActionKind)random.NextInt(Enum.GetValues<ProposalActionKind>().Length));

    private static string Describe(StatefulFuzzRunResult<ProposalAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum ProposalActionKind
    {
        WinnerCommitsCreated,
        LoserPersistsFailure,
        DuplicateWinnerCommit,
        NoOp
    }

    internal sealed record ProposalAction(ProposalActionKind Kind);

    internal sealed class ProposalStatusRaceState : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required VolleyDraftDbContext Db { get; init; }
        public required ZaloAutoSessionStore Store { get; init; }
        public bool WinnerCommitted { get; set; }
        public ZaloPollSessionProposalStatus PersistedStatus { get; set; } = ZaloPollSessionProposalStatus.AwaitingApproval;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    internal sealed class ProposalStatusRaceTarget :
        IStatefulFuzzTarget<ProposalStatusRaceState, ProposalAction>, IAsyncDisposable
    {
        private readonly List<ProposalStatusRaceState> states = [];

        public string Name => "auto-session-proposal-status-race";
        public ProposalStatusRaceState? LastState { get; private set; }

        public ProposalStatusRaceState CreateState(StatefulFuzzCase<ProposalAction> scenario)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            var store = new ZaloAutoSessionStore(db);
            store.EnsureAsync().GetAwaiter().GetResult();
            var initial = Proposal(ZaloPollSessionProposalStatus.AwaitingApproval);
            var persisted = store.UpsertProposalAsync(initial).GetAwaiter().GetResult();
            var state = new ProposalStatusRaceState
            {
                Connection = connection,
                Db = db,
                Store = store,
                PersistedStatus = persisted.Status
            };
            states.Add(state);
            LastState = state;
            return state;
        }

        public async ValueTask ApplyAsync(
            ProposalStatusRaceState state,
            ProposalAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case ProposalActionKind.WinnerCommitsCreated:
                case ProposalActionKind.DuplicateWinnerCommit:
                {
                    var persisted = await state.Store.UpsertProposalAsync(
                        Proposal(ZaloPollSessionProposalStatus.Created),
                        cancellationToken);
                    state.WinnerCommitted = true;
                    state.PersistedStatus = persisted.Status;
                    break;
                }
                case ProposalActionKind.LoserPersistsFailure:
                {
                    var failed = Proposal(ZaloPollSessionProposalStatus.Failed);
                    failed.LastError = "simulated_losing_execution";
                    await ZaloAutoSessionProposalFailurePersistence.PersistUnlessCreatedAsync(
                        state.Db,
                        state.Store,
                        failed,
                        cancellationToken);
                    var persisted = await state.Store.GetProposalAsync(
                        failed.TrackedGroupId,
                        failed.PollId,
                        cancellationToken);
                    state.PersistedStatus = persisted?.Status ?? ZaloPollSessionProposalStatus.Failed;
                    break;
                }
                case ProposalActionKind.NoOp:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(ProposalStatusRaceState state)
        {
            if (state.WinnerCommitted && state.PersistedStatus != ZaloPollSessionProposalStatus.Created)
            {
                yield return new StatefulInvariantViolation(
                    "auto-session",
                    "created-proposal-is-terminal",
                    $"winner committed Created but stale failure cleanup changed durable status to {state.PersistedStatus}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var state in states)
                await state.DisposeAsync();
            states.Clear();
        }
    }

    private static ZaloPollSessionProposalData Proposal(ZaloPollSessionProposalStatus status) => new()
    {
        Id = "proposal-race",
        TrackedGroupId = "tracked-race",
        PollId = "poll-race",
        PollQuestion = "Vote lịch",
        PollCreatorId = "leader-1",
        PollUpdatedAtUnixMs = 1,
        PollStructureHash = "hash-race",
        CandidatesJson = "[]",
        ClassifierReason = "fuzz",
        Status = status,
        CreatedAt = DateTimeOffset.Parse("2026-09-09T00:00:00+00:00"),
        UpdatedAt = DateTimeOffset.Parse("2026-09-09T00:00:00+00:00")
    };
}
