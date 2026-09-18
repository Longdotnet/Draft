using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftPassSlotFinalGateTests
{
    [Fact]
    public async Task Draft_uses_authoritative_roster_even_when_durable_pass_offer_is_active()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = "admin@example.test",
            PasswordHash = "test"
        };
        var zaloConnection = new ZaloConnection
        {
            Id = "conn-1",
            AdminUserId = admin.Id,
            AccountZaloId = "bot-uid",
            DisplayName = "NPC",
            EncryptedCredentials = "test"
        };
        db.Users.Add(admin);
        db.ZaloConnections.Add(zaloConnection);
        await db.SaveChangesAsync();

        var service = new SessionDraftService(db);
        var created = await service.CreateSessionAsync(admin.Id, new CreateSessionRequest("T6", 3, 2));
        Assert.True(created.IsSuccess);
        var sessionId = created.Value!.Id;
        var session = await db.MatchSessions.SingleAsync(item => item.Id == sessionId);
        session.ZaloConnectionId = zaloConnection.Id;
        session.ZaloGroupId = "g1";
        session.BotEnabled = true;
        await db.SaveChangesAsync();

        var playerIds = new List<string>();
        for (var i = 1; i <= 6; i++)
        {
            var added = await service.AddPlayerAsync(
                admin.Id,
                sessionId,
                new AddPlayerRequest($"P{i}", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
            Assert.True(added.IsSuccess);
            playerIds.Add(added.Value!.Id);
        }

        var captains = await service.SetManualCaptainsAsync(
            admin.Id,
            sessionId,
            new ManualCaptainsRequest(playerIds.Take(3).ToList()));
        Assert.True(captains.IsSuccess);

        var offerStore = new ZaloOpenSlotOfferStore(db);
        var offer = await offerStore.OpenAsync(
            zaloConnection.Id,
            "g1",
            "owner-that-already-unvoted",
            "Hoàng",
            sessionId,
            "T6",
            "m-pass",
            DateTimeOffset.UtcNow.AddHours(1),
            null);

        Assert.Equal(
            1,
            await new ZaloOpenSlotRiskCounter(db)
                .CountActiveForSessionAsync(zaloConnection.Id, "g1", sessionId));

        var started = await service.StartDraftAsync(admin.Id, sessionId);
        Assert.True(started.IsSuccess, started.Error);
        Assert.Equal(SessionStatus.Drafting, started.Value!.SessionStatus);
        Assert.Single(
            await db.DraftRounds.AsNoTracking()
                .Where(item => item.SessionId == sessionId)
                .ToListAsync());

        // The handoff ledger stays alive independently of draft and can continue later.
        Assert.True(await offerStore.TryClaimAsync(offer, "claimant-uid", "Bình", "m-claim"));
        Assert.Equal(
            1,
            await new ZaloOpenSlotRiskCounter(db)
                .CountActiveForSessionAsync(zaloConnection.Id, "g1", sessionId));

        var retry = await service.StartDraftAsync(admin.Id, sessionId);
        Assert.False(retry.IsSuccess);
        Assert.Single(
            await db.DraftRounds.AsNoTracking()
                .Where(item => item.SessionId == sessionId)
                .ToListAsync());
    }
}
