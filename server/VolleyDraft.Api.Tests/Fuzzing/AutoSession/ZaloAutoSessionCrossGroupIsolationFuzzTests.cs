using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionCrossGroupIsolationFuzzTests
{
    private static readonly IsolationAction[] SeedCorpus =
    [
        new(IsolationActionKind.UpsertGroupA),
        new(IsolationActionKind.UpsertGroupB),
        new(IsolationActionKind.ReadBoth),
        new(IsolationActionKind.Restart),
        new(IsolationActionKind.ReadBoth)
    ];

    [Fact]
    public async Task Same_poll_id_remains_scoped_to_tracked_group_across_restart()
    {
        var scenario = new StatefulFuzzCase<IsolationAction>(
            "auto-session-cross-group-isolation-minimized",
            20260914,
            SeedCorpus);
        await using var target = new CrossGroupIsolationTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Mutated_cross_group_sequences_never_overwrite_or_leak_sibling_group_state()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                SeedCorpus,
                seed,
                random => new IsolationAction(
                    (IsolationActionKind)random.NextInt(Enum.GetValues<IsolationActionKind>().Length)),
                operationCount: 10);
            var scenario = new StatefulFuzzCase<IsolationAction>(
                $"auto-session-cross-group-isolation-{seed}",
                seed,
                actions);
            await using var target = new CrossGroupIsolationTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static string Describe(StatefulFuzzRunResult<IsolationAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum IsolationActionKind
    {
        UpsertGroupA,
        UpsertGroupB,
        ReadBoth,
        Restart,
        DuplicateGroupA,
        DuplicateGroupB,
        NoOp
    }

    internal sealed record IsolationAction(IsolationActionKind Kind);

    internal sealed class CrossGroupState : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required DbContextOptions<VolleyDraftDbContext> Options { get; init; }
        public required VolleyDraftDbContext Db { get; set; }
        public required ZaloAutoSessionStore Store { get; set; }
        public string? ExpectedQuestionA { get; set; }
        public string? ExpectedQuestionB { get; set; }
        public int WritesA { get; set; }
        public int WritesB { get; set; }

        public async Task RestartAsync()
        {
            await Db.DisposeAsync();
            Db = new VolleyDraftDbContext(Options);
            Store = new ZaloAutoSessionStore(Db);
            await Store.EnsureAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    internal sealed class CrossGroupIsolationTarget :
        IStatefulFuzzTarget<CrossGroupState, IsolationAction>, IAsyncDisposable
    {
        private const string PollId = "shared-provider-poll-id";
        private const string GroupA = "tracked-group-a";
        private const string GroupB = "tracked-group-b";
        private readonly List<CrossGroupState> states = [];

        public string Name => "auto-session-cross-group-isolation";

        public CrossGroupState CreateState(StatefulFuzzCase<IsolationAction> scenario)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            var store = new ZaloAutoSessionStore(db);
            store.EnsureAsync().GetAwaiter().GetResult();
            var state = new CrossGroupState
            {
                Connection = connection,
                Options = options,
                Db = db,
                Store = store
            };
            states.Add(state);
            return state;
        }

        public async ValueTask ApplyAsync(
            CrossGroupState state,
            IsolationAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case IsolationActionKind.UpsertGroupA:
                case IsolationActionKind.DuplicateGroupA:
                    state.WritesA += 1;
                    state.ExpectedQuestionA = $"Group A revision {state.WritesA}";
                    await state.Store.UpsertProposalAsync(
                        Proposal(GroupA, state.ExpectedQuestionA, ZaloPollSessionProposalStatus.AwaitingApproval),
                        cancellationToken);
                    break;
                case IsolationActionKind.UpsertGroupB:
                case IsolationActionKind.DuplicateGroupB:
                    state.WritesB += 1;
                    state.ExpectedQuestionB = $"Group B revision {state.WritesB}";
                    await state.Store.UpsertProposalAsync(
                        Proposal(GroupB, state.ExpectedQuestionB, ZaloPollSessionProposalStatus.Approved),
                        cancellationToken);
                    break;
                case IsolationActionKind.ReadBoth:
                    _ = await state.Store.GetProposalAsync(GroupA, PollId, cancellationToken);
                    _ = await state.Store.GetProposalAsync(GroupB, PollId, cancellationToken);
                    break;
                case IsolationActionKind.Restart:
                    await state.RestartAsync();
                    break;
                case IsolationActionKind.NoOp:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(CrossGroupState state)
        {
            var a = state.Store.GetProposalAsync(GroupA, PollId).GetAwaiter().GetResult();
            var b = state.Store.GetProposalAsync(GroupB, PollId).GetAwaiter().GetResult();
            var unknown = state.Store.GetProposalAsync("tracked-group-unknown", PollId).GetAwaiter().GetResult();

            if (state.WritesA == 0 && a is not null)
                yield return Violation("phantom-group-a", "group A became visible before any group A write");
            if (state.WritesB == 0 && b is not null)
                yield return Violation("phantom-group-b", "group B became visible before any group B write");
            if (unknown is not null)
                yield return Violation("cross-group-read-leak", "unknown tracked group resolved another group's proposal");

            if (a is not null)
            {
                if (!string.Equals(a.TrackedGroupId, GroupA, StringComparison.Ordinal))
                    yield return Violation("group-a-scope-corruption", $"group A lookup returned trackedGroup={a.TrackedGroupId}");
                if (!string.Equals(a.PollQuestion, state.ExpectedQuestionA, StringComparison.Ordinal))
                    yield return Violation("group-a-payload-overwrite", $"expected '{state.ExpectedQuestionA}' but got '{a.PollQuestion}'");
                if (a.Status != ZaloPollSessionProposalStatus.AwaitingApproval)
                    yield return Violation("group-a-status-overwrite", $"group A status became {a.Status}");
            }

            if (b is not null)
            {
                if (!string.Equals(b.TrackedGroupId, GroupB, StringComparison.Ordinal))
                    yield return Violation("group-b-scope-corruption", $"group B lookup returned trackedGroup={b.TrackedGroupId}");
                if (!string.Equals(b.PollQuestion, state.ExpectedQuestionB, StringComparison.Ordinal))
                    yield return Violation("group-b-payload-overwrite", $"expected '{state.ExpectedQuestionB}' but got '{b.PollQuestion}'");
                if (b.Status != ZaloPollSessionProposalStatus.Approved)
                    yield return Violation("group-b-status-overwrite", $"group B status became {b.Status}");
            }
        }

        private static StatefulInvariantViolation Violation(string id, string message) =>
            new("authorization", id, message);

        public async ValueTask DisposeAsync()
        {
            foreach (var state in states)
                await state.DisposeAsync();
            states.Clear();
        }
    }

    private static ZaloPollSessionProposalData Proposal(
        string trackedGroupId,
        string question,
        ZaloPollSessionProposalStatus status) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        TrackedGroupId = trackedGroupId,
        PollId = "shared-provider-poll-id",
        PollQuestion = question,
        PollCreatorId = "leader-1",
        PollUpdatedAtUnixMs = 1,
        PollStructureHash = $"hash-{trackedGroupId}",
        CandidatesJson = "[]",
        ClassifierReason = "cross-group-isolation-fuzz",
        Status = status,
        CreatedAt = DateTimeOffset.Parse("2026-09-14T00:00:00+00:00"),
        UpdatedAt = DateTimeOffset.Parse("2026-09-14T00:00:00+00:00")
    };
}
