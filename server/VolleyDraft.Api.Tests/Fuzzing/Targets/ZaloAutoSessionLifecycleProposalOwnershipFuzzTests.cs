using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloAutoSessionLifecycleProposalOwnershipFuzzTests
{
    [Fact]
    public async Task One_session_cannot_become_terminal_for_two_created_proposals()
    {
        const int seedCount = 64;
        var baseTime = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            string proposalAId;
            string proposalBId;
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
                setupDb.MatchSessions.Add(new MatchSession
                {
                    Id = "session-shared",
                    Name = "Cancelled shared session",
                    AdminUserId = "admin-a",
                    Status = SessionStatus.Cancelled,
                    CreatedAt = baseTime,
                    UpdatedAt = baseTime
                });
                await setupDb.SaveChangesAsync();

                var tracked = await new ZaloAutoSessionSettingsStore(setupDb).InsertIfMissingAsync(new ZaloTrackedGroupData
                {
                    AdminUserId = "admin-a",
                    ZaloConnectionId = "connection-a",
                    GroupId = "group-a",
                    GroupName = "Bóng UTE"
                });
                var store = new ZaloAutoSessionStore(setupDb);

                var polls = random.NextBool()
                    ? new[] { "poll-a", "poll-b" }
                    : new[] { "poll-b", "poll-a" };
                var proposalIds = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var index = 0; index < polls.Length; index++)
                {
                    var pollId = polls[index];
                    var proposal = await store.UpsertProposalAsync(new ZaloPollSessionProposalData
                    {
                        TrackedGroupId = tracked.Id,
                        PollId = pollId,
                        PollQuestion = pollId,
                        PollCreatorId = "captain-a",
                        PollStructureHash = $"hash-{pollId}",
                        Status = ZaloPollSessionProposalStatus.Created
                    });
                    proposalIds[pollId] = proposal.Id;
                    await store.AddLinkAsync(new ZaloAutoSessionLinkData(
                        $"link-{pollId}",
                        tracked.Id,
                        pollId,
                        "option-1",
                        "session-shared",
                        baseTime.AddMinutes(index + 1)));
                }

                proposalAId = proposalIds["poll-a"];
                proposalBId = proposalIds["poll-b"];
            }

            await using (var cycleDb = new VolleyDraftDbContext(options))
            {
                var lifecycle = new ZaloAutoSessionLifecycleHandoffStoreV5(cycleDb);
                var result = await lifecycle.ReconcileMissingAsync(NullLogger.Instance);

                Assert.Equal(2, result.CandidateCount);
                Assert.Equal(1, result.HandedOffCount);
                Assert.Equal(1, result.FailedCount);

                var aTerminal = await lifecycle.HasHandedOffOwnershipAsync(proposalAId);
                var bTerminal = await lifecycle.HasHandedOffOwnershipAsync(proposalBId);
                Assert.NotEqual(aTerminal, bTerminal);
            }

            await using (var restartDb = new VolleyDraftDbContext(options))
            {
                var lifecycle = new ZaloAutoSessionLifecycleHandoffStoreV5(restartDb);

                // The first cycle must surface the structural ownership conflict, but once one
                // proposal owns the durable SessionId handoff the losing proposal must not poison
                // every future scheduler cycle with the same deterministic conflict forever.
                var remaining = await lifecycle.GetMissingAsync(limit: 10);
                Assert.Empty(remaining);

                var retry = await lifecycle.ReconcileMissingAsync(NullLogger.Instance);
                Assert.Equal(0, retry.CandidateCount);
                Assert.Equal(0, retry.HandedOffCount);
                Assert.Equal(0, retry.FailedCount);

                var aTerminal = await lifecycle.HasHandedOffOwnershipAsync(proposalAId);
                var bTerminal = await lifecycle.HasHandedOffOwnershipAsync(proposalBId);
                Assert.NotEqual(aTerminal, bTerminal);

                // Simulate the stale aggregate that older builds could leave behind after moving
                // the same SessionId handoff from one proposal to another. Reconciliation must
                // prune unsupported terminal ownership instead of trusting the aggregate forever.
                var unsupportedProposalId = aTerminal ? proposalBId : proposalAId;
                await restartDb.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO "ZaloAutoSessionLifecycleOwnerships"
                        ("ProposalId", "State", "LinkedSessionCount", "HandedOffAt")
                    VALUES
                        ({{unsupportedProposalId}}, 'HandedOff', 1, {{baseTime.ToString("O")}})
                    ON CONFLICT ("ProposalId") DO UPDATE SET
                        "State" = excluded."State",
                        "LinkedSessionCount" = excluded."LinkedSessionCount",
                        "HandedOffAt" = excluded."HandedOffAt";
                    """);
                Assert.True(await lifecycle.HasHandedOffOwnershipAsync(unsupportedProposalId));

                await lifecycle.ReconcileCompletedOwnershipsAsync();
                Assert.False(await lifecycle.HasHandedOffOwnershipAsync(unsupportedProposalId));
                Assert.True(await lifecycle.HasHandedOffOwnershipAsync(
                    aTerminal ? proposalAId : proposalBId));
            }
        }
    }
}
