using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloAutoSessionLifecycleReconciliationFairnessFuzzTests
{
    [Fact]
    public async Task Missing_handoffs_remain_bounded_and_fair_across_restart_shaped_cycles()
    {
        const int seedCount = 96;
        var baseAttemptAt = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed);
            var candidateCount = 13 + random.NextInt(36);

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var knownAttempts = Enumerable.Range(1, candidateCount)
                .ToDictionary(index => $"session-{index}", _ => 0, StringComparer.Ordinal);

            await using (var setupDb = new VolleyDraftDbContext(options))
            {
                var tracked = await new ZaloAutoSessionSettingsStore(setupDb).InsertIfMissingAsync(new ZaloTrackedGroupData
                {
                    AdminUserId = "admin-a",
                    ZaloConnectionId = "connection-a",
                    GroupId = "group-a",
                    GroupName = "Bóng UTE"
                });
                var autoSessions = new ZaloAutoSessionStore(setupDb);

                for (var index = 1; index <= candidateCount; index++)
                {
                    await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
                    {
                        TrackedGroupId = tracked.Id,
                        PollId = $"poll-{index}",
                        PollQuestion = $"Kèo {index}",
                        PollCreatorId = "captain-a",
                        PollStructureHash = $"hash-{index}",
                        Status = ZaloPollSessionProposalStatus.Created
                    });
                    await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
                        $"link-{index}",
                        tracked.Id,
                        $"poll-{index}",
                        $"option-{index}",
                        $"session-{index}",
                        baseAttemptAt.AddMinutes(index)));
                }

                var store = new ZaloAutoSessionLifecycleHandoffStoreV5(setupDb);
                var preAttemptTarget = random.NextInt(Math.Max(1, candidateCount / 2));
                var preAttempted = new HashSet<int>();
                while (preAttempted.Count < preAttemptTarget)
                    preAttempted.Add(1 + random.NextInt(candidateCount));

                var ordinal = 0;
                foreach (var index in preAttempted.OrderBy(value => value))
                {
                    var sessionId = $"session-{index}";
                    await store.MarkAttemptAsync(sessionId, baseAttemptAt.AddSeconds(ordinal++));
                    knownAttempts[sessionId]++;
                }
            }

            var cycleCount = 2 + random.NextInt(5);
            for (var cycle = 0; cycle < cycleCount; cycle++)
            {
                // A fresh DbContext models API/worker restart while the SQLite database remains durable.
                await using var cycleDb = new VolleyDraftDbContext(options);
                var store = new ZaloAutoSessionLifecycleHandoffStoreV5(cycleDb);
                var candidates = await store.GetMissingAsync();

                Assert.InRange(
                    candidates.Count,
                    1,
                    ZaloAutoSessionLifecycleReconciliationPolicyV5.MaxCandidatesPerCycle);
                Assert.Equal(candidates.Count, candidates.Select(candidate => candidate.SessionId).Distinct().Count());

                var selectedIds = candidates.Select(candidate => candidate.SessionId).ToHashSet(StringComparer.Ordinal);
                var neverAttempted = knownAttempts
                    .Where(pair => pair.Value == 0)
                    .Select(pair => pair.Key)
                    .ToArray();

                if (neverAttempted.Length >= candidates.Count)
                {
                    Assert.All(candidates, candidate => Assert.Equal(0, knownAttempts[candidate.SessionId]));
                }
                else
                {
                    Assert.All(neverAttempted, sessionId => Assert.Contains(sessionId, selectedIds));
                }

                for (var index = 0; index < candidates.Count; index++)
                {
                    var sessionId = candidates[index].SessionId;
                    await store.MarkAttemptAsync(
                        sessionId,
                        baseAttemptAt.AddHours(seed).AddMinutes(cycle).AddTicks(index));
                    knownAttempts[sessionId]++;
                }
            }
        }
    }
}
