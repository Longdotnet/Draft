using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class TeamPreferenceRosterCrossFeatureFuzzTests
{
    [Fact]
    public async Task Roster_presence_changes_are_reconciled_before_team_preferences_are_consumed()
    {
        await using var target = new TeamPreferenceRosterTarget();
        var scenario = new StatefulFuzzCase<RosterAction>(
            "team-preference-roster-cross-feature-seed",
            20260909,
            [
                new(RosterActionKind.SetPresence, 3, false),
                new(RosterActionKind.Reconcile, 0, false),
                new(RosterActionKind.Restart, 0, false),
                new(RosterActionKind.SetPresence, 2, false),
                new(RosterActionKind.Reconcile, 0, false),
                new(RosterActionKind.SetPresence, 2, true),
                new(RosterActionKind.Reconcile, 0, false)
            ]);

        var result = await StatefulFuzzRunner.RunAsync(scenario, target);

        Assert.False(result.Failed, Describe(result));
    }

    [Fact]
    public async Task Sequence_mutations_preserve_roster_truth_rotation_and_session_isolation()
    {
        var seedActions = new RosterAction[]
        {
            new(RosterActionKind.SetPresence, 3, false),
            new(RosterActionKind.Reconcile, 0, false),
            new(RosterActionKind.Restart, 0, false),
            new(RosterActionKind.SetPresence, 2, false),
            new(RosterActionKind.Reconcile, 0, false),
            new(RosterActionKind.SetPresence, 1, false),
            new(RosterActionKind.Reconcile, 0, false)
        };

        for (var seed = 1; seed <= 128; seed += 1)
        {
            var actions = StatefulSequenceMutator.Mutate(
                seedActions,
                seed,
                random => CreateAction(random),
                operationCount: 12);
            await using var target = new TeamPreferenceRosterTarget();
            var scenario = new StatefulFuzzCase<RosterAction>(
                $"team-preference-roster-cross-feature-{seed}",
                seed,
                actions);

            var result = await StatefulFuzzRunner.RunAsync(scenario, target);
            if (!result.Failed)
                continue;

            await using var isolatedTarget = new StatefulFuzzIsolatedTarget<RosterState, RosterAction>(
                static () => new TeamPreferenceRosterTarget());
            var promotion = await StatefulFuzzPromotion.PrepareAsync(
                scenario,
                isolatedTarget,
                confirmationRuns: 3);

            Assert.True(
                promotion.IsPromotable,
                $"{Describe(result)}; candidate failure was not stable enough for corpus promotion");

            Assert.False(
                result.Failed,
                $"{Describe(result)}; minimizedReproducer={promotion.SerializePermanentReproducer()}");
        }
    }

    private static RosterAction CreateAction(StableFuzzRandom random) => random.NextInt(4) switch
    {
        0 => new(RosterActionKind.Reconcile, 0, false),
        1 => new(RosterActionKind.Restart, 0, false),
        _ => new(RosterActionKind.SetPresence, random.NextInt(4), random.NextBool())
    };

    private static string Describe(StatefulFuzzRunResult<RosterAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"actions=[{string.Join(',', result.Scenario.Actions.Select(action => $"{action.Kind}:{action.PlayerIndex}:{action.IsPresent}"))}]; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum RosterActionKind
    {
        SetPresence,
        Reconcile,
        Restart
    }

    internal sealed record RosterAction(RosterActionKind Kind, int PlayerIndex, bool IsPresent);

    internal sealed record PreferenceSnapshot(
        bool GroupExists,
        string[] PlayerIds,
        int[] RotationOrders,
        bool ContainsInactivePlayer);

    internal sealed class RosterState : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<VolleyDraftDbContext> options;

        public RosterState()
        {
            connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            Db = new VolleyDraftDbContext(options);
            Db.Database.EnsureCreated();
            SeedAsync().GetAwaiter().GetResult();
        }

        public VolleyDraftDbContext Db { get; private set; }
        public string SessionId { get; private set; } = string.Empty;
        public string ForeignSessionId { get; private set; } = string.Empty;
        public string GroupId { get; private set; } = string.Empty;
        public string ForeignGroupId { get; private set; } = string.Empty;
        public string[] PlayerIds { get; private set; } = [];
        public string[] ForeignPlayerIds { get; private set; } = [];
        public PreferenceSnapshot? LastSnapshot { get; set; }
        public PreferenceSnapshot? ForeignSnapshot { get; set; }
        public bool LastWasReconcile { get; set; }

        public async Task RestartAsync()
        {
            await Db.DisposeAsync();
            Db = new VolleyDraftDbContext(options);
            LastWasReconcile = false;
        }

        public async Task CaptureAsync(CancellationToken cancellationToken)
        {
            LastSnapshot = await CaptureGroupAsync(GroupId, cancellationToken);
            ForeignSnapshot = await CaptureGroupAsync(ForeignGroupId, cancellationToken);
        }

        private async Task<PreferenceSnapshot> CaptureGroupAsync(
            string groupId,
            CancellationToken cancellationToken)
        {
            var group = await Db.TeamPreferenceGroups
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == groupId, cancellationToken);
            if (group is null)
                return new PreferenceSnapshot(false, [], [], false);

            var links = await Db.TeamPreferenceGroupPlayers
                .AsNoTracking()
                .Where(link => link.TeamPreferenceGroupId == groupId)
                .OrderBy(link => link.RotationOrder)
                .ToListAsync(cancellationToken);
            var linkedPlayerIds = links.Select(link => link.SessionPlayerId).ToArray();
            var inactiveCount = await Db.SessionPlayers
                .AsNoTracking()
                .CountAsync(
                    player => linkedPlayerIds.Contains(player.Id) && !player.IsPresent,
                    cancellationToken);
            return new PreferenceSnapshot(
                true,
                linkedPlayerIds,
                links.Select(link => link.RotationOrder).ToArray(),
                inactiveCount > 0);
        }

        private async Task SeedAsync()
        {
            var admin = new User
            {
                Id = "fuzz-team-pref-admin",
                DisplayName = "Fuzz Admin",
                Email = "fuzz-team-pref-admin@example.test",
                PasswordHash = "test"
            };
            Db.Users.Add(admin);
            await Db.SaveChangesAsync();

            var service = new SessionDraftService(Db);
            var session = await service.CreateSessionAsync(
                admin.Id,
                new CreateSessionRequest("T6", 3, 2));
            if (!session.IsSuccess)
                throw new InvalidOperationException(session.Error);
            SessionId = session.Value!.Id;

            var playerIds = new List<string>();
            for (var index = 0; index < 4; index += 1)
            {
                var added = await service.AddPlayerAsync(
                    admin.Id,
                    SessionId,
                    new AddPlayerRequest(
                        $"Target {index + 1}",
                        PlayerRole.New,
                        PlayerLevel.New,
                        PlayerGender.Male));
                if (!added.IsSuccess)
                    throw new InvalidOperationException(added.Error);
                playerIds.Add(added.Value!.Id);
            }
            PlayerIds = playerIds.ToArray();
            GroupId = AddPreferenceGroup(SessionId, PlayerIds);

            var foreignSession = await service.CreateSessionAsync(
                admin.Id,
                new CreateSessionRequest("CN", 3, 2));
            if (!foreignSession.IsSuccess)
                throw new InvalidOperationException(foreignSession.Error);
            ForeignSessionId = foreignSession.Value!.Id;

            var foreignPlayerIds = new List<string>();
            for (var index = 0; index < 3; index += 1)
            {
                var added = await service.AddPlayerAsync(
                    admin.Id,
                    ForeignSessionId,
                    new AddPlayerRequest(
                        $"Foreign {index + 1}",
                        PlayerRole.New,
                        PlayerLevel.New,
                        PlayerGender.Female));
                if (!added.IsSuccess)
                    throw new InvalidOperationException(added.Error);
                foreignPlayerIds.Add(added.Value!.Id);
            }
            ForeignPlayerIds = foreignPlayerIds.ToArray();
            ForeignGroupId = AddPreferenceGroup(ForeignSessionId, ForeignPlayerIds);
            await Db.SaveChangesAsync();
            await CaptureAsync(CancellationToken.None);
        }

        private string AddPreferenceGroup(string sessionId, IReadOnlyList<string> playerIds)
        {
            var group = new TeamPreferenceGroup { SessionId = sessionId };
            for (var index = 0; index < playerIds.Count; index += 1)
            {
                group.Players.Add(new TeamPreferenceGroupPlayer
                {
                    TeamPreferenceGroupId = group.Id,
                    SessionPlayerId = playerIds[index],
                    RotationOrder = index + 1
                });
            }
            Db.TeamPreferenceGroups.Add(group);
            return group.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal sealed class TeamPreferenceRosterTarget :
        IStatefulFuzzTarget<RosterState, RosterAction>,
        IAsyncDisposable
    {
        public string Name => "team-preference-roster-cross-feature";
        public RosterState? LastState { get; private set; }

        public RosterState CreateState(StatefulFuzzCase<RosterAction> scenario)
        {
            LastState = new RosterState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            RosterState state,
            RosterAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == RosterActionKind.Restart)
            {
                await state.RestartAsync();
                return;
            }

            if (action.Kind == RosterActionKind.SetPresence)
            {
                var playerId = state.PlayerIds[action.PlayerIndex % state.PlayerIds.Length];
                var player = await state.Db.SessionPlayers.SingleAsync(
                    item => item.Id == playerId,
                    cancellationToken);
                player.IsPresent = action.IsPresent;
                await state.Db.SaveChangesAsync(cancellationToken);
                state.LastWasReconcile = false;
                return;
            }

            await new TeamPreferenceRosterReconciler(state.Db)
                .ReconcileAsync(state.SessionId, cancellationToken);
            await state.CaptureAsync(cancellationToken);
            state.LastWasReconcile = true;
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(RosterState state)
        {
            if (!state.LastWasReconcile || state.LastSnapshot is null || state.ForeignSnapshot is null)
                yield break;

            var target = state.LastSnapshot;
            if (target.GroupExists && target.ContainsInactivePlayer)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "team-preference-inactive-member-survived-reconcile",
                    "A reconciled same-team preference still references a roster member that is not present.",
                    "cross-feature:team-preference-inactive-member-survived-reconcile");
            }

            if (target.GroupExists && target.PlayerIds.Length < 2)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "team-preference-singleton-survived-reconcile",
                    "A same-team preference with fewer than two present members survived reconciliation.",
                    "cross-feature:team-preference-singleton-survived-reconcile");
            }

            if (target.GroupExists &&
                !target.RotationOrders.SequenceEqual(Enumerable.Range(1, target.RotationOrders.Length)))
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "team-preference-rotation-not-contiguous",
                    "A reconciled same-team preference did not compact rotation order to a contiguous 1..N sequence.",
                    "cross-feature:team-preference-rotation-not-contiguous");
            }

            var foreign = state.ForeignSnapshot;
            if (!foreign.GroupExists ||
                !foreign.PlayerIds.SequenceEqual(state.ForeignPlayerIds) ||
                !foreign.RotationOrders.SequenceEqual(Enumerable.Range(1, state.ForeignPlayerIds.Length)) ||
                foreign.ContainsInactivePlayer)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "team-preference-cross-session-leak",
                    "Reconciling one session changed another session's same-team preference state.",
                    "cross-feature:team-preference-cross-session-leak");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
