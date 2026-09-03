using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

/// <summary>
/// Cross-feature conversation evals for the recruitment/guest agent. These scenarios
/// intentionally compose production policies and domain services rather than mirroring
/// their implementation in test-only code. Run with:
/// dotnet test --filter Category=GuestAgentEval
/// </summary>
public sealed class ZaloGuestAgentConversationEvalTests
{
    private static readonly ZaloSemanticActionSettings Settings = new(
        Enabled: true,
        MinimumConfidence: .85,
        MaxContextMessages: 12,
        MaxUserCallsPerMinute: 4,
        MaxGroupCallsPerMinute: 20);

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "authority-direct-add")]
    public void E01_DirectAddWithoutRecruitmentReply_FailsClosed()
    {
        var plan = AddPlan(1);
        var validation = ZaloSemanticGuestPlanValidator.Validate(
            plan,
            Snapshot(ZaloSemanticGuestAnchorKind.None, addWindowOpen: true),
            Settings);

        Assert.False(validation.Accepted);
        Assert.Equal("semantic_guest_add_requires_recruitment_reply", validation.Reason);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "authority-window")]
    public void E02_GroundedRecruitmentReply_StillCannotAddBeforeGuestWindow()
    {
        var validation = ZaloSemanticGuestPlanValidator.Validate(
            AddPlan(2),
            Snapshot(ZaloSemanticGuestAnchorKind.RecruitmentBroadcast, addWindowOpen: false),
            Settings);

        Assert.False(validation.Accepted);
        Assert.Equal("semantic_guest_add_window_closed", validation.Reason);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "conditional-not-immediate")]
    public void E03_ConditionalSentence_CannotCollapseIntoImmediateLegacyAdd()
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse("nếu 19h vẫn thiếu thì +2");
        var conditional = ZaloConditionalGuestIntentPolicy.TryParse("nếu 19h vẫn thiếu thì +2");

        Assert.Null(command);
        Assert.NotNull(conditional);
        Assert.Equal(2, conditional!.Quantity);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "conditional-grounding")]
    public void E04_ConditionalPromise_MayBeScheduledEarly_ButOnlyFromRecruitmentAnchor()
    {
        var now = new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero); // 15:00 VN
        var start = new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero); // 20:00 VN
        var plan = ConditionalPlan(hour: 19, quantity: 2);

        var grounded = ZaloSemanticGuestPlanValidator.Validate(
            plan,
            Snapshot(ZaloSemanticGuestAnchorKind.RecruitmentBroadcast, false, now, start),
            Settings);
        var ordinary = ZaloSemanticGuestPlanValidator.Validate(
            plan,
            Snapshot(ZaloSemanticGuestAnchorKind.ActiveGuestConversation, true, now, start),
            Settings);

        Assert.True(grounded.Accepted);
        Assert.Equal(ZaloSemanticGuestActionKind.ScheduleConditionalGuests, grounded.Action);
        Assert.False(ordinary.Accepted);
        Assert.Equal("semantic_guest_conditional_requires_recruitment_reply", ordinary.Reason);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "fabricated-target")]
    public void E05_FabricatedReservationId_CannotEscapeGroundingSnapshot()
    {
        var snapshot = Snapshot(
            ZaloSemanticGuestAnchorKind.ActiveGuestConversation,
            addWindowOpen: true,
            guests:
            [
                new ZaloSemanticGuestGroundingGuest(
                    "real-reservation", 1, "Minh", PlayerGender.Male, null, null,
                    ZaloGuestReservationStatus.Active.ToString())
            ]);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.CancelGuests,
            .99,
            1,
            .99,
            [new ZaloSemanticGuestPlanItem(
                "#1", "invented-reservation", 1, null, 0, null, 0, null, 0, null, 0, .99)],
            false,
            string.Empty,
            "cancel fabricated target");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.False(validation.Accepted);
        Assert.Equal("semantic_guest_cancel_target_ambiguous", validation.Reason);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "tentative-confirm")]
    public async Task E06_TentativeThenConfirm_OnlyConfirmationOccupiesRosterSlot()
    {
        await using var db = await CreateDbAsync(initialPlayers: 17);
        var session = await db.MatchSessions.SingleAsync();
        var domain = new ZaloGuestDomainActionService(db);

        var tentative = await domain.AddTentativeAsync(
            session,
            "sponsor-1",
            "Nick",
            "turn-1",
            "recruitment-1",
            [new ZaloRecruitmentGuestSpec("Minh", PlayerGender.Male)],
            1,
            CancellationToken.None);
        Assert.Equal(17, tentative.EffectiveSlots);
        Assert.Equal(17, await db.SessionPlayers.CountAsync(item => item.IsPresent));

        var confirmed = await domain.ConfirmAsync(
            session,
            "sponsor-1",
            [tentative.Changed.Single().Id],
            CancellationToken.None);

        Assert.Equal(18, confirmed.EffectiveSlots);
        Assert.Equal(18, await db.SessionPlayers.CountAsync(item => item.IsPresent));
        Assert.Equal(
            ZaloGuestReservationStatus.Active,
            (await db.ZaloGuestReservations.SingleAsync()).Status);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "atomic-replace")]
    public async Task E07_ReplaceConversation_KeepsRosterStable_AndRetryDoesNotDuplicate()
    {
        await using var db = await CreateDbAsync(initialPlayers: 17);
        var session = await db.MatchSessions.SingleAsync();
        var reservation = await new ZaloGuestReservationService(db).AddAsync(
            session,
            "sponsor-1",
            "Nick",
            "add-minh",
            "recruitment-1",
            new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.Add,
                Guests: [new ZaloRecruitmentGuestSpec("Minh", PlayerGender.Male)]),
            CancellationToken.None);
        var old = reservation.Added.Single();
        var domain = new ZaloGuestDomainActionService(db);

        var first = await domain.ReplaceAsync(
            session, "sponsor-1", "Nick", old.Id, "replace-turn", "recruitment-1",
            new ZaloRecruitmentGuestSpec("Huy", PlayerGender.Male), CancellationToken.None);
        var retry = await domain.ReplaceAsync(
            session, "sponsor-1", "Nick", old.Id, "replace-turn", "recruitment-1",
            new ZaloRecruitmentGuestSpec("Huy", PlayerGender.Male), CancellationToken.None);

        Assert.Equal(18, first.EffectiveSlots);
        Assert.True(retry.Idempotent);
        Assert.Equal(18, await db.SessionPlayers.CountAsync(item => item.IsPresent));
        Assert.Equal(1, await db.ZaloGuestReservations.CountAsync(item => item.SourceMessageId == "replace-turn"));
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "roster-bounce")]
    public void E08_RosterDropThatRecoversInsideDebounce_DoesNotBecomeRecruitmentIncident()
    {
        var start = DateTimeOffset.UtcNow;
        var baseline = RosterBaseline(18, start);
        var pending = ZaloRosterChangeCoordinatorPolicy.Observe(
            baseline, "session-1", 17, 17, "fp17", start.AddMinutes(.5), TimeSpan.FromMinutes(2));
        var recovered = ZaloRosterChangeCoordinatorPolicy.Observe(
            pending.State, "session-1", 18, 18, "fp18b", start.AddMinutes(1), TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.DropPending, pending.Kind);
        Assert.Equal(ZaloRosterObservationTransitionKind.DropBounced, recovered.Kind);
        Assert.False(recovered.State.HasUnnotifiedDrop);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "roster-multidrop")]
    public void E09_MultipleDropsAreCoalesced_FromOriginalStableRoster()
    {
        var start = DateTimeOffset.UtcNow;
        var first = ZaloRosterChangeCoordinatorPolicy.Observe(
            RosterBaseline(15, start), "session-1", 14, 14, "fp14", start.AddMinutes(.3), TimeSpan.FromMinutes(2));
        var second = ZaloRosterChangeCoordinatorPolicy.Observe(
            first.State, "session-1", 13, 13, "fp13", start.AddMinutes(1), TimeSpan.FromMinutes(2));
        var confirmed = ZaloRosterChangeCoordinatorPolicy.Observe(
            second.State, "session-1", 13, 13, "fp13", start.AddMinutes(3), TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.DropConfirmed, confirmed.Kind);
        Assert.Equal(15, confirmed.DropFrom);
        Assert.Equal(13, confirmed.DropTo);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "same-count-replacement")]
    public void E10_SameSlotCountRosterReplacement_IsNotTreatedAsMissingSlot()
    {
        var start = DateTimeOffset.UtcNow;
        var transition = ZaloRosterChangeCoordinatorPolicy.Observe(
            RosterBaseline(15, start),
            "session-1",
            15,
            15,
            "new-player-fingerprint",
            start.AddMinutes(1),
            TimeSpan.FromMinutes(2));

        Assert.Equal(ZaloRosterObservationTransitionKind.Unchanged, transition.Kind);
        Assert.False(transition.State.HasUnnotifiedDrop);
        Assert.Equal("new-player-fingerprint", transition.State.StableFingerprint);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "conditional-time")]
    public void E11_AmbiguousSevenOClock_IsResolvedAgainstSession_NotWallClockGuessing()
    {
        var now = new DateTimeOffset(2026, 8, 23, 9, 30, 0, TimeSpan.Zero); // 16:30 VN
        var start = new DateTimeOffset(2026, 8, 23, 13, 30, 0, TimeSpan.Zero); // 20:30 VN
        var draft = new ZaloConditionalGuestIntentDraft(2, 1, 7, 0, false, []);

        var trigger = ZaloConditionalGuestIntentPolicy.ResolveRequestedTrigger(draft, now, start);

        Assert.NotNull(trigger);
        Assert.Equal(19, trigger!.Value.ToOffset(TimeSpan.FromHours(7)).Hour);
    }

    [Fact]
    [Trait("Category", "GuestAgentEval")]
    [Trait("Scenario", "planner-contract")]
    public void E12_PlannerJsonConditionalAction_RemainsAConditionThroughParsingAndValidation()
    {
        var plan = ZaloSemanticGuestPlanner.ParsePlan("""
            {
              "action":"ScheduleConditionalGuests",
              "confidence":0.99,
              "quantity":2,
              "quantityConfidence":0.99,
              "conditionalHour":19,
              "conditionalMinute":0,
              "conditionalEvening":false,
              "minimumMissingSlots":1,
              "guests":[],
              "needsClarification":false,
              "clarificationReason":"",
              "reason":"if still missing at 19:00"
            }
            """);
        var now = new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero);

        var validated = ZaloSemanticGuestPlanValidator.Validate(
            plan,
            Snapshot(ZaloSemanticGuestAnchorKind.RecruitmentBroadcast, false, now, start),
            Settings);

        Assert.Equal(ZaloSemanticGuestActionKind.ScheduleConditionalGuests, plan.Action);
        Assert.True(validated.Accepted);
        Assert.Equal(ZaloSemanticGuestActionKind.ScheduleConditionalGuests, validated.Action);
        Assert.Equal(2, validated.Quantity);
    }

    private static ZaloSemanticGuestPlan AddPlan(int quantity) => new(
        ZaloSemanticGuestActionKind.AddGuests,
        .99,
        quantity,
        .99,
        Enumerable.Range(0, quantity)
            .Select(_ => new ZaloSemanticGuestPlanItem(
                string.Empty, null, null, null, 0, null, 0, null, 0, null, 0, .99))
            .ToArray(),
        false,
        string.Empty,
        "add guest");

    private static ZaloSemanticGuestPlan ConditionalPlan(int hour, int quantity) => new(
        ZaloSemanticGuestActionKind.ScheduleConditionalGuests,
        .99,
        quantity,
        .99,
        Enumerable.Range(0, quantity)
            .Select(_ => new ZaloSemanticGuestPlanItem(
                string.Empty, null, null, null, 0, null, 0, null, 0, null, 0, .99))
            .ToArray(),
        false,
        string.Empty,
        "conditional guest",
        ConditionalHour: hour,
        ConditionalMinute: 0,
        ConditionalEvening: false,
        MinimumMissingSlots: 1);

    private static ZaloSemanticGuestGroundingSnapshot Snapshot(
        ZaloSemanticGuestAnchorKind anchor,
        bool addWindowOpen,
        DateTimeOffset? now = null,
        DateTimeOffset? start = null,
        IReadOnlyList<ZaloSemanticGuestGroundingGuest>? guests = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var sessionStart = start ?? current.AddHours(1);
        return new ZaloSemanticGuestGroundingSnapshot(
            "session-1",
            "T7",
            sessionStart,
            17,
            18,
            addWindowOpen,
            "sponsor-1",
            "Nick",
            anchor,
            anchor == ZaloSemanticGuestAnchorKind.RecruitmentBroadcast ? "recruitment-1" : null,
            guests ?? [],
            [],
            current,
            current.ToOffset(TimeSpan.FromHours(7)));
    }

    private static ZaloRecruitmentRosterObservation RosterBaseline(int count, DateTimeOffset now) => new(
        "session-1",
        count,
        count,
        $"fp{count}",
        null,
        null,
        null,
        now,
        null,
        null,
        null,
        null,
        now);

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
