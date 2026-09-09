using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionProposalFreshnessRaceFuzzTests
{
    private const string Fingerprint = "auto-session:proposal-source-revision-monotonic";
    private static readonly ProposalAction[] MinimizedReproducer =
    [
        new(ProposalActionKind.PersistNewerSnapshot),
        new(ProposalActionKind.PersistOlderSnapshot)
    ];

    [Fact]
    public async Task Minimized_out_of_order_poll_snapshot_preserves_newer_provenance()
    {
        var scenario = new StatefulFuzzCase<ProposalAction>(
            $"{Fingerprint}-minimized",
            20260909,
            MinimizedReproducer);
        await using var target = new ProposalFreshnessRaceTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        Assert.Equal(200, target.LastState!.PersistedPollUpdatedAtUnixMs);
        Assert.Equal("hash-newer", target.LastState.PersistedStructureHash);
        Assert.Equal(ZaloPollSessionProposalStatus.AwaitingApproval, target.LastState.PersistedStatus);
    }

    [Fact]
    public async Task Minimized_stale_failure_cleanup_cannot_poison_newer_proposal()
    {
        var scenario = new StatefulFuzzCase<ProposalAction>(
            $"{Fingerprint}-stale-failure",
            20260910,
            [
                new(ProposalActionKind.PersistNewerSnapshot),
                new(ProposalActionKind.PersistOlderFailure)
            ]);
        await using var target = new ProposalFreshnessRaceTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        Assert.Equal(ZaloPollSessionProposalStatus.AwaitingApproval, target.LastState!.PersistedStatus);
    }

    [Fact]
    public async Task Stateful_out_of_order_interleavings_never_regress_authoritative_poll_revision()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                MinimizedReproducer,
                seed,
                CreateAction,
                operationCount: 8);
            var scenario = new StatefulFuzzCase<ProposalAction>(
                $"auto-session-proposal-freshness-race-{seed}",
                seed,
                actions);
            await using var target = new ProposalFreshnessRaceTarget();

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
        PersistNewerSnapshot,
        PersistOlderSnapshot,
        PersistOlderFailure,
        DuplicateNewerSnapshot,
        NoOp
    }

    internal sealed record ProposalAction(ProposalActionKind Kind);

    internal sealed class ProposalFreshnessRaceState : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required VolleyDraftDbContext Db { get; init; }
        public required ZaloAutoSessionStore Store { get; init; }
        public bool NewerSnapshotSeen { get; set; }
        public long PersistedPollUpdatedAtUnixMs { get; set; }
        public string PersistedStructureHash { get; set; } = string.Empty;
        public ZaloPollSessionProposalStatus PersistedStatus { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    internal sealed class ProposalFreshnessRaceTarget :
        IStatefulFuzzTarget<ProposalFreshnessRaceState, ProposalAction>, IAsyncDisposable
    {
        private readonly List<ProposalFreshnessRaceState> states = [];

        public string Name => "auto-session-proposal-freshness-race";
        public ProposalFreshnessRaceState? LastState { get; private set; }

        public ProposalFreshnessRaceState CreateState(StatefulFuzzCase<ProposalAction> scenario)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            var store = new ZaloAutoSessionStore(db);
            store.EnsureAsync().GetAwaiter().GetResult();
            var state = new ProposalFreshnessRaceState
            {
                Connection = connection,
                Db = db,
                Store = store
            };
            states.Add(state);
            LastState = state;
            return state;
        }

        public async ValueTask ApplyAsync(
            ProposalFreshnessRaceState state,
            ProposalAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case ProposalActionKind.PersistNewerSnapshot:
                case ProposalActionKind.DuplicateNewerSnapshot:
                    await state.Store.UpsertProposalAsync(NewerProposal(), cancellationToken);
                    state.NewerSnapshotSeen = true;
                    break;
                case ProposalActionKind.PersistOlderSnapshot:
                    await state.Store.UpsertProposalAsync(OlderProposal(ZaloPollSessionProposalStatus.Ignored), cancellationToken);
                    break;
                case ProposalActionKind.PersistOlderFailure:
                    await ZaloAutoSessionProposalFailurePersistence.PersistUnlessCreatedAsync(
                        state.Db,
                        state.Store,
                        OlderProposal(ZaloPollSessionProposalStatus.Failed),
                        cancellationToken);
                    break;
                case ProposalActionKind.NoOp:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            var persisted = await state.Store.GetProposalAsync("tracked-freshness", "poll-freshness", cancellationToken);
            if (persisted is null) return;
            state.PersistedPollUpdatedAtUnixMs = persisted.PollUpdatedAtUnixMs;
            state.PersistedStructureHash = persisted.PollStructureHash;
            state.PersistedStatus = persisted.Status;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(ProposalFreshnessRaceState state)
        {
            if (!state.NewerSnapshotSeen) yield break;

            if (state.PersistedPollUpdatedAtUnixMs != 200 ||
                !string.Equals(state.PersistedStructureHash, "hash-newer", StringComparison.Ordinal) ||
                state.PersistedStatus != ZaloPollSessionProposalStatus.AwaitingApproval)
            {
                yield return new StatefulInvariantViolation(
                    "auto-session",
                    "proposal-source-revision-monotonic",
                    $"newer poll revision was observed, but durable proposal regressed to updatedAt={state.PersistedPollUpdatedAtUnixMs}, hash={state.PersistedStructureHash}, status={state.PersistedStatus}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var state in states)
                await state.DisposeAsync();
            states.Clear();
        }
    }

    private static ZaloPollSessionProposalData NewerProposal() => Proposal(
        200,
        "hash-newer",
        ZaloPollSessionProposalStatus.AwaitingApproval,
        "newer-candidates");

    private static ZaloPollSessionProposalData OlderProposal(ZaloPollSessionProposalStatus status)
    {
        var proposal = Proposal(100, "hash-older", status, "older-candidates");
        if (status == ZaloPollSessionProposalStatus.Failed)
            proposal.LastError = "simulated_stale_failure";
        return proposal;
    }

    private static ZaloPollSessionProposalData Proposal(
        long pollUpdatedAtUnixMs,
        string structureHash,
        ZaloPollSessionProposalStatus status,
        string reason) => new()
    {
        Id = $"proposal-{pollUpdatedAtUnixMs}",
        TrackedGroupId = "tracked-freshness",
        PollId = "poll-freshness",
        PollQuestion = "Vote lịch",
        PollCreatorId = "leader-1",
        PollUpdatedAtUnixMs = pollUpdatedAtUnixMs,
        PollStructureHash = structureHash,
        CandidatesJson = "[]",
        ClassifierReason = reason,
        Status = status,
        CreatedAt = DateTimeOffset.Parse("2026-09-09T00:00:00+00:00"),
        UpdatedAt = DateTimeOffset.Parse("2026-09-09T00:00:00+00:00")
    };
}
