using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionTerminalRestartRaceFuzzTests
{
    private const string HistoricalFingerprint = "auto-session:created-terminal-across-restart";
    private static readonly TerminalAction[] SeedCorpus =
    [
        new(TerminalActionKind.WinnerCreatesOnA),
        new(TerminalActionKind.RestartA),
        new(TerminalActionKind.LoserFailsSameRevisionOnB),
        new(TerminalActionKind.RestartB),
        new(TerminalActionKind.ReadFromBoth)
    ];

    [Fact]
    public async Task Minimized_restart_reproducer_preserves_created_truth_across_instances()
    {
        var scenario = new StatefulFuzzCase<TerminalAction>(
            $"{HistoricalFingerprint}-minimized",
            20260915,
            SeedCorpus);
        await using var target = new TerminalRestartRaceTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        Assert.True(target.LastState!.WinnerCommitted);
        Assert.Equal(ZaloPollSessionProposalStatus.Created, target.LastState.PersistedStatus);
    }

    [Fact]
    public async Task Mutated_multi_instance_restart_sequences_never_downgrade_created_to_failed()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                SeedCorpus,
                seed,
                random => new TerminalAction(
                    (TerminalActionKind)random.NextInt(Enum.GetValues<TerminalActionKind>().Length)),
                operationCount: 12);
            var scenario = new StatefulFuzzCase<TerminalAction>(
                $"auto-session-terminal-restart-race-{seed}",
                seed,
                actions);
            await using var target = new TerminalRestartRaceTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static string Describe(StatefulFuzzRunResult<TerminalAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum TerminalActionKind
    {
        WinnerCreatesOnA,
        WinnerCreatesOnB,
        DuplicateCreatedOnA,
        LoserFailsSameRevisionOnA,
        LoserFailsSameRevisionOnB,
        LoserFailsNewerRevisionOnA,
        LoserFailsNewerRevisionOnB,
        RestartA,
        RestartB,
        ReadFromBoth,
        NoOp
    }

    internal sealed record TerminalAction(TerminalActionKind Kind);

    internal sealed class TerminalRestartRaceState : IAsyncDisposable
    {
        private readonly string connectionString;

        public TerminalRestartRaceState(string connectionString, SqliteConnection anchor)
        {
            this.connectionString = connectionString;
            Anchor = anchor;
        }

        public SqliteConnection Anchor { get; }
        public required AutoSessionInstance InstanceA { get; set; }
        public required AutoSessionInstance InstanceB { get; set; }
        public bool WinnerCommitted { get; set; }
        public ZaloPollSessionProposalStatus PersistedStatus { get; set; } = ZaloPollSessionProposalStatus.AwaitingApproval;

        public AutoSessionInstance CreateInstance()
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            var store = new ZaloAutoSessionStore(db);
            store.EnsureAsync().GetAwaiter().GetResult();
            return new AutoSessionInstance(connection, db, store);
        }

        public async Task RestartAAsync()
        {
            await InstanceA.DisposeAsync();
            InstanceA = CreateInstance();
        }

        public async Task RestartBAsync()
        {
            await InstanceB.DisposeAsync();
            InstanceB = CreateInstance();
        }

        public async Task<ZaloPollSessionProposalStatus?> ReadFreshAsync()
        {
            await using var instance = CreateInstance();
            var proposal = await instance.Store.GetProposalAsync(TrackedGroupId, PollId);
            return proposal?.Status;
        }

        public async ValueTask DisposeAsync()
        {
            await InstanceA.DisposeAsync();
            await InstanceB.DisposeAsync();
            await Anchor.DisposeAsync();
        }
    }

    internal sealed class AutoSessionInstance(
        SqliteConnection connection,
        VolleyDraftDbContext db,
        ZaloAutoSessionStore store) : IAsyncDisposable
    {
        public VolleyDraftDbContext Db { get; } = db;
        public ZaloAutoSessionStore Store { get; } = store;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal sealed class TerminalRestartRaceTarget :
        IStatefulFuzzTarget<TerminalRestartRaceState, TerminalAction>, IAsyncDisposable
    {
        private readonly List<TerminalRestartRaceState> states = [];

        public string Name => "auto-session-terminal-restart-race";
        public TerminalRestartRaceState? LastState { get; private set; }

        public TerminalRestartRaceState CreateState(StatefulFuzzCase<TerminalAction> scenario)
        {
            var connectionString = $"Data Source=auto-session-terminal-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var anchor = new SqliteConnection(connectionString);
            anchor.Open();
            var state = new TerminalRestartRaceState(connectionString, anchor)
            {
                InstanceA = null!,
                InstanceB = null!
            };
            state.InstanceA = state.CreateInstance();
            state.InstanceB = state.CreateInstance();
            var initial = Proposal(ZaloPollSessionProposalStatus.AwaitingApproval, pollRevision: 10);
            state.InstanceA.Store.UpsertProposalAsync(initial).GetAwaiter().GetResult();
            states.Add(state);
            LastState = state;
            return state;
        }

        public async ValueTask ApplyAsync(
            TerminalRestartRaceState state,
            TerminalAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case TerminalActionKind.WinnerCreatesOnA:
                case TerminalActionKind.DuplicateCreatedOnA:
                    await CommitCreatedAsync(state, state.InstanceA, cancellationToken);
                    break;
                case TerminalActionKind.WinnerCreatesOnB:
                    await CommitCreatedAsync(state, state.InstanceB, cancellationToken);
                    break;
                case TerminalActionKind.LoserFailsSameRevisionOnA:
                    await PersistFailureAsync(state.InstanceA, 10, cancellationToken);
                    break;
                case TerminalActionKind.LoserFailsSameRevisionOnB:
                    await PersistFailureAsync(state.InstanceB, 10, cancellationToken);
                    break;
                case TerminalActionKind.LoserFailsNewerRevisionOnA:
                    await PersistFailureAsync(state.InstanceA, 11, cancellationToken);
                    break;
                case TerminalActionKind.LoserFailsNewerRevisionOnB:
                    await PersistFailureAsync(state.InstanceB, 11, cancellationToken);
                    break;
                case TerminalActionKind.RestartA:
                    await state.RestartAAsync();
                    break;
                case TerminalActionKind.RestartB:
                    await state.RestartBAsync();
                    break;
                case TerminalActionKind.ReadFromBoth:
                    _ = await state.InstanceA.Store.GetProposalAsync(TrackedGroupId, PollId, cancellationToken);
                    _ = await state.InstanceB.Store.GetProposalAsync(TrackedGroupId, PollId, cancellationToken);
                    break;
                case TerminalActionKind.NoOp:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            var persisted = await state.ReadFreshAsync();
            state.PersistedStatus = persisted ?? ZaloPollSessionProposalStatus.Failed;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(TerminalRestartRaceState state)
        {
            if (state.WinnerCommitted && state.PersistedStatus != ZaloPollSessionProposalStatus.Created)
            {
                yield return new StatefulInvariantViolation(
                    "auto-session",
                    "created-terminal-across-restart",
                    $"Created committed on one instance but durable status became {state.PersistedStatus} after restart/stale failure activity");
            }
        }

        private static async Task CommitCreatedAsync(
            TerminalRestartRaceState state,
            AutoSessionInstance instance,
            CancellationToken cancellationToken)
        {
            var persisted = await instance.Store.UpsertProposalAsync(
                Proposal(ZaloPollSessionProposalStatus.Created, pollRevision: 10),
                cancellationToken);
            state.WinnerCommitted = true;
            state.PersistedStatus = persisted.Status;
        }

        private static async Task PersistFailureAsync(
            AutoSessionInstance instance,
            long pollRevision,
            CancellationToken cancellationToken)
        {
            var failed = Proposal(ZaloPollSessionProposalStatus.Failed, pollRevision);
            failed.LastError = $"simulated_loser_revision_{pollRevision}";
            await ZaloAutoSessionProposalFailurePersistence.PersistUnlessCreatedAsync(
                instance.Db,
                instance.Store,
                failed,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var state in states)
                await state.DisposeAsync();
            states.Clear();
        }
    }

    private const string TrackedGroupId = "tracked-terminal-race";
    private const string PollId = "poll-terminal-race";

    private static ZaloPollSessionProposalData Proposal(
        ZaloPollSessionProposalStatus status,
        long pollRevision) => new()
    {
        Id = "proposal-terminal-race",
        TrackedGroupId = TrackedGroupId,
        PollId = PollId,
        PollQuestion = "Vote lịch terminal race",
        PollCreatorId = "leader-1",
        PollUpdatedAtUnixMs = pollRevision,
        PollStructureHash = $"hash-{pollRevision}",
        CandidatesJson = "[]",
        ClassifierReason = "terminal-restart-fuzz",
        Status = status,
        CreatedAt = DateTimeOffset.Parse("2026-09-15T00:00:00+00:00"),
        UpdatedAt = DateTimeOffset.Parse("2026-09-15T00:00:00+00:00")
    };
}
