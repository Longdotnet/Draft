using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloAutoSessionLifecycleMixedFailureFuzzTests
{
    [Fact]
    public async Task Missing_session_failures_do_not_block_valid_handoffs_across_restart_shaped_cycles()
    {
        const int seedCount = 64;
        var baseTime = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed);
            var invalidCount = 1 + random.NextInt(5);
            var validCount = 1 + random.NextInt(5);
            var totalCount = invalidCount + validCount;

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var invalidSessionIds = new HashSet<string>(StringComparer.Ordinal);
            var validProposalIds = new HashSet<string>(StringComparer.Ordinal);
            var invalidProposalIds = new HashSet<string>(StringComparer.Ordinal);

            await using (var setupDb = new VolleyDraftDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Users.Add(new User
                {
                    Id = "admin-a",
                    DisplayName = "Admin",
                    Email = $"admin-{seed}@example.test",
                    PasswordHash = "test"
                });
                await setupDb.SaveChangesAsync();

                var tracked = await new ZaloAutoSessionSettingsStore(setupDb).InsertIfMissingAsync(new ZaloTrackedGroupData
                {
                    AdminUserId = "admin-a",
                    ZaloConnectionId = "connection-a",
                    GroupId = "group-a",
                    GroupName = "Bóng UTE"
                });
                var autoSessions = new ZaloAutoSessionStore(setupDb);

                var validRemaining = validCount;
                var invalidRemaining = invalidCount;
                for (var index = 1; index <= totalCount; index++)
                {
                    var chooseValid = validRemaining > 0 &&
                                      (invalidRemaining == 0 || random.NextBool());
                    if (chooseValid)
                        validRemaining--;
                    else
                        invalidRemaining--;

                    var pollId = $"poll-{seed}-{index}";
                    var sessionId = $"session-{seed}-{index}";
                    var proposal = await autoSessions.UpsertProposalAsync(new ZaloPollSessionProposalData
                    {
                        TrackedGroupId = tracked.Id,
                        PollId = pollId,
                        PollQuestion = $"Kèo {index}",
                        PollCreatorId = "captain-a",
                        PollStructureHash = $"hash-{seed}-{index}",
                        Status = ZaloPollSessionProposalStatus.Created
                    });
                    await autoSessions.AddLinkAsync(new ZaloAutoSessionLinkData(
                        $"link-{seed}-{index}",
                        tracked.Id,
                        pollId,
                        $"option-{index}",
                        sessionId,
                        baseTime.AddMinutes(index)));

                    if (chooseValid)
                    {
                        setupDb.MatchSessions.Add(new MatchSession
                        {
                            Id = sessionId,
                            Name = $"Cancelled {index}",
                            AdminUserId = "admin-a",
                            Status = SessionStatus.Cancelled,
                            CreatedAt = baseTime,
                            UpdatedAt = baseTime
                        });
                        validProposalIds.Add(proposal.Id);
                    }
                    else
                    {
                        // Durable Auto Session link exists but the referenced session row is gone.
                        // This is intentionally adversarial: one corrupt/stale candidate must not
                        // stop unrelated valid lifecycle handoffs in the same scheduler batch.
                        invalidSessionIds.Add(sessionId);
                        invalidProposalIds.Add(proposal.Id);
                    }
                }

                await setupDb.SaveChangesAsync();
            }

            await using (var firstCycleDb = new VolleyDraftDbContext(options))
            {
                var result = await new ZaloAutoSessionLifecycleHandoffStoreV5(firstCycleDb)
                    .ReconcileMissingAsync(NullLogger.Instance);

                Assert.Equal(totalCount, result.CandidateCount);
                Assert.Equal(validCount, result.HandedOffCount);
                Assert.Equal(invalidCount, result.FailedCount);
            }

            // Fresh DbContext models a deploy/restart after the mixed batch. Successful work must
            // stay terminal while only the malformed durable links remain retryable.
            await using (var restartedDb = new VolleyDraftDbContext(options))
            {
                var restartedStore = new ZaloAutoSessionLifecycleHandoffStoreV5(restartedDb);
                var remaining = await restartedStore.GetMissingAsync(limit: 200);

                Assert.Equal(invalidCount, remaining.Count);
                Assert.All(remaining, candidate => Assert.Contains(candidate.SessionId, invalidSessionIds));
                Assert.Equal(invalidCount, remaining.Select(candidate => candidate.SessionId).Distinct().Count());

                foreach (var proposalId in validProposalIds)
                    Assert.True(await restartedStore.HasHandedOffOwnershipAsync(proposalId));
                foreach (var proposalId in invalidProposalIds)
                    Assert.False(await restartedStore.HasHandedOffOwnershipAsync(proposalId));

                var retry = await restartedStore.ReconcileMissingAsync(NullLogger.Instance);
                Assert.Equal(invalidCount, retry.CandidateCount);
                Assert.Equal(0, retry.HandedOffCount);
                Assert.Equal(invalidCount, retry.FailedCount);
            }
        }
    }
}
