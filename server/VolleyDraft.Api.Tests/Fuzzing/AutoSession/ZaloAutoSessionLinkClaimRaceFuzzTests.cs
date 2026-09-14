using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloAutoSessionLinkClaimRaceFuzzTests
{
    private static readonly LinkAction[] SeedCorpus =
    [
        new(LinkActionKind.ClaimA),
        new(LinkActionKind.ClaimB),
        new(LinkActionKind.RestartA),
        new(LinkActionKind.RestartB),
        new(LinkActionKind.ReadBoth)
    ];

    [Fact]
    public async Task Minimized_multi_instance_reproducer_keeps_first_poll_option_claim_authoritative()
    {
        var scenario = new StatefulFuzzCase<LinkAction>(
            "auto-session-link-claim-race-minimized",
            20260915,
            SeedCorpus);
        await using var target = new LinkClaimRaceTarget();

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
        Assert.Equal(SessionA, target.LastState!.ExpectedSessionId);
    }

    [Fact]
    public async Task Mutated_claim_restart_and_rollback_sequences_never_split_one_poll_option_across_sessions()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                SeedCorpus,
                seed,
                random => new LinkAction(
                    (LinkActionKind)random.NextInt(Enum.GetValues<LinkActionKind>().Length)),
                operationCount: 14);
            var scenario = new StatefulFuzzCase<LinkAction>(
                $"auto-session-link-claim-race-{seed}",
                seed,
                actions);
            await using var target = new LinkClaimRaceTarget();

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);

            Assert.False(result.Failed, Describe(result));
        }
    }

    private static string Describe(StatefulFuzzRunResult<LinkAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => action.Kind))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum LinkActionKind
    {
        ClaimA,
        ClaimB,
        DuplicateA,
        DuplicateB,
        RollbackClaimA,
        RollbackClaimB,
        RestartA,
        RestartB,
        ReadBoth,
        NoOp
    }

    internal sealed record LinkAction(LinkActionKind Kind);

    internal sealed record DurableLinkSnapshot(int Count, IReadOnlyList<string> SessionIds);

    internal sealed class LinkRaceState : IAsyncDisposable
    {
        private readonly string connectionString;

        public LinkRaceState(string connectionString, SqliteConnection anchor)
        {
            this.connectionString = connectionString;
            Anchor = anchor;
        }

        public SqliteConnection Anchor { get; }
        public required LinkInstance InstanceA { get; set; }
        public required LinkInstance InstanceB { get; set; }
        public string? ExpectedSessionId { get; set; }

        public LinkInstance CreateInstance()
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            var store = new ZaloAutoSessionStore(db);
            store.EnsureAsync().GetAwaiter().GetResult();
            return new LinkInstance(connection, db, store);
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

        public async Task<DurableLinkSnapshot> ReadSnapshotAsync()
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "SessionId"
                FROM "ZaloAutoSessionLinks"
                WHERE "TrackedGroupId" = @TrackedGroupId
                  AND "PollId" = @PollId
                  AND "OptionId" = @OptionId
                ORDER BY "SessionId";
                """;
            command.Parameters.AddWithValue("@TrackedGroupId", TrackedGroupId);
            command.Parameters.AddWithValue("@PollId", PollId);
            command.Parameters.AddWithValue("@OptionId", OptionId);
            var sessions = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                sessions.Add(reader.GetString(0));
            return new DurableLinkSnapshot(sessions.Count, sessions);
        }

        public async ValueTask DisposeAsync()
        {
            await InstanceA.DisposeAsync();
            await InstanceB.DisposeAsync();
            await Anchor.DisposeAsync();
        }
    }

    internal sealed class LinkInstance(
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

    internal sealed class LinkClaimRaceTarget :
        IStatefulFuzzTarget<LinkRaceState, LinkAction>, IAsyncDisposable
    {
        private readonly List<LinkRaceState> states = [];

        public string Name => "auto-session-link-claim-race";
        public LinkRaceState? LastState { get; private set; }

        public LinkRaceState CreateState(StatefulFuzzCase<LinkAction> scenario)
        {
            var connectionString = $"Data Source=auto-session-link-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var anchor = new SqliteConnection(connectionString);
            anchor.Open();
            var state = new LinkRaceState(connectionString, anchor)
            {
                InstanceA = null!,
                InstanceB = null!
            };
            state.InstanceA = state.CreateInstance();
            state.InstanceB = state.CreateInstance();
            states.Add(state);
            LastState = state;
            return state;
        }

        public async ValueTask ApplyAsync(
            LinkRaceState state,
            LinkAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case LinkActionKind.ClaimA:
                case LinkActionKind.DuplicateA:
                    await CommitClaimAsync(state, state.InstanceA, SessionA, cancellationToken);
                    break;
                case LinkActionKind.ClaimB:
                case LinkActionKind.DuplicateB:
                    await CommitClaimAsync(state, state.InstanceB, SessionB, cancellationToken);
                    break;
                case LinkActionKind.RollbackClaimA:
                    await RollbackClaimAsync(state.InstanceA, SessionA, cancellationToken);
                    break;
                case LinkActionKind.RollbackClaimB:
                    await RollbackClaimAsync(state.InstanceB, SessionB, cancellationToken);
                    break;
                case LinkActionKind.RestartA:
                    await state.RestartAAsync();
                    break;
                case LinkActionKind.RestartB:
                    await state.RestartBAsync();
                    break;
                case LinkActionKind.ReadBoth:
                    _ = await state.InstanceA.Store.GetLinkAsync(TrackedGroupId, PollId, OptionId, cancellationToken);
                    _ = await state.InstanceB.Store.GetLinkAsync(TrackedGroupId, PollId, OptionId, cancellationToken);
                    break;
                case LinkActionKind.NoOp:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(LinkRaceState state)
        {
            var snapshot = state.ReadSnapshotAsync().GetAwaiter().GetResult();
            var readA = state.InstanceA.Store.GetLinkAsync(TrackedGroupId, PollId, OptionId).GetAwaiter().GetResult();
            var readB = state.InstanceB.Store.GetLinkAsync(TrackedGroupId, PollId, OptionId).GetAwaiter().GetResult();

            if (snapshot.Count > 1)
                yield return Violation("duplicate-option-link", $"one poll option has {snapshot.Count} durable link rows");

            if (state.ExpectedSessionId is null)
            {
                if (snapshot.Count != 0 || readA is not null || readB is not null)
                    yield return Violation("phantom-option-claim", "a durable link appeared before any committed claim");
                yield break;
            }

            if (snapshot.Count != 1)
            {
                yield return Violation("lost-option-claim", $"expected one durable link for {state.ExpectedSessionId}, found {snapshot.Count}");
                yield break;
            }

            var durableSessionId = snapshot.SessionIds.Single();
            if (!string.Equals(durableSessionId, state.ExpectedSessionId, StringComparison.Ordinal))
                yield return Violation("claim-owner-changed", $"first winner {state.ExpectedSessionId} changed to {durableSessionId}");
            if (!string.Equals(readA?.SessionId, state.ExpectedSessionId, StringComparison.Ordinal))
                yield return Violation("instance-a-stale-link-read", $"instance A read {readA?.SessionId ?? "null"}, expected {state.ExpectedSessionId}");
            if (!string.Equals(readB?.SessionId, state.ExpectedSessionId, StringComparison.Ordinal))
                yield return Violation("instance-b-stale-link-read", $"instance B read {readB?.SessionId ?? "null"}, expected {state.ExpectedSessionId}");
        }

        private static async Task CommitClaimAsync(
            LinkRaceState state,
            LinkInstance instance,
            string sessionId,
            CancellationToken cancellationToken)
        {
            if (state.ExpectedSessionId is null)
                state.ExpectedSessionId = sessionId;

            await instance.Store.AddLinkAsync(
                Link(sessionId, $"link-{sessionId}-{Guid.NewGuid():N}"),
                cancellationToken);
        }

        private static async Task RollbackClaimAsync(
            LinkInstance instance,
            string sessionId,
            CancellationToken cancellationToken)
        {
            await using var transaction = await instance.Db.Database.BeginTransactionAsync(cancellationToken);
            await instance.Store.AddLinkAsync(
                Link(sessionId, $"rollback-{sessionId}-{Guid.NewGuid():N}"),
                cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
        }

        private static StatefulInvariantViolation Violation(string id, string message) =>
            new("idempotency", id, message);

        public async ValueTask DisposeAsync()
        {
            foreach (var state in states)
                await state.DisposeAsync();
            states.Clear();
        }
    }

    private const string TrackedGroupId = "tracked-link-race";
    private const string PollId = "poll-link-race";
    private const string OptionId = "option-cn";
    private const string SessionA = "session-a";
    private const string SessionB = "session-b";

    private static ZaloAutoSessionLinkData Link(string sessionId, string linkId) => new(
        linkId,
        TrackedGroupId,
        PollId,
        OptionId,
        sessionId,
        DateTimeOffset.Parse("2026-09-15T00:00:00+00:00"));
}
