using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class PassSlotClaimRaceFuzzTests
{
    [Fact]
    public async Task Concurrent_claimers_cannot_both_own_one_pass_slot_or_cross_the_draft_gate()
    {
        var connectionString = $"Data Source=pass-slot-claim-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connectionString)
            .Options;

        const string adminId = "fuzz-claim-race-admin";
        const string connectionId = "fuzz-claim-race-conn";
        const string groupId = "fuzz-claim-race-group";
        string sessionId;

        await using (var seedDb = new VolleyDraftDbContext(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Users.Add(new User
            {
                Id = adminId,
                DisplayName = "Fuzz Claim Race Admin",
                Email = "fuzz-claim-race-admin@example.test",
                PasswordHash = "test"
            });
            seedDb.ZaloConnections.Add(new ZaloConnection
            {
                Id = connectionId,
                AdminUserId = adminId,
                AccountZaloId = "fuzz-claim-race-bot",
                DisplayName = "NPC",
                EncryptedCredentials = "test"
            });
            await seedDb.SaveChangesAsync();

            var draft = new SessionDraftService(seedDb);
            var created = await draft.CreateSessionAsync(adminId, new CreateSessionRequest("T6", 3, 2));
            Assert.True(created.IsSuccess, created.Error);
            sessionId = created.Value!.Id;
            var session = await seedDb.MatchSessions.SingleAsync(item => item.Id == sessionId);
            session.ZaloConnectionId = connectionId;
            session.ZaloGroupId = groupId;
            session.BotEnabled = true;
            await seedDb.SaveChangesAsync();

            var playerIds = new List<string>();
            for (var index = 1; index <= 6; index += 1)
            {
                var added = await draft.AddPlayerAsync(
                    adminId,
                    sessionId,
                    new AddPlayerRequest($"P{index}", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
                Assert.True(added.IsSuccess, added.Error);
                playerIds.Add(added.Value!.Id);
            }

            var captains = await draft.SetManualCaptainsAsync(
                adminId,
                sessionId,
                new ManualCaptainsRequest(playerIds.Take(3).ToList()));
            Assert.True(captains.IsSuccess, captains.Error);
        }

        for (var seed = 1; seed <= 128; seed += 1)
        {
            await using var primary = new VolleyDraftDbContext(options);
            await using var secondary = new VolleyDraftDbContext(options);
            await using var verifier = new VolleyDraftDbContext(options);
            var ownerStore = new ZaloOpenSlotOfferStore(primary);
            var rivalStore = new ZaloOpenSlotOfferStore(secondary);
            var verifyStore = new ZaloOpenSlotOfferStore(verifier);

            var offer = await ownerStore.OpenAsync(
                connectionId,
                groupId,
                "owner-uid",
                "Owner",
                sessionId,
                "T6",
                $"open-{seed}",
                DateTimeOffset.UtcNow.AddHours(1),
                null);

            var firstClaimantId = seed % 2 == 0 ? $"claimant-a-{seed}" : $"claimant-b-{seed}";
            var secondClaimantId = seed % 2 == 0 ? $"claimant-b-{seed}" : $"claimant-a-{seed}";
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = Task.Run(async () =>
            {
                await start.Task;
                return await ownerStore.TryClaimAsync(
                    offer,
                    firstClaimantId,
                    $"First {seed}",
                    $"claim-first-{seed}");
            });
            var second = Task.Run(async () =>
            {
                await start.Task;
                return await rivalStore.TryClaimAsync(
                    offer,
                    secondClaimantId,
                    $"Second {seed}",
                    $"claim-second-{seed}");
            });

            start.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.Equal(1, results.Count(result => result));

            var firstPending = await verifyStore.LoadPendingClaimAsync(connectionId, groupId, firstClaimantId);
            var secondPending = await verifyStore.LoadPendingClaimAsync(connectionId, groupId, secondClaimantId);
            Assert.Equal(1, new[] { firstPending, secondPending }.Count(item => item is not null));
            var winner = firstPending ?? secondPending!;
            Assert.Equal(ZaloOpenSlotOfferStatus.ClaimPending, winner.Status);
            Assert.Equal(offer.Version + 1, winner.Version);

            var activeRisk = await new ZaloOpenSlotRiskCounter(verifier)
                .CountActiveForSessionAsync(connectionId, groupId, sessionId);
            Assert.Equal(1, activeRisk);

            var blocked = await new SessionDraftService(verifier).StartDraftAsync(adminId, sessionId);
            Assert.False(blocked.IsSuccess);
            Assert.Equal(409, blocked.StatusCode);
            Assert.Equal(
                SessionStatus.CaptainSelection,
                (await verifier.MatchSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId)).Status);
            Assert.Empty(await verifier.DraftRounds.AsNoTracking().Where(item => item.SessionId == sessionId).ToListAsync());

            Assert.True(await verifyStore.CancelAsync(offer.Id, "owner-uid"));
            Assert.Equal(
                0,
                await new ZaloOpenSlotRiskCounter(verifier)
                    .CountActiveForSessionAsync(connectionId, groupId, sessionId));
        }

        await using var finalDb = new VolleyDraftDbContext(options);
        var finalDraft = await new SessionDraftService(finalDb).StartDraftAsync(adminId, sessionId);
        Assert.True(finalDraft.IsSuccess, finalDraft.Error);
        Assert.Equal(SessionStatus.Drafting, (await finalDb.MatchSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId)).Status);
        Assert.Single(await finalDb.DraftRounds.AsNoTracking().Where(item => item.SessionId == sessionId).ToListAsync());
    }
}
