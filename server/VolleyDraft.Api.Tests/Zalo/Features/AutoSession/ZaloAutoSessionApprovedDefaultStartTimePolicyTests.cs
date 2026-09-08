using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionApprovedDefaultStartTimePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void ApprovedDefaultTimeRefresh_AutoAppliesWithoutMaterialConfirmation()
    {
        var source = Draft(Item(17, 30));
        var poll = Poll("Vote sân UTE", "T6 11/9");
        var tracked = Tracked(18, 0);

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, tracked, source, source, Now);

        Assert.True(result.CanExecute);
        Assert.False(result.RequiresOrganizerConfirmation);
        Assert.Equal(At(18, 0), Assert.Single(result.Reconciliation.Draft.Items).StartTime);
        var change = Assert.Single(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ApprovedStartTimeChanged);
        Assert.False(change.RequiresConfirmation);
    }

    [Fact]
    public void ApprovedDefaultTimeRefresh_PreservesOrganizerCorrection()
    {
        var source = Draft(Item(17, 30));
        var organizer = source with
        {
            Items = [source.Items[0] with { StartTime = At(19, 0) }]
        };
        var poll = Poll("Vote sân UTE", "T6 11/9");

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(18, 0), source, organizer, Now);

        Assert.True(result.CanExecute);
        Assert.Equal(At(19, 0), Assert.Single(result.Reconciliation.Draft.Items).StartTime);
        Assert.Contains(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ApprovedStartTimeChanged && !change.RequiresConfirmation);
    }

    [Fact]
    public void ExplicitPollTimeChange_RemainsMaterialAndOverridesOrganizerCorrection()
    {
        var source = Draft(Item(17, 30, "T6 11/9 17:30"));
        var organizer = source with
        {
            Items = [source.Items[0] with { StartTime = At(19, 0) }]
        };
        var poll = Poll("Vote sân UTE", "T6 11/9 18:00");

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(17, 30), source, organizer, Now);

        Assert.False(result.CanExecute);
        Assert.True(result.RequiresOrganizerConfirmation);
        Assert.Equal(At(18, 0), Assert.Single(result.Reconciliation.Draft.Items).StartTime);
        Assert.Contains(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ExplicitStartTimeChanged && change.RequiresConfirmation);
        Assert.DoesNotContain(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ApprovedStartTimeChanged);
    }

    [Fact]
    public void ExplicitTitleTimeChange_RemainsMaterialForOptionsWithoutOwnTime()
    {
        var source = Draft(Item(17, 30));
        var poll = Poll("Vote sân UTE 18:00-22:00", "T6 11/9");

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(19, 0), source, source, Now);

        Assert.False(result.CanExecute);
        Assert.True(result.RequiresOrganizerConfirmation);
        Assert.Equal(At(18, 0), Assert.Single(result.Reconciliation.Draft.Items).StartTime);
        Assert.Contains(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ExplicitStartTimeChanged);
    }

    [Fact]
    public void RestartBaseline_AfterDefaultRefresh_DoesNotRequestConfirmationAgain()
    {
        var original = Draft(Item(17, 30));
        var poll = Poll("Vote sân UTE", "T6 11/9");
        var first = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(18, 0), original, original, Now);

        Assert.True(first.CanExecute);
        var second = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll,
            Tracked(18, 0),
            first.CurrentSourceDraft,
            first.Reconciliation.Draft,
            Now.AddMinutes(5));

        Assert.True(second.CanExecute);
        Assert.DoesNotContain(second.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ApprovedStartTimeChanged ||
            change.Kind == ZaloAutoSessionPollChangeKindV4.ExplicitStartTimeChanged);
    }

    private static ZaloTrackedGroupData Tracked(int hour, int minute) => new()
    {
        DefaultStartMinutes = hour * 60 + minute,
        DefaultTeamSize = 6,
        DefaultLocation = "UTE",
        AssumePmForHourUnder12 = true
    };

    private static ZaloAutoSessionConversationDraft Draft(ZaloAutoSessionConversationDraftItem item) =>
        new([item], "UTE", 6);

    private static ZaloAutoSessionConversationDraftItem Item(int hour, int minute, string content = "T6 11/9") =>
        new("t6", content, "T6", At(hour, minute), 8, true);

    private static BridgePoll Poll(string question, string optionContent) => new(
        "poll-1",
        question,
        "organizer-1",
        [new BridgePollOption("t6", optionContent, 8, [])],
        true,
        false,
        false,
        false,
        8,
        Now.AddHours(-1).ToUnixTimeMilliseconds(),
        Now.ToUnixTimeMilliseconds(),
        Now.AddDays(7).ToUnixTimeMilliseconds());

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 9, 11, hour, minute, 0, TimeSpan.FromHours(7));
}
