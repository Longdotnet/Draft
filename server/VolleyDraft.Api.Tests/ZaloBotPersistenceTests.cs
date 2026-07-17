using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
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
            "admin", session.Id, "Bạn của Nick Tran", PlayerGender.Male, null, null, "guest-zalo");
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
            "admin", session.Id, "An", PlayerGender.Male, null, null, "zalo-an")).IsSuccess);
        Assert.True((await service.UpdatePlayerProfileFromBotAsync(
            "admin", session.Id, "Bình", PlayerGender.Female, null, null, "zalo-binh")).IsSuccess);
        var drafted = await service.AutoRunDraftAsync("admin", session.Id);
        Assert.True(drafted.IsSuccess, drafted.Error);
        Assert.Equal(SessionStatus.Finished, drafted.Value!.SessionStatus);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(18, await fixture.Db.DraftSlots.CountAsync(slot => slot.SessionId == session.Id));
        Assert.Equal(20, await fixture.Db.SessionPlayers.CountAsync(player => player.SessionId == session.Id && player.IsPresent));
    }

    [Fact]
    public async Task Share_slot_preview_is_read_only_and_calculates_effective_slot_and_score()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = PreferenceSession("share-preview", 6,
            ("a", "Thanh Long", 3d),
            ("b", "To An", 1d));
        session.StartTime = DateTimeOffset.UtcNow.AddDays(1);
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var preview = await service.PreviewShareSlotAsync(
            "admin",
            session.Id,
            "Thanh Long",
            [new ShareSlotParticipantInput("To An")]);

        Assert.True(preview.IsSuccess, preview.Error);
        Assert.Equal("Thanh Long / To An", preview.Value!.ProposedSlotDisplayName);
        Assert.Equal(1, preview.Value.EffectiveSlotCount);
        Assert.Equal(2, preview.Value.ProposedSlotAverageScore);
        Assert.True(preview.Value.AnchorIsPrimary);
        Assert.Empty(await fixture.Db.DraftSlots.Where(slot => slot.SessionId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task Post_draft_share_preview_flags_moving_an_existing_player_without_mutation()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = await SeedFinishedDraftAsync(fixture.Db);
        session.StartTime = DateTimeOffset.UtcNow.AddHours(2);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var preview = await service.PreviewShareSlotAsync(
            "admin",
            session.Id,
            "Nick Tran",
            [new ShareSlotParticipantInput("Thanh Tuyền")]);

        Assert.True(preview.IsSuccess, preview.Error);
        Assert.True(preview.Value!.MovesExistingDraftSlot);
        Assert.Equal("Team B", preview.Value.TeamName);
        Assert.Equal(2, preview.Value.ProposedSlotAverageScore);
        Assert.NotNull(preview.Value.ProjectedTeamScore);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("team-a", await fixture.Db.DraftSlots
            .Where(slot => slot.Id == "thanh-tuyen-slot")
            .Select(slot => slot.AssignedTeamId)
            .SingleAsync());
    }

    [Fact]
    public async Task Secondary_shared_player_is_not_treated_as_slot_owner_in_preview()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = await SeedFinishedDraftAsync(fixture.Db);
        session.StartTime = DateTimeOffset.UtcNow.AddHours(2);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);
        Assert.True((await service.SharePostDraftSlotAsync(
            "admin", session.Id, "Nick Tran", new ShareSlotParticipantInput("Vivian"))).IsSuccess);

        var preview = await service.PreviewShareSlotAsync(
            "admin",
            session.Id,
            "Vivian",
            [new ShareSlotParticipantInput("Bạn share cùng Vivian")]);

        Assert.True(preview.IsSuccess, preview.Error);
        Assert.False(preview.Value!.AnchorIsPrimary);
        Assert.Contains(preview.Value.Warnings, warning => warning.Contains("không phải người giữ slot chính"));
    }

    [Fact]
    public async Task Share_slot_preview_rejects_more_than_three_people_and_past_sessions()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = PreferenceSession("share-limit", 6,
            ("a", "A", 2d), ("b", "B", 2d), ("c", "C", 2d), ("d", "D", 2d));
        session.StartTime = DateTimeOffset.UtcNow.AddHours(2);
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);
        Assert.True((await service.SharePreDraftSlotAsync(
            "admin", session.Id, "A", [new ShareSlotParticipantInput("B"), new ShareSlotParticipantInput("C")])).IsSuccess);

        var overLimit = await service.PreviewShareSlotAsync(
            "admin", session.Id, "A", [new ShareSlotParticipantInput("D")]);
        Assert.False(overLimit.IsSuccess);
        Assert.Contains("tối đa 2", overLimit.Error);

        session.StartTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();
        var past = await service.PreviewShareSlotAsync(
            "admin", session.Id, "A", [new ShareSlotParticipantInput("B")]);
        Assert.False(past.IsSuccess);
        Assert.Contains("đã bắt đầu", past.Error);
    }

    [Fact]
    public async Task Share_slot_preview_only_carries_new_partner_when_one_is_already_shared()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = PreferenceSession("share-incremental", 6,
            ("a", "A", 2d), ("b", "B", 2d), ("c", "C", 2d));
        session.StartTime = DateTimeOffset.UtcNow.AddHours(2);
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);
        Assert.True((await service.SharePreDraftSlotAsync(
            "admin", session.Id, "A", [new ShareSlotParticipantInput("B")])).IsSuccess);

        var preview = await service.PreviewShareSlotAsync(
            "admin",
            session.Id,
            "A",
            [new ShareSlotParticipantInput("B"), new ShareSlotParticipantInput("C")]);

        Assert.True(preview.IsSuccess, preview.Error);
        Assert.Single(preview.Value!.PartnerInputs);
        Assert.Equal("C", preview.Value.PartnerInputs[0].DisplayName);
        var applied = await service.SharePreDraftSlotAsync(
            "admin", session.Id, preview.Value.AnchorPlayerName, preview.Value.PartnerInputs);
        Assert.True(applied.IsSuccess, applied.Error);
        Assert.Equal("A / B / C", applied.Value!.SlotDisplayName);
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
            "admin", session.Id, "Vivian", PlayerGender.Female, PlayerRole.Defense, PlayerLevel.New, "zalo-vivian")).IsSuccess);

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

    [Fact]
    public async Task Captain_can_transfer_slot_and_receiver_becomes_new_captain()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = await SeedFinishedDraftAsync(fixture.Db);
        var service = new SessionDraftService(fixture.Db);

        var preview = await service.PreviewPostDraftSlotTransferAsync(
            "admin", session.Id, "Captain A", new ShareSlotParticipantInput("Bạn Mới"));
        Assert.True(preview.IsSuccess, preview.Error);
        Assert.True(preview.Value!.CaptainTransferred);
        Assert.Equal("Team A", preview.Value.TeamName);

        var result = await service.TransferPostDraftSlotAsync(
            "admin", session.Id, "Captain A", new ShareSlotParticipantInput("Bạn Mới"));

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value!.CaptainTransferred);
        fixture.Db.ChangeTracker.Clear();
        var replacement = await fixture.Db.SessionPlayers.SingleAsync(player => player.DisplayName == "Bạn Mới");
        Assert.True(replacement.IsCaptainEligible);
        Assert.Equal(replacement.Id, await fixture.Db.Teams.Where(team => team.Id == "team-a")
            .Select(team => team.CaptainSessionPlayerId).SingleAsync());
        Assert.Equal(replacement.Id, await fixture.Db.DraftSlotPlayers.Where(link => link.DraftSlotId == "captain-slot-a")
            .Select(link => link.SessionPlayerId).SingleAsync());
        Assert.False(await fixture.Db.SessionPlayers.Where(player => player.Id == "captain-a")
            .Select(player => player.IsPresent).SingleAsync());
    }

    [Fact]
    public async Task Manual_board_edit_swaps_whole_slots_and_records_one_undoable_action()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = await SeedFinishedDraftAsync(fixture.Db);
        var history = new ZaloBotActionHistoryService(
            fixture.Db,
            NullLogger<ZaloBotActionHistoryService>.Instance);
        var draft = new SessionDraftService(fixture.Db);
        var board = new DraftBoardService(fixture.Db, draft, history);
        var state = await draft.GetDraftStateAsync("admin", session.Id);
        Assert.True(state.IsSuccess, state.Error);
        var assignments = state.Value!.TeamPreview.SelectMany(team => team.Slots.Select(slot =>
            new DraftBoardAssignmentRequest(
                slot.Id,
                team.TeamId,
                slot.Id == "thanh-tuyen-slot" ? "team-b" :
                slot.Id == "nick-tran-slot" ? "team-a" : team.TeamId)))
            .ToList();

        var result = await board.UpdateAsync(
            "admin",
            session.Id,
            new UpdateDraftBoardRequest(state.Value.StateToken, assignments));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("team-b", await fixture.Db.DraftSlots.Where(slot => slot.Id == "thanh-tuyen-slot")
            .Select(slot => slot.AssignedTeamId).SingleAsync());
        Assert.Equal("team-a", await fixture.Db.DraftSlots.Where(slot => slot.Id == "nick-tran-slot")
            .Select(slot => slot.AssignedTeamId).SingleAsync());
        var actions = await fixture.Db.ZaloBotActionHistory.Where(action => action.ActionType == "ManualDraftBoardEdit").ToListAsync();
        Assert.Single(actions);
        Assert.True(actions[0].IsUndoable);
    }

    [Fact]
    public async Task Draft_snapshot_restores_lineup_without_rolling_back_reminders()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = await SeedFinishedDraftAsync(fixture.Db);
        fixture.Db.ZaloReminderSchedules.Add(new ZaloReminderSchedule
        {
            SessionId = session.Id,
            CreatedBySenderId = "admin",
            CreatedBySenderName = "Admin",
            Message = "Nhắc đầu tiên",
            NextRunAt = DateTimeOffset.UtcNow.AddHours(1),
            Enabled = true
        });
        await fixture.Db.SaveChangesAsync();
        var history = new ZaloBotActionHistoryService(
            fixture.Db,
            NullLogger<ZaloBotActionHistoryService>.Instance);
        var snapshot = await history.CreateDraftSnapshotAsync(
            "admin", session.Id, "Đội hình ban đầu", "Admin website");
        Assert.True(snapshot.IsSuccess, snapshot.Error);

        await fixture.Db.DraftSlots.Where(slot => slot.Id == "thanh-tuyen-slot")
            .ExecuteUpdateAsync(update => update.SetProperty(slot => slot.AssignedTeamId, "team-b"));
        await fixture.Db.DraftSlots.Where(slot => slot.Id == "nick-tran-slot")
            .ExecuteUpdateAsync(update => update.SetProperty(slot => slot.AssignedTeamId, "team-a"));
        fixture.Db.ZaloReminderSchedules.Add(new ZaloReminderSchedule
        {
            SessionId = session.Id,
            CreatedBySenderId = "admin",
            CreatedBySenderName = "Admin",
            Message = "Nhắc tạo sau snapshot",
            NextRunAt = DateTimeOffset.UtcNow.AddHours(2),
            Enabled = true
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var changedState = await new SessionDraftService(fixture.Db).GetDraftStateAsync("admin", session.Id);

        var restored = await history.RestoreDraftSnapshotAsync(
            "admin",
            session.Id,
            snapshot.Value!.Id,
            changedState.Value!.StateToken,
            "Admin website");

        Assert.True(restored.IsSuccess, restored.Error);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("team-a", await fixture.Db.DraftSlots.Where(slot => slot.Id == "thanh-tuyen-slot")
            .Select(slot => slot.AssignedTeamId).SingleAsync());
        Assert.Equal("team-b", await fixture.Db.DraftSlots.Where(slot => slot.Id == "nick-tran-slot")
            .Select(slot => slot.AssignedTeamId).SingleAsync());
        Assert.Equal(2, await fixture.Db.ZaloReminderSchedules.CountAsync(schedule => schedule.SessionId == session.Id));
        Assert.Single(await fixture.Db.ZaloBotActionHistory.Where(action => action.ActionType == "DraftSnapshotRestore").ToListAsync());
    }

    [Fact]
    public async Task Profile_update_uses_mentioned_uid_even_when_an_external_guest_has_a_similar_name()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession { Id = "profile-uid-session", AdminUserId = "admin", Name = "T6" };
        var memberProfile = new PlayerProfile
        {
            Id = "huyen-profile",
            ZaloUserId = "huyen-uid",
            DisplayName = "Ngọc Huyền"
        };
        session.Players.Add(new SessionPlayer
        {
            Id = "huyen-player",
            SessionId = session.Id,
            PlayerProfileId = memberProfile.Id,
            PlayerProfile = memberProfile,
            DisplayName = "Ngọc Huyền",
            Gender = PlayerGender.Unknown,
            Role = PlayerRole.New,
            Level = PlayerLevel.New,
            IsPresent = true
        });
        session.Players.Add(new SessionPlayer
        {
            Id = "huyen-guest",
            SessionId = session.Id,
            DisplayName = "Bạn của Ngọc Huyền",
            Gender = PlayerGender.Unknown,
            Role = PlayerRole.New,
            Level = PlayerLevel.New,
            IsPresent = true
        });
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var result = await service.UpdatePlayerProfileFromBotAsync(
            "admin", session.Id, "Ngọc Huyền", PlayerGender.Female, null, null, "huyen-uid");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("huyen-player", result.Value!.Id);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(PlayerGender.Female, await fixture.Db.SessionPlayers
            .Where(player => player.Id == "huyen-player").Select(player => player.Gender).SingleAsync());
        Assert.Equal(PlayerGender.Unknown, await fixture.Db.SessionPlayers
            .Where(player => player.Id == "huyen-guest").Select(player => player.Gender).SingleAsync());
    }

    [Fact]
    public async Task Profile_update_without_uid_asks_instead_of_guessing_between_member_and_guest()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession { Id = "profile-ambiguous-session", AdminUserId = "admin", Name = "T6" };
        var profile = new PlayerProfile
        {
            Id = "member-profile",
            ZaloUserId = "member-uid",
            DisplayName = "Ngọc Huyền"
        };
        session.Players.Add(new SessionPlayer
        {
            Id = "member-player",
            SessionId = session.Id,
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            DisplayName = "Ngọc Huyền",
            Gender = PlayerGender.Unknown,
            IsPresent = true
        });
        session.Players.Add(new SessionPlayer
        {
            Id = "external-player",
            SessionId = session.Id,
            DisplayName = "Bạn của Ngọc Huyền",
            Gender = PlayerGender.Unknown,
            IsPresent = true
        });
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var result = await service.UpdatePlayerProfileFromBotAsync(
            "admin", session.Id, "Ngọc Huyền", PlayerGender.Male, null, null);

        Assert.False(result.IsSuccess);
        Assert.Contains("Bạn muốn cập nhật ai", result.Error);
        fixture.Db.ChangeTracker.Clear();
        Assert.All(await fixture.Db.SessionPlayers.Where(player => player.SessionId == session.Id).ToListAsync(),
            player => Assert.Equal(PlayerGender.Unknown, player.Gender));
    }

    [Fact]
    public async Task Profile_update_can_fuzzy_match_one_external_guest_only()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession { Id = "profile-external-session", AdminUserId = "admin", Name = "T6" };
        session.Players.Add(new SessionPlayer
        {
            Id = "external-huyen",
            SessionId = session.Id,
            DisplayName = "Bạn của Ngọc Huyền",
            Gender = PlayerGender.Unknown,
            IsPresent = true
        });
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var result = await service.UpdatePlayerProfileFromBotAsync(
            "admin", session.Id, "bạn Ngọc Huyền", PlayerGender.Male, null, null);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("external-huyen", result.Value!.Id);
    }

    [Fact]
    public async Task Same_team_preference_uses_uids_and_does_not_create_a_shared_slot()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = new MatchSession { Id = "team-preference-session", AdminUserId = "admin", Name = "T6" };
        foreach (var player in new[]
                 {
                     (Id: "to-an", Name: "To An", ProfileId: "to-an-profile", Uid: "to-an-uid"),
                     (Id: "anh-duy", Name: "Anh Duy", ProfileId: "anh-duy-profile", Uid: "anh-duy-uid")
                 })
        {
            var profile = new PlayerProfile
            {
                Id = player.ProfileId,
                ZaloUserId = player.Uid,
                DisplayName = player.Name
            };
            session.Players.Add(new SessionPlayer
            {
                Id = player.Id,
                SessionId = session.Id,
                PlayerProfileId = profile.Id,
                PlayerProfile = profile,
                DisplayName = player.Name,
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

        var result = await service.CreateTeamPreferenceGroupFromBotAsync(
            "admin",
            session.Id,
            [
                new ShareSlotParticipantInput("To An", "to-an-uid"),
                new ShareSlotParticipantInput("Anh Duy", "anh-duy-uid")
            ]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(["To An", "Anh Duy"], result.Value!.PlayerNames);
        Assert.Single(await fixture.Db.TeamPreferenceGroups.Where(group => group.SessionId == session.Id).ToListAsync());
        Assert.Empty(await fixture.Db.DraftSlots.Where(slot => slot.SessionId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task Same_team_preference_merges_overlapping_groups_transitively()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = PreferenceSession("preference-merge", 6,
            ("a", "A", 2d), ("b", "B", 2d), ("c", "C", 2d), ("d", "D", 2d));
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        Assert.True((await service.CreateTeamPreferenceGroupAsync("admin", session.Id, new(["a", "b"]))).IsSuccess);
        Assert.True((await service.CreateTeamPreferenceGroupAsync("admin", session.Id, new(["c", "d"]))).IsSuccess);
        var history = new ZaloBotActionHistoryService(
            fixture.Db,
            NullLogger<ZaloBotActionHistoryService>.Instance);
        var beforeMerge = await history.CaptureAsync(session.Id);
        var merged = await service.CreateTeamPreferenceGroupAsync("admin", session.Id, new(["b", "c"]));

        Assert.True(merged.IsSuccess, merged.Error);
        Assert.Equal(["A", "B", "C", "D"], merged.Value!.PlayerNames.OrderBy(name => name).ToList());
        Assert.Single(await fixture.Db.TeamPreferenceGroups.Where(group => group.SessionId == session.Id).ToListAsync());
        Assert.Equal(4, await fixture.Db.TeamPreferenceGroupPlayers.CountAsync());

        var action = await history.RecordAsync(
            session.Id, "operator", "Operator", "MergeTeamPreference", "Gộp hai nhóm", beforeMerge);
        Assert.NotNull(action);
        var undone = await history.UndoAsync("admin", session.Id, action!.Id, "operator");
        Assert.True(undone.IsSuccess, undone.Error);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(2, await fixture.Db.TeamPreferenceGroups.CountAsync(group => group.SessionId == session.Id));
        var restoredLinks = await fixture.Db.TeamPreferenceGroupPlayers
            .Where(link => link.TeamPreferenceGroup.SessionId == session.Id)
            .Select(link => new { link.TeamPreferenceGroupId, link.SessionPlayerId })
            .ToListAsync();
        var restoredGroups = restoredLinks
            .GroupBy(link => link.TeamPreferenceGroupId)
            .Select(group => group.Select(link => link.SessionPlayerId).OrderBy(id => id).ToList())
            .ToList();
        Assert.Contains(restoredGroups, players => players.SequenceEqual(["a", "b"]));
        Assert.Contains(restoredGroups, players => players.SequenceEqual(["c", "d"]));
    }

    [Fact]
    public async Task High_score_three_player_cluster_requires_preview_confirmation()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = PreferenceSession("preference-high", 6,
            ("a", "A", 3.5d), ("b", "B", 3.5d), ("c", "C", 3.5d),
            ("d", "D", 1d), ("e", "E", 1d), ("f", "F", 1d),
            ("g", "G", 1d), ("h", "H", 1d), ("i", "I", 1d),
            ("j", "J", 1d), ("k", "K", 1d), ("l", "L", 1d),
            ("m", "M", 1d), ("n", "N", 1d), ("o", "O", 1d),
            ("p", "P", 1d), ("q", "Q", 1d), ("r", "R", 1d));
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var preview = await service.PreviewTeamPreferenceGroupFromBotAsync("admin", session.Id,
        [
            new ShareSlotParticipantInput("A"),
            new ShareSlotParticipantInput("B"),
            new ShareSlotParticipantInput("C")
        ]);

        Assert.True(preview.IsSuccess, preview.Error);
        Assert.True(preview.Value!.IsFeasible);
        Assert.True(preview.Value.RequiresConfirmation);
        Assert.Equal(3, preview.Value.EffectiveSlotCount);
        Assert.Equal(3.5, preview.Value.GroupAverageScore);
        Assert.Contains(preview.Value.Warnings, warning => warning.Contains("slot mạnh"));
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.Where(group => group.SessionId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task Shared_players_count_as_one_effective_preference_slot()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = PreferenceSession("preference-shared", 2,
            ("a", "A", 3d), ("a2", "A2", 1d), ("b", "B", 2d),
            ("c", "C", 2d), ("d", "D", 2d), ("e", "E", 2d), ("f", "F", 2d));
        var shared = new DraftSlot
        {
            Id = "shared-a",
            SessionId = session.Id,
            Type = DraftSlotType.Shared,
            DisplayName = "A / A2",
            AverageScore = 2
        };
        shared.Players.AddRange([
            new DraftSlotPlayer { DraftSlotId = shared.Id, SessionPlayerId = "a", RotationOrder = 1 },
            new DraftSlotPlayer { DraftSlotId = shared.Id, SessionPlayerId = "a2", RotationOrder = 2 }
        ]);
        session.DraftSlots.Add(shared);
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var preview = await service.PreviewTeamPreferenceGroupFromBotAsync("admin", session.Id,
            [new ShareSlotParticipantInput("A"), new ShareSlotParticipantInput("B")]);

        Assert.True(preview.IsSuccess, preview.Error);
        Assert.True(preview.Value!.IsFeasible);
        Assert.Equal(2, preview.Value.EffectiveSlotCount);
        Assert.Equal(["A", "A2", "B"], preview.Value.PlayerNames.OrderBy(name => name).ToList());
    }

    [Fact]
    public async Task Preference_over_team_capacity_is_rejected_without_mutation()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = PreferenceSession("preference-capacity", 2,
            ("a", "A", 2d), ("b", "B", 2d), ("c", "C", 2d),
            ("d", "D", 2d), ("e", "E", 2d), ("f", "F", 2d));
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);

        var preview = await service.PreviewTeamPreferenceGroupFromBotAsync("admin", session.Id,
        [
            new ShareSlotParticipantInput("A"),
            new ShareSlotParticipantInput("B"),
            new ShareSlotParticipantInput("C")
        ]);

        Assert.True(preview.IsSuccess, preview.Error);
        Assert.False(preview.Value!.IsFeasible);
        Assert.Contains("3 slot", preview.Value.BlockingReason);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.Where(group => group.SessionId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task Withdrawing_player_shrinks_or_removes_preference_group()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var session = PreferenceSession("preference-withdraw", 6,
            ("a", "A", 2d), ("b", "B", 2d), ("c", "C", 2d));
        fixture.Db.MatchSessions.Add(session);
        await fixture.Db.SaveChangesAsync();
        var service = new SessionDraftService(fixture.Db);
        Assert.True((await service.CreateTeamPreferenceGroupAsync("admin", session.Id, new(["a", "b", "c"]))).IsSuccess);

        var player = session.Players.Single(item => item.Id == "c");
        var updated = await service.UpdatePlayerAsync("admin", session.Id, player.Id,
            new UpdatePlayerRequest(player.DisplayName, player.Role, player.Level, player.Gender, false, player.IsCaptainEligible));

        Assert.True(updated.IsSuccess, updated.Error);
        var remaining = await fixture.Db.TeamPreferenceGroupPlayers
            .Where(link => link.TeamPreferenceGroup.SessionId == session.Id)
            .Select(link => link.SessionPlayerId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(["a", "b"], remaining);
    }

    private static MatchSession PreferenceSession(
        string id,
        int teamSize,
        params (string Id, string Name, double Score)[] players)
    {
        var session = new MatchSession
        {
            Id = id,
            AdminUserId = "admin",
            Name = id,
            TeamCount = 3,
            TeamSize = teamSize
        };
        foreach (var player in players)
        {
            session.Players.Add(new SessionPlayer
            {
                Id = player.Id,
                SessionId = id,
                DisplayName = player.Name,
                Score = player.Score,
                Gender = PlayerGender.Male,
                Role = player.Score >= 3.5 ? PlayerRole.FullStack : PlayerRole.Attack,
                Level = player.Score >= 3 ? PlayerLevel.Good : player.Score >= 2 ? PlayerLevel.Average : PlayerLevel.New,
                IsPresent = true,
                IsCaptainEligible = true
            });
        }
        return session;
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
