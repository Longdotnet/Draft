using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloConditionalGuestIntentTests
{
    private static readonly ZaloSemanticActionSettings Settings = new(
        Enabled: true,
        MinimumConfidence: .85,
        MaxContextMessages: 12,
        MaxUserCallsPerMinute: 4,
        MaxGroupCallsPerMinute: 20);

    [Fact]
    public void ParsesNaturalConditionalGuestIntent()
    {
        var parsed = ZaloConditionalGuestIntentPolicy.TryParse("nếu 19h vẫn thiếu thì cho 2 bạn tui vô");

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Quantity);
        Assert.Equal(1, parsed.MinimumMissingSlots);
        Assert.Equal(19, parsed.Hour);
        Assert.Equal(0, parsed.Minute);
    }

    [Fact]
    public void LegacyParser_DoesNotTurnConditionalIntoImmediateAdd()
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse("nếu 19h vẫn thiếu thì +2");

        Assert.Null(command);
    }

    [Fact]
    public void AmbiguousSevenOClock_ResolvesToEveningWhenMorningIsAlreadyPastAndMatchIsEvening()
    {
        var now = new DateTimeOffset(2026, 8, 23, 9, 30, 0, TimeSpan.Zero); // 16:30 VN
        var start = new DateTimeOffset(2026, 8, 23, 13, 30, 0, TimeSpan.Zero); // 20:30 VN
        var draft = new ZaloConditionalGuestIntentDraft(2, 1, 7, 0, false, []);

        var trigger = ZaloConditionalGuestIntentPolicy.ResolveRequestedTrigger(draft, now, start);

        Assert.NotNull(trigger);
        Assert.Equal(19, trigger!.Value.ToOffset(TimeSpan.FromHours(7)).Hour);
    }

    [Fact]
    public void SevenOClock_ResolvesToMorningForMorningMatch()
    {
        var now = new DateTimeOffset(2026, 8, 22, 22, 0, 0, TimeSpan.Zero); // 05:00 VN
        var start = new DateTimeOffset(2026, 8, 23, 3, 0, 0, TimeSpan.Zero); // 10:00 VN
        var draft = new ZaloConditionalGuestIntentDraft(1, 1, 7, 0, false, []);

        var trigger = ZaloConditionalGuestIntentPolicy.ResolveRequestedTrigger(draft, now, start);

        Assert.NotNull(trigger);
        Assert.Equal(7, trigger!.Value.ToOffset(TimeSpan.FromHours(7)).Hour);
    }

    [Fact]
    public void SemanticValidation_AllowsSchedulingBeforeGuestWindow_ButRequiresRecruitmentAnchor()
    {
        var now = new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero);
        var plan = ConditionalPlan();
        var recruitment = Snapshot(now, start, ZaloSemanticGuestAnchorKind.RecruitmentBroadcast, addWindowOpen: false);
        var conversation = Snapshot(now, start, ZaloSemanticGuestAnchorKind.GuestConversation, addWindowOpen: true);

        var accepted = ZaloSemanticGuestPlanValidator.Validate(plan, recruitment, Settings);
        var rejected = ZaloSemanticGuestPlanValidator.Validate(plan, conversation, Settings);

        Assert.True(accepted.Accepted);
        Assert.Equal(ZaloSemanticGuestActionKind.ScheduleConditionalGuests, accepted.Action);
        Assert.Equal(19, accepted.ConditionalHour);
        Assert.Equal(2, accepted.Quantity);
        Assert.False(rejected.Accepted);
        Assert.Equal("semantic_guest_conditional_requires_recruitment_reply", rejected.Reason);
    }

    [Fact]
    public void SemanticValidation_RejectsTriggerOutsideUpcomingSession()
    {
        var now = new DateTimeOffset(2026, 8, 23, 12, 30, 0, TimeSpan.Zero); // 19:30 VN
        var start = new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero); // 20:00 VN
        var plan = ConditionalPlan() with { ConditionalHour = 19 };

        var result = ZaloSemanticGuestPlanValidator.Validate(
            plan,
            Snapshot(now, start, ZaloSemanticGuestAnchorKind.RecruitmentBroadcast, true),
            Settings);

        Assert.False(result.Accepted);
        Assert.Equal("semantic_guest_conditional_time_outside_session", result.Reason);
    }

    [Fact]
    public async Task DurableStore_IsIdempotentBySourceMessage_AndLoadsDueIntent()
    {
        await using var db = await CreateDbAsync();
        var store = new ZaloConditionalGuestIntentStore(db);
        var now = DateTimeOffset.UtcNow;

        var first = await store.CreateOrReuseAsync(
            "session-1", "group-1", "sponsor-1", "Nick", "message-1", "recruitment-1",
            now.AddMinutes(-1), now.AddMinutes(-1), 1, 2, "[]");
        var retry = await store.CreateOrReuseAsync(
            "session-1", "group-1", "sponsor-1", "Nick", "message-1", "recruitment-1",
            now.AddMinutes(10), now.AddMinutes(10), 2, 1, "[]");
        var due = await store.LoadDueAsync(now);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(2, retry.Quantity);
        Assert.Single(due);
        Assert.Equal(first.Id, due[0].Id);

        await store.SetStatusAsync(first.Id, ZaloConditionalGuestIntentStatus.Executed, null, now);
        Assert.Empty(await store.LoadDueAsync(now.AddMinutes(1)));
    }

    private static ZaloSemanticGuestPlan ConditionalPlan() => new(
        ZaloSemanticGuestActionKind.ScheduleConditionalGuests,
        .99,
        2,
        .99,
        [
            new ZaloSemanticGuestPlanItem("guest 1", null, null, null, 0, null, 0, null, 0, null, 0, .99),
            new ZaloSemanticGuestPlanItem("guest 2", null, null, null, 0, null, 0, null, 0, null, 0, .99)
        ],
        false,
        string.Empty,
        "conditional add",
        ConditionalHour: 19,
        ConditionalMinute: 0,
        ConditionalEvening: false,
        MinimumMissingSlots: 1);

    private static ZaloSemanticGuestGroundingSnapshot Snapshot(
        DateTimeOffset now,
        DateTimeOffset start,
        ZaloSemanticGuestAnchorKind anchor,
        bool addWindowOpen) => new(
        "session-1",
        "T7",
        start,
        16,
        18,
        addWindowOpen,
        "sponsor-1",
        "Nick",
        anchor,
        anchor == ZaloSemanticGuestAnchorKind.RecruitmentBroadcast ? "recruitment-1" : null,
        [],
        [],
        now,
        now.ToOffset(TimeSpan.FromHours(7)));

    private static async Task<VolleyDraftDbContext> CreateDbAsync()
    {
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new VolleyDraftDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
