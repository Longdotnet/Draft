using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class TeamPreferenceRosterReconcileFuzzTests
{
    [Fact]
    public async Task Roster_churn_never_leaves_stale_or_malformed_same_team_preferences()
    {
        for (var seed = 1; seed <= 192; seed += 1)
            await RunSeedAsync(seed);
    }

    private static async Task RunSeedAsync(int seed)
    {
        var connectionString = $"Data Source=team-preference-roster-fuzz-{seed}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var adminId = $"fuzz-team-pref-admin-{seed}";
        var sessionId = $"fuzz-team-pref-session-{seed}";
        var groupId = $"fuzz-team-pref-group-{seed}";
        var playerIds = Enumerable.Range(1, 5)
            .Select(index => $"fuzz-team-pref-player-{seed}-{index}")
            .ToArray();

        await using (var seedDb = new VolleyDraftDbContext(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Users.Add(new User
            {
                Id = adminId,
                DisplayName = $"Fuzz Team Preference Admin {seed}",
                Email = $"fuzz-team-pref-admin-{seed}@example.test",
                PasswordHash = "test"
            });
            seedDb.MatchSessions.Add(new MatchSession
            {
                Id = sessionId,
                Name = $"Fuzz Team Preference {seed}",
                AdminUserId = adminId,
                Status = SessionStatus.Setup,
                TeamCount = 2,
                TeamSize = 3
            });

            for (var index = 0; index < playerIds.Length; index += 1)
            {
                seedDb.SessionPlayers.Add(new SessionPlayer
                {
                    Id = playerIds[index],
                    SessionId = sessionId,
                    DisplayName = $"P{index + 1}",
                    IsPresent = true
                });
            }

            var preference = new TeamPreferenceGroup
            {
                Id = groupId,
                SessionId = sessionId
            };
            seedDb.TeamPreferenceGroups.Add(preference);
            for (var index = 0; index < playerIds.Length; index += 1)
            {
                seedDb.TeamPreferenceGroupPlayers.Add(new TeamPreferenceGroupPlayer
                {
                    TeamPreferenceGroupId = groupId,
                    SessionPlayerId = playerIds[index],
                    RotationOrder = index + 1
                });
            }

            await seedDb.SaveChangesAsync();
        }

        var random = new StableFuzzRandom(seed);
        var operationCount = 12 + random.NextInt(13);
        for (var operation = 0; operation < operationCount; operation += 1)
        {
            await using (var mutateDb = new VolleyDraftDbContext(options))
            {
                var targetId = playerIds[random.NextInt(playerIds.Length)];
                var target = await mutateDb.SessionPlayers.SingleAsync(item => item.Id == targetId);

                switch (random.NextInt(4))
                {
                    case 0:
                        target.IsPresent = false;
                        break;
                    case 1:
                        target.IsPresent = true;
                        break;
                    case 2:
                        foreach (var player in await mutateDb.SessionPlayers
                                     .Where(item => item.SessionId == sessionId)
                                     .ToListAsync())
                            player.IsPresent = random.NextBool();
                        break;
                    default:
                        target.IsPresent = !target.IsPresent;
                        break;
                }

                await mutateDb.SaveChangesAsync();
            }

            await using (var reconcileDb = new VolleyDraftDbContext(options))
                await new TeamPreferenceRosterReconciler(reconcileDb).ReconcileAsync(sessionId);

            await AssertAuthoritativePreferenceInvariantAsync(options, sessionId, groupId, seed, operation);
        }
    }

    private static async Task AssertAuthoritativePreferenceInvariantAsync(
        DbContextOptions<VolleyDraftDbContext> options,
        string sessionId,
        string groupId,
        int seed,
        int operation)
    {
        await using var verifyDb = new VolleyDraftDbContext(options);
        var group = await verifyDb.TeamPreferenceGroups
            .AsNoTracking()
            .Include(item => item.Players)
            .ThenInclude(link => link.SessionPlayer)
            .SingleOrDefaultAsync(item => item.Id == groupId);

        if (group is null)
            return;

        var context = $"seed={seed}; operation={operation}; present=[{string.Join(',', group.Players.Select(link => $"{link.SessionPlayer.DisplayName}:{link.SessionPlayer.IsPresent}"))}]";

        Assert.True(group.Players.Count >= 2, $"same-team preference survived with fewer than two members; {context}");
        Assert.All(group.Players, link => Assert.True(link.SessionPlayer.IsPresent, $"inactive roster member survived preference reconciliation; {context}"));

        var ordered = group.Players
            .OrderBy(link => link.RotationOrder)
            .Select(link => link.RotationOrder)
            .ToArray();
        Assert.Equal(Enumerable.Range(1, ordered.Length).ToArray(), ordered);

        Assert.Equal(
            group.Players.Count,
            group.Players.Select(link => link.SessionPlayerId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(group.Players, link => Assert.Equal(sessionId, link.SessionPlayer.SessionId));
    }
}
