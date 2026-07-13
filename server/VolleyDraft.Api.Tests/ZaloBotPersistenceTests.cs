using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloBotPersistenceTests
{
    [Fact]
    public async Task Conversation_state_is_isolated_by_connection_group_and_sender()
    {
        await using var fixture = await DbFixture.CreateAsync();
        fixture.Db.ZaloBotConversationStates.AddRange(
            State("connection", "group-a", "user-a"),
            State("connection", "group-a", "user-b"),
            State("connection", "group-b", "user-a"));
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(3, await fixture.Db.ZaloBotConversationStates.CountAsync());
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            fixture.Db.ZaloBotConversationStates.Add(State("connection", "group-a", "user-a"));
            await fixture.Db.SaveChangesAsync();
        });
    }

    [Fact]
    public void Conversation_expiry_uses_absolute_utc_deadline()
    {
        var state = State("connection", "group", "user");
        state.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.True(state.ExpiresAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Incoming_message_id_is_unique_per_connection_for_idempotency()
    {
        await using var fixture = await DbFixture.CreateAsync();
        fixture.Db.ZaloGroupMessages.Add(Message("same-message"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ZaloGroupMessages.Add(Message("same-message"));
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Reminder_schedule_and_retry_state_are_persisted()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var next = DateTimeOffset.UtcNow.AddHours(6);
        fixture.Db.MatchSessions.Add(new MatchSession
        {
            Id = "reminder-session",
            AdminUserId = "admin",
            Name = "T6",
            ReminderEnabled = true,
            ReminderIntervalMinutes = 360,
            ReminderRepeats = true,
            NextReminderAt = next,
            ReminderLastKnownPlayerCount = 17,
            ReminderFailureCount = 2,
            LastReminderError = "bridge sleeping"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var stored = await fixture.Db.MatchSessions.SingleAsync(session => session.Id == "reminder-session");
        Assert.True(stored.ReminderEnabled);
        Assert.Equal(360, stored.ReminderIntervalMinutes);
        Assert.True(stored.ReminderRepeats);
        Assert.Equal(next, stored.NextReminderAt);
        Assert.Equal(17, stored.ReminderLastKnownPlayerCount);
        Assert.Equal(2, stored.ReminderFailureCount);
        Assert.Equal("bridge sleeping", stored.LastReminderError);
    }

    [Fact]
    public async Task Finished_draft_can_swap_two_regular_players_and_recalculate_team_scores()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = await SeedFinishedDraftAsync(fixture.Db);

        var result = await new SessionDraftService(fixture.Db).SwapDraftPlayersAsync(
            "admin",
            session.Id,
            "Thanh Tuyền",
            "Nick Tran");

        Assert.True(result.IsSuccess, result.Error);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("team-b", await fixture.Db.DraftSlots.Where(slot => slot.Id == "thanh-tuyen-slot").Select(slot => slot.AssignedTeamId).SingleAsync());
        Assert.Equal("team-a", await fixture.Db.DraftSlots.Where(slot => slot.Id == "nick-tran-slot").Select(slot => slot.AssignedTeamId).SingleAsync());
        Assert.Equal(5, await fixture.Db.Teams.Where(team => team.Id == "team-a").Select(team => team.TotalAverageScore).SingleAsync());
        Assert.Equal(3, await fixture.Db.Teams.Where(team => team.Id == "team-b").Select(team => team.TotalAverageScore).SingleAsync());
    }

    [Fact]
    public async Task Gender_only_confirmation_keeps_new_role_and_level_but_unblocks_profile()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession { Id = "profile-session", AdminUserId = "admin", Name = "T4" };
        var profile = new PlayerProfile
        {
            Id = "guest-profile",
            ZaloUserId = "guest-zalo",
            DisplayName = "Bạn của Nick Tran"
        };
        session.Players.Add(new SessionPlayer
        {
            Id = "guest",
            SessionId = session.Id,
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            DisplayName = "Bạn của Nick Tran",
            Gender = PlayerGender.Unknown,
            Role = PlayerRole.New,
            Level = PlayerLevel.New,
            IsPresent = true
        });
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var beforeUpdate = await service.GetIncompletePlayerProfilesAsync("admin", session.Id);

        var updated = await service.UpdatePlayerProfileFromBotAsync(
            "admin", session.Id, "Bạn của Nick Tran", PlayerGender.Male, null, null);
        var incomplete = await service.GetIncompletePlayerProfilesAsync("admin", session.Id);

        Assert.True(beforeUpdate.IsSuccess, beforeUpdate.Error);
        var missingProfile = Assert.Single(beforeUpdate.Value!);
        Assert.True(missingProfile.MissingGender);
        Assert.True(missingProfile.MissingRole);
        Assert.True(missingProfile.MissingLevel);
        Assert.True(updated.IsSuccess, updated.Error);
        Assert.Equal(PlayerGender.Male, updated.Value!.Gender);
        Assert.Equal(PlayerRole.New, updated.Value.Role);
        Assert.Equal(PlayerLevel.New, updated.Value.Level);
        Assert.Empty(incomplete.Value!);
        Assert.Equal(PlayerRole.New, profile.DefaultRole);
        Assert.Equal(PlayerLevel.New, profile.DefaultLevel);
    }

    [Fact]
    public async Task Auto_draft_is_blocked_before_mutation_when_gender_is_unknown()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession { Id = "blocked-session", AdminUserId = "admin", Name = "T4" };
        session.Players.Add(new SessionPlayer
        {
            SessionId = session.Id,
            DisplayName = "Chưa rõ",
            Gender = PlayerGender.Unknown,
            Role = PlayerRole.New,
            Level = PlayerLevel.New,
            IsPresent = true
        });
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();

        var result = await new SessionDraftService(fixture.Db).AutoRunDraftAsync("admin", session.Id);

        Assert.False(result.IsSuccess);
        Assert.Contains("Chưa rõ", result.Error);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(SessionStatus.Setup, await fixture.Db.MatchSessions.Where(item => item.Id == session.Id).Select(item => item.Status).SingleAsync());
    }

    [Fact]
    public async Task Authorized_guest_can_be_added_before_draft_and_requires_profile()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession { Id = "guest-session", AdminUserId = "admin", Name = "T4", TeamCount = 3 };
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var result = await service.AddGuestPlayerFromBotAsync("admin", session.Id, "Bạn của Nick Tran");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, result.Value!.PresentPlayerCount);
        Assert.Equal(PlayerGender.Unknown, result.Value.Player.Gender);
        Assert.Single((await service.GetIncompletePlayerProfilesAsync("admin", session.Id)).Value!);
    }

    [Fact]
    public async Task External_partner_can_join_existing_finished_team_as_shared_slot()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = await SeedFinishedDraftAsync(fixture.Db);
        var service = new SessionDraftService(fixture.Db);

        var result = await service.SharePostDraftSlotAsync(
            "admin", session.Id, "Nick Tran", "Bạn share cùng Nick Tran");

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value!.PartnerWasAdded);
        Assert.True(result.Value.NeedsProfileUpdate);
        fixture.Db.ChangeTracker.Clear();
        var slot = await fixture.Db.DraftSlots
            .Include(item => item.Players)
            .SingleAsync(item => item.Id == "nick-tran-slot");
        Assert.Equal(DraftSlotType.Shared, slot.Type);
        Assert.Equal(2, slot.Players.Count);
        Assert.Equal("team-b", slot.AssignedTeamId);
        Assert.Equal(6, await fixture.Db.SessionPlayers.CountAsync(player => player.SessionId == session.Id));
    }

    [Fact]
    public async Task Existing_player_can_move_from_another_team_into_shared_slot()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = await SeedFinishedDraftAsync(fixture.Db);

        var result = await new SessionDraftService(fixture.Db).SharePostDraftSlotAsync(
            "admin", session.Id, "Nick Tran", "Thanh Tuyền");

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.Value!.PartnerWasAdded);
        fixture.Db.ChangeTracker.Clear();
        Assert.Null(await fixture.Db.DraftSlots.Where(slot => slot.Id == "thanh-tuyen-slot").Select(slot => slot.AssignedTeamId).SingleAsync());
        var nickSlot = await fixture.Db.DraftSlots.Include(slot => slot.Players).SingleAsync(slot => slot.Id == "nick-tran-slot");
        Assert.Equal(DraftSlotType.Shared, nickSlot.Type);
        Assert.Equal(2, nickSlot.Players.Count);
        Assert.Equal("team-b", nickSlot.AssignedTeamId);
    }

    private static ZaloBotConversationState State(string connection, string group, string sender) => new()
    {
        ZaloConnectionId = connection,
        GroupId = group,
        SenderZaloUserId = sender,
        PendingIntent = "PaymentQr",
        PendingPayloadJson = "[]",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
    };

    private static ZaloGroupMessage Message(string messageId) => new()
    {
        ZaloConnectionId = "connection",
        GroupId = "group",
        MessageId = messageId,
        SenderId = "sender",
        SenderName = "Sender",
        Content = "@bot help",
        SentAt = DateTimeOffset.UtcNow
    };

    private static SessionPlayer Player(string id, string name, double score) => new()
    {
        Id = id,
        SessionId = "session",
        DisplayName = name,
        Score = score,
        Gender = PlayerGender.Male,
        Role = PlayerRole.Attack,
        Level = PlayerLevel.Average,
        IsPresent = true
    };

    private static async Task<MatchSession> SeedFinishedDraftAsync(VolleyDraftDbContext db)
    {
        var session = new MatchSession
        {
            Id = "session",
            AdminUserId = "admin",
            Name = "Hôm nay",
            Status = SessionStatus.Finished,
            TeamCount = 3,
            TeamSize = 2
        };
        var captainA = Player("captain-a", "Captain A", 2);
        var captainB = Player("captain-b", "Captain B", 2);
        var captainC = Player("captain-c", "Captain C", 2);
        var thanhTuyen = Player("thanh-tuyen", "Thanh Tuyền", 1);
        var nickTran = Player("nick-tran", "Nick Tran", 3);
        session.Players.AddRange([captainA, captainB, captainC, thanhTuyen, nickTran]);
        var teamA = new Team { Id = "team-a", SessionId = session.Id, Name = "Team A", CaptainSessionPlayerId = captainA.Id };
        var teamB = new Team { Id = "team-b", SessionId = session.Id, Name = "Team B", CaptainSessionPlayerId = captainB.Id };
        var teamC = new Team { Id = "team-c", SessionId = session.Id, Name = "Team C", CaptainSessionPlayerId = captainC.Id };
        session.Teams.AddRange([teamA, teamB, teamC]);
        session.DraftSlots.AddRange([
            Slot("captain-slot-a", captainA, teamA.Id, 2, true),
            Slot("captain-slot-b", captainB, teamB.Id, 2, true),
            Slot("captain-slot-c", captainC, teamC.Id, 2, true),
            Slot("thanh-tuyen-slot", thanhTuyen, teamA.Id, 1, false),
            Slot("nick-tran-slot", nickTran, teamB.Id, 3, false)
        ]);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static DraftSlot Slot(
        string id,
        SessionPlayer player,
        string teamId,
        double score,
        bool isCaptain)
    {
        var slot = new DraftSlot
        {
            Id = id,
            SessionId = "session",
            DisplayName = player.DisplayName,
            AssignedTeamId = teamId,
            AverageScore = score,
            IsCaptainSlot = isCaptain
        };
        slot.Players.Add(new DraftSlotPlayer
        {
            DraftSlotId = slot.Id,
            SessionPlayerId = player.Id,
            SessionPlayer = player
        });
        return slot;
    }

    private sealed class DbFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public VolleyDraftDbContext Db { get; }

        private DbFixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public static async Task<DbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(db);
            db.Users.Add(new User { Id = "admin", DisplayName = "Admin", Email = "admin@test.local", PasswordHash = "hash" });
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "connection",
                AdminUserId = "admin",
                AccountZaloId = "bot",
                DisplayName = "Bot",
                EncryptedCredentials = "encrypted",
                Status = ZaloConnectionStatus.Connected
            });
            await db.SaveChangesAsync();
            return new DbFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
