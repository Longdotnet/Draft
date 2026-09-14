using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class TeamPreferenceOwnershipStatefulFuzzTests
{
    private const string DuplicateFingerprint = "cross-feature:team-preference-player-owned-by-multiple-groups";
    private const string SingletonFingerprint = "cross-feature:team-preference-singleton-group";
    private const string CrossSessionFingerprint = "cross-feature:team-preference-cross-session-membership";

    [Fact]
    public async Task Overlap_restart_and_roster_mutations_preserve_single_group_ownership()
    {
        for (var seed = 1; seed <= 96; seed += 1)
        {
            await using var target = new TeamPreferenceOwnershipTarget();
            var random = new StableFuzzRandom(seed);
            var actions = new List<PreferenceAction>();

            for (var step = 0; step < 48; step += 1)
            {
                var kind = random.NextInt(10) switch
                {
                    0 or 1 => PreferenceActionKind.Restart,
                    2 or 3 => PreferenceActionKind.SetPresence,
                    4 => PreferenceActionKind.DeleteRandomGroup,
                    _ => PreferenceActionKind.CreatePreference
                };

                var first = random.NextInt(6);
                var second = random.NextInt(6);
                while (second == first)
                    second = random.NextInt(6);
                var third = random.NextBool() ? random.NextInt(6) : -1;
                if (third == first || third == second)
                    third = -1;

                actions.Add(new PreferenceAction(
                    kind,
                    first,
                    second,
                    third,
                    random.NextBool()));
            }

            var scenario = new StatefulFuzzCase<PreferenceAction>(
                $"team-preference-ownership-{seed}",
                seed,
                actions);
            var result = await StatefulFuzzRunner.RunAsync(scenario, target);
            if (!result.Failed)
                continue;

            await using var isolated = new StatefulFuzzIsolatedTarget<PreferenceState, PreferenceAction>(
                static () => new TeamPreferenceOwnershipTarget());
            var promotion = await StatefulFuzzPromotion.PrepareAsync(
                scenario,
                isolated,
                confirmationRuns: 3);

            Assert.True(
                promotion.IsPromotable,
                $"{Describe(result)}; candidate failure was not deterministic enough for promotion");
            Assert.False(
                result.Failed,
                $"{Describe(result)}; minimizedReproducer={promotion.SerializePermanentReproducer()}");
        }
    }

    private static string Describe(StatefulFuzzRunResult<PreferenceAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal enum PreferenceActionKind
    {
        CreatePreference,
        SetPresence,
        DeleteRandomGroup,
        Restart
    }

    internal sealed record PreferenceAction(
        PreferenceActionKind Kind,
        int First,
        int Second,
        int Third,
        bool Flag);

    internal sealed class PreferenceState : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<VolleyDraftDbContext> options;

        public PreferenceState()
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
        public string AdminId { get; private set; } = string.Empty;
        public string SessionId { get; private set; } = string.Empty;
        public string ForeignSessionId { get; private set; } = string.Empty;
        public string[] PlayerIds { get; private set; } = [];

        public async Task RestartAsync()
        {
            await Db.DisposeAsync();
            Db = new VolleyDraftDbContext(options);
        }

        private async Task SeedAsync()
        {
            AdminId = "fuzz-team-pref-owner-admin";
            Db.Users.Add(new User
            {
                Id = AdminId,
                DisplayName = "Fuzz Admin",
                Email = "fuzz-team-pref-owner@example.test",
                PasswordHash = "test"
            });
            await Db.SaveChangesAsync();

            var service = new SessionDraftService(Db);
            var session = await service.CreateSessionAsync(
                AdminId,
                new CreateSessionRequest("T4 ownership fuzz", 3, 2));
            if (!session.IsSuccess)
                throw new InvalidOperationException(session.Error);
            SessionId = session.Value!.Id;

            var ids = new List<string>();
            for (var index = 0; index < 6; index += 1)
            {
                var added = await service.AddPlayerAsync(
                    AdminId,
                    SessionId,
                    new AddPlayerRequest(
                        $"Target {index + 1}",
                        PlayerRole.New,
                        PlayerLevel.New,
                        index % 2 == 0 ? PlayerGender.Male : PlayerGender.Female));
                if (!added.IsSuccess)
                    throw new InvalidOperationException(added.Error);
                ids.Add(added.Value!.Id);
            }
            PlayerIds = ids.ToArray();

            var foreign = await service.CreateSessionAsync(
                AdminId,
                new CreateSessionRequest("CN isolation", 3, 2));
            if (!foreign.IsSuccess)
                throw new InvalidOperationException(foreign.Error);
            ForeignSessionId = foreign.Value!.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal sealed class TeamPreferenceOwnershipTarget :
        IStatefulFuzzTarget<PreferenceState, PreferenceAction>,
        IAsyncDisposable
    {
        public string Name => "team-preference-ownership-stateful";
        public PreferenceState? LastState { get; private set; }

        public PreferenceState CreateState(StatefulFuzzCase<PreferenceAction> scenario)
        {
            LastState = new PreferenceState();
            return LastState;
        }

        public async ValueTask ApplyAsync(
            PreferenceState state,
            PreferenceAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            if (action.Kind == PreferenceActionKind.Restart)
            {
                await state.RestartAsync();
                return;
            }

            var service = new SessionDraftService(state.Db);
            if (action.Kind == PreferenceActionKind.SetPresence)
            {
                var playerId = state.PlayerIds[action.First % state.PlayerIds.Length];
                var player = await state.Db.SessionPlayers
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == playerId, cancellationToken);
                await service.UpdatePlayerAsync(
                    state.AdminId,
                    state.SessionId,
                    player.Id,
                    new UpdatePlayerRequest(
                        player.DisplayName,
                        player.Role,
                        player.Level,
                        player.Gender,
                        action.Flag,
                        player.IsCaptainEligible));
                return;
            }

            if (action.Kind == PreferenceActionKind.DeleteRandomGroup)
            {
                var groups = await state.Db.TeamPreferenceGroups
                    .AsNoTracking()
                    .Where(group => group.SessionId == state.SessionId)
                    .OrderBy(group => group.Id)
                    .Select(group => group.Id)
                    .ToListAsync(cancellationToken);
                if (groups.Count == 0)
                    return;
                var groupId = groups[(action.First + action.Second) % groups.Count];
                await service.DeleteTeamPreferenceGroupAsync(
                    state.AdminId,
                    state.SessionId,
                    groupId);
                return;
            }

            var ids = new List<string>
            {
                state.PlayerIds[action.First % state.PlayerIds.Length],
                state.PlayerIds[action.Second % state.PlayerIds.Length]
            };
            if (action.Third >= 0)
                ids.Add(state.PlayerIds[action.Third % state.PlayerIds.Length]);

            await service.CreateTeamPreferenceGroupAsync(
                state.AdminId,
                state.SessionId,
                new CreateTeamPreferenceGroupRequest(ids));
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(PreferenceState state)
        {
            state.Db.ChangeTracker.Clear();

            var duplicateOwner = state.Db.TeamPreferenceGroupPlayers
                .AsNoTracking()
                .Where(link => link.TeamPreferenceGroup.SessionId == state.SessionId)
                .GroupBy(link => link.SessionPlayerId)
                .Select(group => new { PlayerId = group.Key, Count = group.Count() })
                .FirstOrDefault(group => group.Count > 1);
            if (duplicateOwner is not null)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "team-preference-player-owned-by-multiple-groups",
                    $"Player {duplicateOwner.PlayerId} belongs to {duplicateOwner.Count} same-team groups.",
                    DuplicateFingerprint);
            }

            var singleton = state.Db.TeamPreferenceGroups
                .AsNoTracking()
                .Where(group => group.SessionId == state.SessionId)
                .Select(group => new
                {
                    group.Id,
                    Count = group.Players.Count(link => link.SessionPlayer.IsPresent)
                })
                .FirstOrDefault(group => group.Count < 2);
            if (singleton is not null)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "team-preference-singleton-group",
                    $"Group {singleton.Id} has only {singleton.Count} present member(s).",
                    SingletonFingerprint);
            }

            var crossSession = state.Db.TeamPreferenceGroupPlayers
                .AsNoTracking()
                .Any(link =>
                    link.TeamPreferenceGroup.SessionId != link.SessionPlayer.SessionId);
            if (crossSession)
            {
                yield return new StatefulInvariantViolation(
                    "cross-feature",
                    "team-preference-cross-session-membership",
                    "A same-team group contains a player from another session.",
                    CrossSessionFingerprint);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
