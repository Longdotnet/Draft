using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGuestDomainActionServiceTests
{
    [Fact]
    public async Task TentativeGuest_DoesNotOccupyRosterSlot()
    {
        await using var db = await CreateDbAsync(initialPlayers: 17);
        var session = await db.MatchSessions.SingleAsync();
        var service = new ZaloGuestDomainActionService(db);

        var result = await service.AddTentativeAsync(
            session,
            "sponsor-1",
            "Nick",
            "tentative-1",
            "recruitment-1",
            [new ZaloRecruitmentGuestSpec("Minh", PlayerGender.Male)],
            1,
            CancellationToken.None);

        Assert.False(result.Idempotent);
        var guest = Assert.Single(result.Changed);
        Assert.Equal(ZaloGuestReservationStatus.Tentative, guest.Status);
        Assert.Null(guest.SessionPlayerId);
        Assert.Equal(17, result.EffectiveSlots);
        Assert.Equal(17, await db.SessionPlayers.CountAsync(player => player.IsPresent));
    }

    [Fact]
    public async Task ConfirmTentative_WithRoom_ActivatesGuest_AndRetryIsIdempotent()
    {
        await using var db = await CreateDbAsync(initialPlayers: 17);
        var session = await db.MatchSessions.SingleAsync();
        var service = new ZaloGuestDomainActionService(db);
        var tentative = await service.AddTentativeAsync(
            session, "sponsor-1", "Nick", "tentative-1", "recruitment-1",
            [new ZaloRecruitmentGuestSpec("Minh", PlayerGender.Male)], 1, CancellationToken.None);
        var id = tentative.Changed.Single().Id;

        var confirmed = await service.ConfirmAsync(session, "sponsor-1", [id], CancellationToken.None);
        var retry = await service.ConfirmAsync(session, "sponsor-1", [id], CancellationToken.None);

        Assert.False(confirmed.Idempotent);
        Assert.True(retry.Idempotent);
        var row = await db.ZaloGuestReservations.SingleAsync(item => item.Id == id);
        Assert.Equal(ZaloGuestReservationStatus.Active, row.Status);
        Assert.NotNull(row.SessionPlayerId);
        Assert.Equal(18, confirmed.EffectiveSlots);
        Assert.Equal(18, await db.SessionPlayers.CountAsync(player => player.IsPresent));
    }

    [Fact]
    public async Task ConfirmTentative_WhenRosterFull_WaitlistsWithoutCreatingPlayer()
    {
        await using var db = await CreateDbAsync(initialPlayers: 18);
        var session = await db.MatchSessions.SingleAsync();
        var service = new ZaloGuestDomainActionService(db);
        var tentative = await service.AddTentativeAsync(
            session, "sponsor-1", "Nick", "tentative-1", "recruitment-1",
            [new ZaloRecruitmentGuestSpec("Minh")], 1, CancellationToken.None);
        var id = tentative.Changed.Single().Id;

        var confirmed = await service.ConfirmAsync(session, "sponsor-1", [id], CancellationToken.None);

        var row = await db.ZaloGuestReservations.SingleAsync(item => item.Id == id);
        Assert.Equal(ZaloGuestReservationStatus.Waitlisted, row.Status);
        Assert.Null(row.SessionPlayerId);
        Assert.Equal(18, confirmed.EffectiveSlots);
        Assert.Equal(18, await db.SessionPlayers.CountAsync(player => player.IsPresent));
    }

    [Fact]
    public async Task CancelTentative_LeavesRosterCountUnchanged()
    {
        await using var db = await CreateDbAsync(initialPlayers: 16);
        var session = await db.MatchSessions.SingleAsync();
        var service = new ZaloGuestDomainActionService(db);
        var tentative = await service.AddTentativeAsync(
            session, "sponsor-1", "Nick", "tentative-1", "recruitment-1",
            [new ZaloRecruitmentGuestSpec("Minh")], 1, CancellationToken.None);
        var id = tentative.Changed.Single().Id;

        var cancelled = await service.CancelTentativeAsync(session, "sponsor-1", [id], CancellationToken.None);
        var retry = await service.CancelTentativeAsync(session, "sponsor-1", [id], CancellationToken.None);

        Assert.False(cancelled.Idempotent);
        Assert.True(retry.Idempotent);
        Assert.Equal(ZaloGuestReservationStatus.Cancelled,
            (await db.ZaloGuestReservations.SingleAsync(item => item.Id == id)).Status);
        Assert.Equal(16, cancelled.EffectiveSlots);
        Assert.Equal(16, await db.SessionPlayers.CountAsync(player => player.IsPresent));
    }

    [Fact]
    public async Task ReplaceActiveGuest_IsAtomicFromRosterPerspective_AndRetryDoesNotDuplicate()
    {
        await using var db = await CreateDbAsync(initialPlayers: 17);
        var session = await db.MatchSessions.SingleAsync();
        var normal = new ZaloGuestReservationService(db);
        var added = await normal.AddAsync(
            session,
            "sponsor-1",
            "Nick",
            "add-minh",
            "recruitment-1",
            new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.Add,
                Guests: [new ZaloRecruitmentGuestSpec("Minh", PlayerGender.Male)]),
            CancellationToken.None);
        var old = added.Added.Single();
        Assert.Equal(18, await db.SessionPlayers.CountAsync(player => player.IsPresent));

        var domain = new ZaloGuestDomainActionService(db);
        var replaced = await domain.ReplaceAsync(
            session,
            "sponsor-1",
            "Nick",
            old.Id,
            "replace-1",
            "recruitment-1",
            new ZaloRecruitmentGuestSpec("Huy", PlayerGender.Male),
            CancellationToken.None);
        var retry = await domain.ReplaceAsync(
            session,
            "sponsor-1",
            "Nick",
            old.Id,
            "replace-1",
            "recruitment-1",
            new ZaloRecruitmentGuestSpec("Huy", PlayerGender.Male),
            CancellationToken.None);

        Assert.False(replaced.Idempotent);
        Assert.True(retry.Idempotent);
        Assert.Equal(18, replaced.EffectiveSlots);
        Assert.Equal(18, await db.SessionPlayers.CountAsync(player => player.IsPresent));
        Assert.Equal(ZaloGuestReservationStatus.Cancelled,
            (await db.ZaloGuestReservations.SingleAsync(item => item.Id == old.Id)).Status);
        var replacement = await db.ZaloGuestReservations.SingleAsync(item => item.SourceMessageId == "replace-1");
        Assert.Equal("Huy", replacement.DisplayName);
        Assert.Equal(ZaloGuestReservationStatus.Active, replacement.Status);
        Assert.Equal(1, await db.ZaloGuestReservations.CountAsync(item => item.SourceMessageId == "replace-1"));
    }

    private static async Task<VolleyDraftDbContext> CreateDbAsync(int initialPlayers)
    {
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new VolleyDraftDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        await ZaloGuestReservationSchemaPatch.EnsureAsync(db);

        var user = new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = "admin@example.test",
            PasswordHash = "x"
        };
        var connection = new ZaloConnection
        {
            Id = "connection-1",
            AdminUserId = user.Id,
            AdminUser = user,
            AccountZaloId = "bot-zalo",
            DisplayName = "Npc",
            EncryptedCredentials = "encrypted",
            Status = ZaloConnectionStatus.Connected
        };
        var session = new MatchSession
        {
            Id = "session-1",
            Name = "T7",
            AdminUserId = user.Id,
            AdminUser = user,
            ZaloConnectionId = connection.Id,
            ZaloConnection = connection,
            ZaloGroupId = "group-1",
            BotEnabled = true,
            StartTime = DateTimeOffset.UtcNow.AddHours(6),
            TeamCount = 3,
            TeamSize = 6,
            Status = SessionStatus.Setup
        };
        db.Users.Add(user);
        db.ZaloConnections.Add(connection);
        db.MatchSessions.Add(session);
        for (var index = 1; index <= initialPlayers; index += 1)
        {
            db.SessionPlayers.Add(new SessionPlayer
            {
                Id = $"player-{index}",
                SessionId = session.Id,
                DisplayName = $"Player {index}",
                Gender = PlayerGender.Male,
                Role = PlayerRole.New,
                Level = PlayerLevel.New,
                IsPresent = true
            });
        }
        await db.SaveChangesAsync();
        return db;
    }
}
