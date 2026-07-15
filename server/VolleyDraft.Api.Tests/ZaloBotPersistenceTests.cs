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
    public async Task Natural_reminder_persists_stop_when_full_policy()
    {
        await using var fixture = await DbFixture.CreateAsync();
        fixture.Db.MatchSessions.Add(new MatchSession
        {
            Id = "natural-reminder-session",
            AdminUserId = "admin",
            Name = "T6"
        });
        fixture.Db.ZaloReminderSchedules.Add(new ZaloReminderSchedule
        {
            Id = "stop-when-full",
            SessionId = "natural-reminder-session",
            CreatedBySenderId = "sender",
            CreatedBySenderName = "Thanh Long",
            OnlyIfMissingSlots = true,
            StopWhenFull = true,
            Repeats = true,
            IntervalMinutes = 360,
            NextRunAt = DateTimeOffset.UtcNow.AddHours(6)
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var stored = await fixture.Db.ZaloReminderSchedules.SingleAsync();

        Assert.True(stored.StopWhenFull);
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
    public async Task Finished_draft_can_preview_and_confirm_two_team_rebalance_without_touching_other_team_or_splitting_share()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession
        {
            Id = "rebalance-session",
            AdminUserId = "admin",
            Name = "Thứ 4 15/7",
            Status = SessionStatus.Finished,
            TeamCount = 3,
            TeamSize = 3
        };
        var players = new[]
        {
            PlayerForSession("ra-cap", "Captain A", 2, session.Id),
            PlayerForSession("ra-one", "A One", 2, session.Id),
            PlayerForSession("rb-cap", "Captain B", 2, session.Id),
            PlayerForSession("rb-high", "B High", 5, session.Id),
            PlayerForSession("rb-share", "B Share", 5, session.Id),
            PlayerForSession("rb-mid", "B Mid", 4, session.Id),
            PlayerForSession("rc-cap", "Captain C", 2, session.Id),
            PlayerForSession("rc-low", "C Low", 1, session.Id),
            PlayerForSession("rc-mid", "C Mid", 2, session.Id)
        };
        session.Players.AddRange(players);
        var teamA = new Team { Id = "rebalance-a", SessionId = session.Id, Name = "Team A", CaptainSessionPlayerId = "ra-cap" };
        var teamB = new Team { Id = "rebalance-b", SessionId = session.Id, Name = "Team B", CaptainSessionPlayerId = "rb-cap" };
        var teamC = new Team { Id = "rebalance-c", SessionId = session.Id, Name = "Team C", CaptainSessionPlayerId = "rc-cap" };
        session.Teams.AddRange([teamA, teamB, teamC]);
        session.DraftSlots.AddRange([
            SlotForSession("ra-cap-slot", players[0], teamA.Id, 2, true, session.Id),
            SlotForSession("ra-one-slot", players[1], teamA.Id, 2, false, session.Id),
            SlotForSession("rb-cap-slot", players[2], teamB.Id, 2, true, session.Id),
            SlotForSession("rb-high-slot", players[3], teamB.Id, 5, false, session.Id),
            SlotForSession("rb-mid-slot", players[5], teamB.Id, 4, false, session.Id),
            SlotForSession("rc-cap-slot", players[6], teamC.Id, 2, true, session.Id),
            SlotForSession("rc-low-slot", players[7], teamC.Id, 1, false, session.Id),
            SlotForSession("rc-mid-slot", players[8], teamC.Id, 2, false, session.Id)
        ]);
        var sharedSlot = session.DraftSlots.Single(slot => slot.Id == "rb-high-slot");
        sharedSlot.Type = DraftSlotType.Shared;
        sharedSlot.DisplayName = "B High / B Share";
        sharedSlot.Players.Add(new DraftSlotPlayer
        {
            DraftSlotId = sharedSlot.Id,
            SessionPlayerId = players[4].Id,
            SessionPlayer = players[4],
            RotationOrder = 1
        });
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var preview = await service.PreviewTeamRebalanceAsync("admin", session.Id, 2, 3);

        Assert.True(preview.IsSuccess, preview.Error);
        Assert.NotNull(preview.Value);
        Assert.NotEmpty(preview.Value!.Moves);
        Assert.True(
            Math.Abs(preview.Value.FirstAfterScore - preview.Value.SecondAfterScore) <
            Math.Abs(preview.Value.FirstBeforeScore - preview.Value.SecondBeforeScore));
        var applied = await service.ApplyTeamRebalanceAsync("admin", preview.Value);
        Assert.True(applied.IsSuccess, applied.Error);

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("rebalance-a", await fixture.Db.DraftSlots
            .Where(slot => slot.Id == "ra-one-slot")
            .Select(slot => slot.AssignedTeamId)
            .SingleAsync());
        Assert.Equal("rebalance-b", await fixture.Db.DraftSlots
            .Where(slot => slot.Id == "rb-cap-slot")
            .Select(slot => slot.AssignedTeamId)
            .SingleAsync());
        Assert.Equal("rebalance-c", await fixture.Db.DraftSlots
            .Where(slot => slot.Id == "rc-cap-slot")
            .Select(slot => slot.AssignedTeamId)
            .SingleAsync());
        Assert.Equal(3, await fixture.Db.DraftSlots.CountAsync(slot => slot.AssignedTeamId == "rebalance-b"));
        Assert.Equal(3, await fixture.Db.DraftSlots.CountAsync(slot => slot.AssignedTeamId == "rebalance-c"));
        Assert.Equal(2, await fixture.Db.DraftSlotPlayers.CountAsync(link => link.DraftSlotId == "rb-high-slot"));
        var teamBScore = await fixture.Db.Teams.Where(team => team.Id == "rebalance-b").Select(team => team.TotalAverageScore).SingleAsync();
        var teamCScore = await fixture.Db.Teams.Where(team => team.Id == "rebalance-c").Select(team => team.TotalAverageScore).SingleAsync();
        Assert.Equal(preview.Value.FirstAfterScore, teamBScore, 6);
        Assert.Equal(preview.Value.SecondAfterScore, teamCScore, 6);
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
    public async Task Plus_two_before_draft_adds_two_named_people_but_keeps_eighteen_effective_slots()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession
        {
            Id = "pre-draft-share",
            AdminUserId = "admin",
            Name = "T6",
            TeamCount = 3,
            TeamSize = 6
        };
        for (var index = 1; index <= 18; index += 1)
        {
            session.Players.Add(new SessionPlayer
            {
                Id = $"player-{index}",
                SessionId = session.Id,
                DisplayName = index == 1 ? "Nick Tran" : $"Player {index}",
                Gender = PlayerGender.Male,
                Role = PlayerRole.Attack,
                Level = PlayerLevel.Average,
                Score = 2,
                IsPresent = true
            });
        }
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var result = await service.SharePreDraftSlotAsync(
            "admin",
            session.Id,
            "Nick Tran",
            [
                new ShareSlotParticipantInput("An", "zalo-an"),
                new ShareSlotParticipantInput("Bình", "zalo-binh")
            ]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(20, result.Value!.PresentPlayerCount);
        Assert.Equal(18, result.Value.EffectiveSlotCount);
        Assert.Equal(["An", "Bình"], result.Value.NewlyAddedPlayerNames);
        Assert.Equal(2, result.Value.NeedsProfileUpdateNames.Count);
        fixture.Db.ChangeTracker.Clear();
        var sharedSlot = await fixture.Db.DraftSlots
            .Include(slot => slot.Players)
            .SingleAsync(slot => slot.SessionId == session.Id && slot.Type == DraftSlotType.Shared);
        Assert.Equal(3, sharedSlot.Players.Count);
        Assert.Equal(2, await fixture.Db.PlayerProfiles.CountAsync(profile =>
            profile.ZaloUserId == "zalo-an" || profile.ZaloUserId == "zalo-binh"));

        Assert.True((await service.UpdatePlayerProfileFromBotAsync(
            "admin", session.Id, "An", PlayerGender.Male, null, null)).IsSuccess);
        Assert.True((await service.UpdatePlayerProfileFromBotAsync(
            "admin", session.Id, "Bình", PlayerGender.Female, null, null)).IsSuccess);
        var drafted = await service.AutoRunDraftAsync("admin", session.Id);
        Assert.True(drafted.IsSuccess, drafted.Error);
        Assert.Equal(SessionStatus.Finished, drafted.Value!.SessionStatus);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(18, await fixture.Db.DraftSlots.CountAsync(slot => slot.SessionId == session.Id));
        Assert.Equal(20, await fixture.Db.SessionPlayers.CountAsync(player => player.SessionId == session.Id && player.IsPresent));
    }

    [Fact]
    public async Task Redraft_uses_shared_slot_membership_and_never_duplicates_anchor_or_drops_player()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession
        {
            Id = "redraft-shared-slot",
            AdminUserId = "admin",
            Name = "Thứ 4 15/7",
            TeamCount = 3,
            TeamSize = 6
        };
        for (var index = 1; index <= 18; index += 1)
        {
            var name = index switch
            {
                4 => "Vinh",
                5 => "Thanh Long",
                _ => $"Player {index}"
            };
            session.Players.Add(new SessionPlayer
            {
                Id = $"redraft-player-{index}",
                SessionId = session.Id,
                DisplayName = name,
                AvatarUrl = $"https://avatars.example/{index}.jpg",
                Gender = index % 2 == 0 ? PlayerGender.Male : PlayerGender.Female,
                Role = PlayerRole.Attack,
                Level = index <= 3 ? PlayerLevel.Good : PlayerLevel.Average,
                Score = index <= 3 ? 3 : 2,
                IsPresent = true,
                IsCaptainEligible = true
            });
        }
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var firstDraft = await service.AutoRunDraftAsync("admin", session.Id);
        Assert.True(firstDraft.IsSuccess, firstDraft.Error);

        var shared = await service.SharePostDraftSlotAsync(
            "admin",
            session.Id,
            "Vinh",
            new ShareSlotParticipantInput("Vivian", "zalo-vivian", "https://avatars.example/vivian.jpg"));
        Assert.True(shared.IsSuccess, shared.Error);
        Assert.True((await service.UpdatePlayerProfileFromBotAsync(
            "admin", session.Id, "Vivian", PlayerGender.Female, PlayerRole.Defense, PlayerLevel.New)).IsSuccess);

        // Simulate data written by an older deployment where the anchor's denormalized
        // flag was stale although its DraftSlotPlayer relationship was correct.
        fixture.Db.ChangeTracker.Clear();
        await fixture.Db.SessionPlayers
            .Where(player => player.Id == "redraft-player-4")
            .ExecuteUpdateAsync(updates => updates.SetProperty(player => player.IsInsideSharedSlot, false));

        var redraft = await service.AutoRunDraftAsync("admin", session.Id, restart: true);
        Assert.True(redraft.IsSuccess, redraft.Error);

        fixture.Db.ChangeTracker.Clear();
        var assignedLinks = await fixture.Db.DraftSlotPlayers
            .AsNoTracking()
            .Where(link => link.DraftSlot.SessionId == session.Id && link.DraftSlot.AssignedTeamId != null)
            .Select(link => new
            {
                link.SessionPlayerId,
                link.SessionPlayer.DisplayName,
                link.DraftSlotId,
                link.DraftSlot.Type
            })
            .ToListAsync();

        Assert.Equal(19, assignedLinks.Count);
        Assert.Equal(19, assignedLinks.Select(link => link.SessionPlayerId).Distinct().Count());
        Assert.Single(assignedLinks, link => link.DisplayName == "Vinh");
        Assert.Single(assignedLinks, link => link.DisplayName == "Vivian");
        Assert.Single(assignedLinks, link => link.DisplayName == "Thanh Long");
        Assert.Equal(
            assignedLinks.Single(link => link.DisplayName == "Vinh").DraftSlotId,
            assignedLinks.Single(link => link.DisplayName == "Vivian").DraftSlotId);

        var vivian = await fixture.Db.SessionPlayers
            .AsNoTracking()
            .Include(player => player.PlayerProfile)
            .SingleAsync(player => player.SessionId == session.Id && player.DisplayName == "Vivian");
        Assert.Equal("https://avatars.example/vivian.jpg", vivian.AvatarUrl);
        Assert.Equal("https://avatars.example/vivian.jpg", vivian.PlayerProfile!.AvatarUrl);
    }

    [Fact]
    public async Task Multiple_independent_reminders_can_be_saved_for_different_sessions()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var sessions = new[] { "T4", "T6", "CN" }
            .Select(name => new MatchSession
            {
                Id = $"session-{name}",
                AdminUserId = "admin",
                Name = name,
                BotEnabled = true
            })
            .ToList();
        fixture.Db.MatchSessions.AddRange(sessions);
        var dueAt = DateTimeOffset.UtcNow.AddHours(1);
        foreach (var session in sessions)
        {
            fixture.Db.ZaloReminderSchedules.Add(new ZaloReminderSchedule
            {
                SessionId = session.Id,
                CreatedBySenderId = "operator",
                CreatedBySenderName = "Operator",
                Message = "Nhớ lên sân và đem theo nước",
                Audience = ZaloReminderAudience.All,
                NextRunAt = dueAt,
                Enabled = true
            });
        }
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        Assert.Equal(3, await fixture.Db.ZaloReminderSchedules.CountAsync(schedule => schedule.Enabled));
        Assert.Equal(3, await fixture.Db.ZaloReminderSchedules.Select(schedule => schedule.SessionId).Distinct().CountAsync());
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
    public async Task Repeating_existing_share_with_explicit_mention_backfills_zalo_avatar()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = await SeedFinishedDraftAsync(fixture.Db);
        var service = new SessionDraftService(fixture.Db);

        var legacyShare = await service.SharePostDraftSlotAsync(
            "admin", session.Id, "Nick Tran", "Vivian");
        Assert.True(legacyShare.IsSuccess, legacyShare.Error);

        var enrichedShare = await service.SharePostDraftSlotAsync(
            "admin",
            session.Id,
            "Nick Tran",
            new ShareSlotParticipantInput("Vivian", "zalo-vivian", "https://avatars.example/vivian.jpg"));
        Assert.True(enrichedShare.IsSuccess, enrichedShare.Error);

        fixture.Db.ChangeTracker.Clear();
        var vivian = await fixture.Db.SessionPlayers
            .AsNoTracking()
            .Include(player => player.PlayerProfile)
            .SingleAsync(player => player.SessionId == session.Id && player.DisplayName == "Vivian");
        Assert.Equal("https://avatars.example/vivian.jpg", vivian.AvatarUrl);
        Assert.Equal("zalo-vivian", vivian.PlayerProfile!.ZaloUserId);
        Assert.Equal("https://avatars.example/vivian.jpg", vivian.PlayerProfile.AvatarUrl);
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

    private static SessionPlayer PlayerForSession(string id, string name, double score, string sessionId) => new()
    {
        Id = id,
        SessionId = sessionId,
        DisplayName = name,
        Score = score,
        Gender = PlayerGender.Male,
        Role = PlayerRole.Attack,
        Level = PlayerLevel.Average,
        IsPresent = true
    };

    private static DraftSlot SlotForSession(
        string id,
        SessionPlayer player,
        string teamId,
        double score,
        bool isCaptain,
        string sessionId)
    {
        var slot = new DraftSlot
        {
            Id = id,
            SessionId = sessionId,
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
