using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class TeamPreferenceMultiInstanceRaceFuzzTests
{
    private const string DuplicateFingerprint = "concurrency:team-preference-multi-instance-duplicate-owner";
    private const string SingletonFingerprint = "concurrency:team-preference-multi-instance-singleton";
    private const string CrossSessionFingerprint = "concurrency:team-preference-multi-instance-cross-session";
    private const string AllRejectedFingerprint = "concurrency:team-preference-multi-instance-all-overlap-writers-rejected";

    [Fact]
    public async Task Overlapping_writers_from_separate_contexts_fail_closed_and_preserve_one_owner()
    {
        for (var seed = 1; seed <= 24; seed += 1)
        {
            await using var target = new TeamPreferenceMultiInstanceTarget();
            var random = new StableFuzzRandom(seed);
            var actions = new List<RaceAction>();

            for (var step = 0; step < 12; step += 1)
            {
                var anchor = random.NextInt(6);
                var left = NextDistinct(random, anchor);
                var right = NextDistinct(random, anchor, left);
                var extra = random.NextBool() ? NextDistinct(random, anchor, left, right) : -1;
                actions.Add(new RaceAction(anchor, left, right, extra, random.NextBool()));
            }

            var scenario = new StatefulFuzzCase<RaceAction>(
                $"team-preference-multi-instance-race-{seed}",
                seed,
                actions);
            var result = await StatefulFuzzRunner.RunAsync(scenario, target);
            if (!result.Failed)
                continue;

            await using var isolated = new StatefulFuzzIsolatedTarget<RaceState, RaceAction>(
                static () => new TeamPreferenceMultiInstanceTarget());
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

    private static int NextDistinct(StableFuzzRandom random, params int[] excluded)
    {
        while (true)
        {
            var value = random.NextInt(6);
            if (!excluded.Contains(value))
                return value;
        }
    }

    private static string Describe(StatefulFuzzRunResult<RaceAction> result) =>
        $"seed={result.Scenario.Seed}; fingerprint={result.FailureFingerprint ?? "none"}; " +
        $"failureIndex={result.FailureActionIndex?.ToString() ?? "none"}; " +
        $"violation={result.Violation?.Message ?? "none"}; exception={result.Exception?.Message ?? "none"}";

    internal sealed record RaceAction(
        int Anchor,
        int LeftPartner,
        int RightPartner,
        int ExtraPartner,
        bool ReverseStartOrder);

    internal sealed class RaceState : IAsyncDisposable
    {
        private readonly SqliteConnection keeper;
        private readonly string connectionString;

        public RaceState()
        {
            connectionString = $"Data Source=team-pref-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            keeper = new SqliteConnection(connectionString);
            keeper.Open();
            SeedAsync().GetAwaiter().GetResult();
        }

        public string AdminId { get; private set; } = string.Empty;
        public string SessionId { get; private set; } = string.Empty;
        public string ForeignSessionId { get; private set; } = string.Empty;
        public string[] PlayerIds { get; private set; } = [];

        public VolleyDraftDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;
            return new VolleyDraftDbContext(options);
        }

        private async Task SeedAsync()
        {
            await using var db = CreateDb();
            await db.Database.EnsureCreatedAsync();

            AdminId = "fuzz-team-pref-race-admin";
            db.Users.Add(new User
            {
                Id = AdminId,
                DisplayName = "Race Admin",
                Email = "fuzz-team-pref-race@example.test",
                PasswordHash = "test"
            });
            await db.SaveChangesAsync();

            var service = new SessionDraftService(db);
            var session = await service.CreateSessionAsync(
                AdminId,
                new CreateSessionRequest("T4 multi-instance race", 3, 2));
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
                        $"Race Player {index + 1}",
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
                new CreateSessionRequest("CN race isolation", 3, 2));
            if (!foreign.IsSuccess)
                throw new InvalidOperationException(foreign.Error);
            ForeignSessionId = foreign.Value!.Id;
        }

        public async ValueTask DisposeAsync() => await keeper.DisposeAsync();
    }

    internal sealed class TeamPreferenceMultiInstanceTarget :
        IStatefulFuzzTarget<RaceState, RaceAction>,
        IAsyncDisposable
    {
        public string Name => "team-preference-multi-instance-race";
        public RaceState? LastState { get; private set; }
        public bool LastRaceAllRejected { get; private set; }
        public string? LastRaceDescription { get; private set; }

        public RaceState CreateState(StatefulFuzzCase<RaceAction> scenario)
        {
            LastState = new RaceState();
            LastRaceAllRejected = false;
            LastRaceDescription = null;
            return LastState;
        }

        public async ValueTask ApplyAsync(
            RaceState state,
            RaceAction action,
            int actionIndex,
            CancellationToken cancellationToken)
        {
            await using var leftDb = state.CreateDb();
            await using var rightDb = state.CreateDb();
            var leftService = new SessionDraftService(leftDb);
            var rightService = new SessionDraftService(rightDb);

            var anchorId = state.PlayerIds[action.Anchor % state.PlayerIds.Length];
            var leftIds = new List<string>
            {
                anchorId,
                state.PlayerIds[action.LeftPartner % state.PlayerIds.Length]
            };
            var rightIds = new List<string>
            {
                anchorId,
                state.PlayerIds[action.RightPartner % state.PlayerIds.Length]
            };
            if (action.ExtraPartner >= 0)
            {
                var extra = state.PlayerIds[action.ExtraPartner % state.PlayerIds.Length];
                if (!leftIds.Contains(extra, StringComparer.Ordinal))
                    leftIds.Add(extra);
                else if (!rightIds.Contains(extra, StringComparer.Ordinal))
                    rightIds.Add(extra);
            }

            var leftTask = leftService.CreateTeamPreferenceGroupAsync(
                state.AdminId,
                state.SessionId,
                new CreateTeamPreferenceGroupRequest(leftIds));
            var rightTask = rightService.CreateTeamPreferenceGroupAsync(
                state.AdminId,
                state.SessionId,
                new CreateTeamPreferenceGroupRequest(rightIds));

            ServiceResult<TeamPreferenceGroupResponse>[] results;
            if (action.ReverseStartOrder)
            {
                results = await Task.WhenAll(rightTask, leftTask);
            }
            else
            {
                results = await Task.WhenAll(leftTask, rightTask);
            }

            LastRaceAllRejected = results.All(result => !result.IsSuccess);
            LastRaceDescription = string.Join(
                "; ",
                results.Select(result => result.IsSuccess
                    ? $"success:{result.StatusCode}"
                    : $"failure:{result.StatusCode}:{result.Error}"));
        }

        public IEnumerable<StatefulInvariantViolation> EvaluateInvariants(RaceState state)
        {
            using var db = state.CreateDb();

            var duplicateOwner = db.TeamPreferenceGroupPlayers
                .AsNoTracking()
                .Where(link => link.TeamPreferenceGroup.SessionId == state.SessionId)
                .GroupBy(link => link.SessionPlayerId)
                .Select(group => new { PlayerId = group.Key, Count = group.Count() })
                .FirstOrDefault(group => group.Count > 1);
            if (duplicateOwner is not null)
            {
                yield return new StatefulInvariantViolation(
                    "concurrency",
                    "team-preference-multi-instance-duplicate-owner",
                    $"Player {duplicateOwner.PlayerId} belongs to {duplicateOwner.Count} same-team groups after a race.",
                    DuplicateFingerprint);
            }

            var singleton = db.TeamPreferenceGroups
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
                    "concurrency",
                    "team-preference-multi-instance-singleton",
                    $"Group {singleton.Id} has only {singleton.Count} present member(s) after a race.",
                    SingletonFingerprint);
            }

            var crossSession = db.TeamPreferenceGroupPlayers
                .AsNoTracking()
                .Any(link => link.TeamPreferenceGroup.SessionId != link.SessionPlayer.SessionId);
            if (crossSession)
            {
                yield return new StatefulInvariantViolation(
                    "concurrency",
                    "team-preference-multi-instance-cross-session",
                    "A same-team group contains a player from another session after a race.",
                    CrossSessionFingerprint);
            }

            if (LastRaceAllRejected)
            {
                yield return new StatefulInvariantViolation(
                    "concurrency",
                    "team-preference-multi-instance-all-overlap-writers-rejected",
                    $"Both overlapping writers were rejected instead of one authoritative mutation winning. {LastRaceDescription}",
                    AllRejectedFingerprint);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (LastState is not null)
                await LastState.DisposeAsync();
        }
    }
}
