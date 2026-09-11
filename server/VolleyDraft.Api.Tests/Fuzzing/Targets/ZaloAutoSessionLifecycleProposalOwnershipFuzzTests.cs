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
                foreach (var pollId in polls)
                {
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
                        baseTime.AddMinutes(pollId == "poll-a" ? 1 : 2)));
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
                var remaining = await lifecycle.GetMissingAsync(limit: 10);
                Assert.Single(remaining);
                Assert.Equal("session-shared", remaining[0].SessionId);

                var aTerminal = await lifecycle.HasHandedOffOwnershipAsync(proposalAId);
                var bTerminal = await lifecycle.HasHandedOffOwnershipAsync(proposalBId);
                Assert.NotEqual(aTerminal, bTerminal);
            }
        }
    }
}
