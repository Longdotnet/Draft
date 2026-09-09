using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloOpenSlotCrossAccountIsolationFuzzTests
{
    private static readonly SlotAction[] SeedActions =
    [
        new(SlotActionKind.OpenConnectionA),
        new(SlotActionKind.OpenConnectionB),
        new(SlotActionKind.ReopenConnectionA)
    ];

    [Fact]
    public async Task Same_group_owner_session_ids_on_two_connections_keep_separate_offer_ledgers()
    {
        var scenario = new StatefulFuzzCase<SlotAction>(
            "slot-cross-account-isolation-production-shape",
            20260909,
            [new(SlotActionKind.OpenConnectionA), new(SlotActionKind.OpenConnectionB)]);
        await using var target = new SlotIsolationTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Stateful_open_reopen_mutations_never_move_an_offer_between_connections()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                SeedActions,
                seed,
                CreateAction,
                operationCount: 8);
            var scenario = new StatefulFuzzCase<SlotAction>(
                $"slot-cross-account-isolation-{seed}",
                seed,
                actions);
            await using var target = new SlotIsolationTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static SlotAction CreateAction(StableFuzzRandom random) =>
        new((SlotActionKind)random.NextInt(Enum.GetValues<SlotActionKind>().Length));

    private static string Describe(StatefulFuzzRunResult<SlotAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum SlotActionKind
    {
        OpenConnectionA,
        OpenConnectionB,
        ReopenConnectionA,
        ReopenConnectionB
    }

    internal sealed record SlotAction(SlotActionKind Kind);

    internal sealed class SlotIsolationState : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        public SlotIsolationState()
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
        public HashSet<string> OpenedConnections { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<ZaloOpenSlotOfferSnapshot>> Observed { get; } =
            new(StringComparer.Ordinal);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal sealed class SlotIsolationTarget :
        IStatefulFuzzTarget<SlotIsolationState, SlotAction>,
        IAsyncDisposable
    {
        private const string ConnectionA = "connection-a";
        private const string ConnectionB = "connection-b";
        private const string GroupId = "shared-provider-group-id";
        private const string OwnerId = "owner-stable-uid";
        private const string SessionId = "shared-session-id";

        public string Name => "slot-cross-account-isolation";
        public SlotIsolationState? LastState { get; private set; }

        public SlotIsolationState CreateState(StatefulFuzzCase<SlotAction> scenario)
        {
            LastState = new SlotIsolationState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            SlotIsolationState state,
            SlotAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            var connectionId = action.Kind is SlotActionKind.OpenConnectionA or SlotActionKind.ReopenConnectionA
                ? ConnectionA
                : ConnectionB;
            state.OpenedConnections.Add(connectionId);

            await state.Store.OpenAsync(
                connectionId,
                GroupId,
                OwnerId,
                "Owner",
                SessionId,
                "T6",
                $"message-{actionIndex}",
                DateTimeOffset.UtcNow.AddHours(2),
                DateTimeOffset.UtcNow.AddMinutes(45),
                cancellationToken);

            foreach (var openedConnection in state.OpenedConnections)
            {
                state.Observed[openedConnection] = await state.Store.ListOwnedActiveAsync(
                    openedConnection,
                    GroupId,
                    OwnerId,
                    cancellationToken);
            }
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(SlotIsolationState state)
        {
            foreach (var openedConnection in state.OpenedConnections)
            {
                if (!state.Observed.TryGetValue(openedConnection, out var offers))
                    continue;

                var matching = offers.Where(offer =>
                    string.Equals(offer.ConnectionId, openedConnection, StringComparison.Ordinal) &&
                    string.Equals(offer.GroupId, GroupId, StringComparison.Ordinal) &&
                    string.Equals(offer.OwnerZaloUserId, OwnerId, StringComparison.Ordinal) &&
                    string.Equals(offer.SessionId, SessionId, StringComparison.Ordinal)).ToArray();
                if (matching.Length == 1)
                    continue;

                yield return new StatefulInvariantViolation(
                    "slot-lifecycle",
                    "open-offer-ledger-isolated-by-connection",
                    $"connection={openedConnection}; expected=1; actual={matching.Length}; totalObserved={offers.Count}",
                    "slot-lifecycle:cross-account-open-offer-alias");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
