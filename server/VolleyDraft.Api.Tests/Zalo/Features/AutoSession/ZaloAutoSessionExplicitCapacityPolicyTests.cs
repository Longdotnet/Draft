using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionExplicitCapacityPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 17, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void ExplicitMaxSlots_OverridesApprovedTeamSizeAndRequiresConfirmation()
    {
        var source = Draft(teamSize: 7);
        var poll = Poll("Vote sân UTE tuần sau. Max 18 slots/sân. 17:45-22:00");

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(teamSize: 7), source, source, Now);

        Assert.False(result.CanExecute);
        Assert.True(result.RequiresOrganizerConfirmation);
        Assert.Equal(6, result.Reconciliation.Draft.TeamSize);
        var change = Assert.Single(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ExplicitCapacityChanged);
        Assert.Equal("21", change.Before);
        Assert.Equal("18", change.After);
        Assert.True(change.RequiresConfirmation);
        Assert.Contains("18 slot", ZaloAutoSessionPollRevalidationWorkflowV4.BuildOrganizerMessage(result));
    }

    [Fact]
    public void ExplicitCapacity_OverridesOlderOrganizerTeamSizeCorrection()
    {
        var source = Draft(teamSize: 7);
        var organizer = source with { TeamSize = 8 };
        var poll = Poll("Vote sân UTE tuần sau - tối đa 18 người");

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(teamSize: 7), source, organizer, Now);

        Assert.Equal(6, result.Reconciliation.Draft.TeamSize);
        Assert.True(result.RequiresOrganizerConfirmation);
        Assert.Contains(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ExplicitCapacityChanged);
    }

    [Fact]
    public void UnsupportedExplicitCapacity_FailsClosedWithGroundedIssue()
    {
        var source = Draft(teamSize: 6);
        var poll = Poll("Vote sân UTE tuần sau. Max 20 slots/sân");

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(teamSize: 6), source, source, Now);

        Assert.False(result.CanExecute);
        Assert.False(result.RequiresOrganizerConfirmation);
        var issue = Assert.Single(result.Issues, issue => issue.Code == "explicit_capacity_not_supported");
        Assert.Contains("20 slot", issue.Message);
        Assert.Contains("3 đội", issue.Message);
    }

    [Fact]
    public void NoExplicitCapacity_ApprovedTeamSizeRefreshRemainsNonMaterial()
    {
        var source = Draft(teamSize: 6);
        var poll = Poll("Vote sân UTE tuần sau");

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(teamSize: 7), source, source, Now);

        Assert.True(result.CanExecute);
        Assert.Equal(7, result.Reconciliation.Draft.TeamSize);
        Assert.Contains(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ApprovedTeamSizeChanged && !change.RequiresConfirmation);
    }

    [Fact]
    public void ConfirmedExplicitCapacity_BecomesRestartSafeBaseline()
    {
        var source = Draft(teamSize: 7);
        var poll = Poll("Vote sân UTE tuần sau. capacity 18 slots");
        var first = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(teamSize: 7), source, source, Now);

        Assert.True(first.RequiresOrganizerConfirmation);
        var second = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll,
            Tracked(teamSize: 7),
            first.CurrentSourceDraft,
            first.Reconciliation.Draft,
            Now.AddMinutes(5));

        Assert.True(second.CanExecute);
        Assert.Equal(6, second.Reconciliation.Draft.TeamSize);
        Assert.DoesNotContain(second.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ExplicitCapacityChanged);
    }

    private static ZaloTrackedGroupData Tracked(int teamSize) => new()
    {
        DefaultStartMinutes = 17 * 60 + 45,
        DefaultTeamSize = teamSize,
        DefaultLocation = "UTE",
        AssumePmForHourUnder12 = true
    };

    private static ZaloAutoSessionConversationDraft Draft(int teamSize) =>
        new([new ZaloAutoSessionConversationDraftItem("t6", "T6 11/9", "T6", At(), 8, true)], "UTE", teamSize);

    private static BridgePoll Poll(string question) => new(
        "poll-1",
        question,
        "organizer-1",
        [new BridgePollOption("t6", "T6 11/9", 8, [])],
        true,
        false,
        false,
        false,
        8,
        Now.AddHours(-1).ToUnixTimeMilliseconds(),
        Now.ToUnixTimeMilliseconds(),
        Now.AddDays(7).ToUnixTimeMilliseconds());

    private static DateTimeOffset At() =>
        new(2026, 9, 11, 17, 45, 0, TimeSpan.FromHours(7));
}
