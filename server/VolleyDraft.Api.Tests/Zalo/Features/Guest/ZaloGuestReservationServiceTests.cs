using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGuestReservationServiceTests
{
    [Fact]
    public async Task SeventeenOfEighteen_PlusTwo_AdmitsOneAndWaitlistsOne()
    {
        await using var db = await CreateDbAsync(initialPlayers: 17);
        var session = await db.MatchSessions.SingleAsync();
        var command = new ZaloRecruitmentGuestCommand(
            ZaloRecruitmentGuestCommandKind.Add,
            Quantity: 2,
            Guests:
            [
                new ZaloRecruitmentGuestSpec("Minh", PlayerGender.Male),
                new ZaloRecruitmentGuestSpec("Huy")
            ]);

        var result = await new ZaloGuestReservationService(db).AddAsync(
            session,
            "sponsor-1",
            "Nick",
            "message-1",
            "recruitment-1",
            command,
            CancellationToken.None);

        Assert.Equal(17, result.BeforeEffectiveSlots);
        Assert.Equal(18, result.AfterEffectiveSlots);
        Assert.Equal(18, result.Capacity);
        Assert.Single(result.Added);
        Assert.Single(result.Waitlisted);
        Assert.Equal("Minh", result.Added[0].DisplayName);
        Assert.Equal(PlayerGender.Male, result.Added[0].Gender);
        Assert.Equal("Huy", result.Waitlisted[0].DisplayName);
        Assert.Equal(18, await db.SessionPlayers.CountAsync(player => player.IsPresent));
    }

    [Fact]
    public async Task DuplicateIncomingMessage_IsIdempotent()
    {
        await using var db = await CreateDbAsync(initialPlayers: 16);
        var session = await db.MatchSessions.SingleAsync();
        var command = new ZaloRecruitmentGuestCommand(
            ZaloRecruitmentGuestCommandKind.Add,
            Quantity: 2,
            Guests: [new ZaloRecruitmentGuestSpec(), new ZaloRecruitmentGuestSpec()]);
        var service = new ZaloGuestReservationService(db);

        var first = await service.AddAsync(
            session, "sponsor-1", "Nick", "same-message", "recruitment-1", command, CancellationToken.None);
        var retry = await service.AddAsync(
            session, "sponsor-1", "Nick", "same-message", "recruitment-1", command, CancellationToken.None);

        Assert.False(first.Idempotent);
        Assert.True(retry.Idempotent);
        Assert.Equal(2, await db.ZaloGuestReservations.CountAsync());
        Assert.Equal(18, await db.SessionPlayers.CountAsync(player => player.IsPresent));
    }

    [Fact]
    public async Task MissingNames_GetStableSponsorSequences_AndGenderCanBeCompletedLater()
    {
        await using var db = await CreateDbAsync(initialPlayers: 15);
        var session = await db.MatchSessions.SingleAsync();
        var service = new ZaloGuestReservationService(db);
        await service.AddAsync(
            session,
            "sponsor-1",
            "Nick",
            "message-1",
            "recruitment-1",
            new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.Add,
                Quantity: 2,
                Guests: [new ZaloRecruitmentGuestSpec(), new ZaloRecruitmentGuestSpec()]),
            CancellationToken.None);

        var update = await service.UpdateProfileAsync(
            session,
            "sponsor-1",
            new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.UpdateProfile,
                SponsorSequence: 1,
                Gender: PlayerGender.Female),
            CancellationToken.None);

        Assert.False(update.NeedsClarification);
        Assert.Single(update.Changed);
        Assert.Equal(1, update.Changed[0].SponsorSequence);
        Assert.Equal(PlayerGender.Female, update.Changed[0].Gender);
        var linkedPlayer = await db.SessionPlayers.SingleAsync(player => player.Id == update.Changed[0].SessionPlayerId);
        Assert.Equal(PlayerGender.Female, linkedPlayer.Gender);
    }

    [Fact]
    public async Task CancellingOneOfMultipleGuests_RequiresClarification()
    {
        await using var db = await CreateDbAsync(initialPlayers: 15);
        var session = await db.MatchSessions.SingleAsync();
        var service = new ZaloGuestReservationService(db);
        await service.AddAsync(
            session,
            "sponsor-1",
            "Nick",
            "message-1",
            "recruitment-1",
            new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.Add,
                Quantity: 2,
                Guests: [new ZaloRecruitmentGuestSpec("Minh"), new ZaloRecruitmentGuestSpec("Huy")]),
            CancellationToken.None);

        var cancel = await service.CancelAsync(
            session,
            "sponsor-1",
            new ZaloRecruitmentGuestCommand(ZaloRecruitmentGuestCommandKind.Cancel),
            CancellationToken.None);

        Assert.True(cancel.NeedsClarification);
        Assert.Empty(cancel.Changed);
        Assert.Equal(17, await db.SessionPlayers.CountAsync(player => player.IsPresent));
    }

    [Fact]
    public async Task ExactUniqueNamedGuest_IsCollapsedWhenSamePersonLaterAppearsFromPoll()
    {
        await using var db = await CreateDbAsync(initialPlayers: 15);
        var session = await db.MatchSessions.SingleAsync();
        var service = new ZaloGuestReservationService(db);
        var added = await service.AddAsync(
            session,
            "sponsor-1",
            "Nick",
            "message-1",
            "recruitment-1",
            new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.Add,
                Guests: [new ZaloRecruitmentGuestSpec("Minh", PlayerGender.Male)]),
            CancellationToken.None);
        var manualPlayerId = added.Added.Single().SessionPlayerId!;

        var profile = new PlayerProfile
        {
            Id = "profile-minh",
            ZaloUserId = "zalo-minh",
            DisplayName = "Minh",
            Gender = PlayerGender.Male,
            DefaultRole = PlayerRole.New,
            DefaultLevel = PlayerLevel.New
        };
        var pollPlayer = new SessionPlayer
        {
            Id = "poll-minh",
            SessionId = session.Id,
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            DisplayName = "Minh",
            Gender = PlayerGender.Male,
            Role = PlayerRole.New,
            Level = PlayerLevel.New,
            IsPresent = true,
            SourcePollId = "poll-1"
        };
        db.PlayerProfiles.Add(profile);
        db.SessionPlayers.Add(pollPlayer);
        await db.SaveChangesAsync();

        var changed = await new ZaloGuestIdentityReconciler(db).ReconcileAsync(session.Id);

        Assert.Equal(1, changed);
        Assert.False((await db.SessionPlayers.SingleAsync(item => item.Id == manualPlayerId)).IsPresent);
        var reservation = await db.ZaloGuestReservations.SingleAsync();
        Assert.Equal(ZaloGuestReservationStatus.Linked, reservation.Status);
        Assert.Equal(pollPlayer.Id, reservation.SessionPlayerId);
        Assert.Equal(16, await db.SessionPlayers.CountAsync(player => player.IsPresent));
    }

    [Fact]
    public async Task GeneratedPlaceholder_IsNeverAutoLinkedByNameGuessing()
    {
        await using var db = await CreateDbAsync(initialPlayers: 15);
        var session = await db.MatchSessions.SingleAsync();
        var service = new ZaloGuestReservationService(db);
        var added = await service.AddAsync(
            session,
            "sponsor-1",
            "Nick",
            "message-1",
            "recruitment-1",
            new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.Add,
                Guests: [new ZaloRecruitmentGuestSpec()]),
            CancellationToken.None);
        var placeholder = added.Added.Single();

        var profile = new PlayerProfile
        {
            Id = "profile-placeholder",
            ZaloUserId = "zalo-placeholder",
            DisplayName = placeholder.DisplayName,
            Gender = PlayerGender.Male,
            DefaultRole = PlayerRole.New,
            DefaultLevel = PlayerLevel.New
        };
        db.PlayerProfiles.Add(profile);
        db.SessionPlayers.Add(new SessionPlayer
        {
            Id = "poll-placeholder",
            SessionId = session.Id,
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            DisplayName = placeholder.DisplayName,
            Gender = PlayerGender.Male,
            Role = PlayerRole.New,
            Level = PlayerLevel.New,
            IsPresent = true,
            SourcePollId = "poll-1"
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await new ZaloGuestIdentityReconciler(db).ReconcileAsync(session.Id));
        Assert.Equal(ZaloGuestReservationStatus.Active, (await db.ZaloGuestReservations.SingleAsync()).Status);
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
