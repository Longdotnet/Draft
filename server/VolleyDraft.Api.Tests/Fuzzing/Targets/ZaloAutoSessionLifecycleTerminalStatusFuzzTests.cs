using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloAutoSessionLifecycleTerminalStatusFuzzTests
{
    [Fact]
    public async Task Non_created_proposal_cannot_keep_handed_off_lifecycle_authority()
    {
        const int seedCount = 64;
        var baseTime = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        var nonCreatedStatuses = new[]
        {
            ZaloPollSessionProposalStatus.Ignored,
            ZaloPollSessionProposalStatus.AwaitingApproval,
            ZaloPollSessionProposalStatus.Approved,
            ZaloPollSessionProposalStatus.Rejected,
            ZaloPollSessionProposalStatus.Superseded,
            ZaloPollSessionProposalStatus.Failed
        };

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            string proposalId;
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
                    Id = "session-a",
                    Name = "Completed lifecycle session",
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
                var proposal = await store.UpsertProposalAsync(new ZaloPollSessionProposalData
                {
                    TrackedGroupId = tracked.Id,
                    PollId = "poll-a",
                    PollQuestion = "CN 13/9",
                    PollCreatorId = "captain-a",
                    PollStructureHash = "hash-created",
                    PollUpdatedAtUnixMs = 100,
                    Status = ZaloPollSessionProposalStatus.Created
                });
                proposalId = proposal.Id;
                await store.AddLinkAsync(new ZaloAutoSessionLinkData(
                    "link-a",
                    tracked.Id,
                    "poll-a",
                    "option-1",
                    "session-a",
                    baseTime));

                var lifecycle = new ZaloAutoSessionLifecycleHandoffStoreV5(setupDb);
                await lifecycle.HandOffAsync(proposal.Id, "admin-a", "session-a");
                Assert.True(await lifecycle.HasHandedOffOwnershipAsync(proposal.Id));
            }

            var nextStatus = nonCreatedStatuses[random.NextInt(nonCreatedStatuses.Length)];
            await using (var mutateDb = new VolleyDraftDbContext(options))
            {
                await new ZaloAutoSessionStore(mutateDb).EnsureAsync();
                await mutateDb.Database.ExecuteSqlInterpolatedAsync($$"""
                    UPDATE "ZaloPollSessionProposals"
                    SET "Status" = {{nextStatus.ToString()}},
                        "PollStructureHash" = 'hash-new-authority',
                        "PollUpdatedAtUnixMs" = 200,
                        "UpdatedAt" = {{baseTime.AddMinutes(5).ToString("O")}}
                    WHERE "Id" = {{proposalId}};
                    """);
            }

            // A stale aggregate must lose read authority immediately after the durable proposal
            // leaves Created. Reconciliation then removes only the aggregate; historical
            // per-session handoff evidence remains available for diagnostics/provenance.
            await using (var restartDb = new VolleyDraftDbContext(options))
            {
                var lifecycle = new ZaloAutoSessionLifecycleHandoffStoreV5(restartDb);
                Assert.False(await lifecycle.HasHandedOffOwnershipAsync(proposalId));

                await lifecycle.ReconcileCompletedOwnershipsAsync();

                Assert.False(await lifecycle.HasHandedOffOwnershipAsync(proposalId));
                Assert.Empty(await lifecycle.GetMissingAsync(limit: 10));
                Assert.Equal(0L, await CountByProposalAsync(
                    restartDb,
                    "ZaloAutoSessionLifecycleOwnerships",
                    proposalId));
                Assert.Equal(1L, await CountByProposalAsync(
                    restartDb,
                    "ZaloAutoSessionLifecycleHandoffs",
                    proposalId));
            }
        }
    }

    private static async Task<long> CountByProposalAsync(
        VolleyDraftDbContext db,
        string tableName,
        string proposalId)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\" WHERE \"ProposalId\" = @ProposalId;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@ProposalId";
        parameter.Value = proposalId;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
