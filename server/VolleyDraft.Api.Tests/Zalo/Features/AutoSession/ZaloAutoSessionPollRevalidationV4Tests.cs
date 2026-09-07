using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionPollRevalidationV4Tests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void VoteOnlyDrift_CanExecute_AndPreservesOrganizerCorrection()
    {
        var source = Draft(Item("t6", "T6 11/9 17:45", "T6", 11, 17, 45, 8));
        var durable = source with
        {
            Location = "Sân A",
            Items = [source.Items[0] with { StartTime = At(11, 18, 0), Selected = true }]
        };
        var poll = Poll("Vote sân UTE tuần này", Option("t6", "T6 11/9 17:45", 12));

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(), source, durable, Now);

        Assert.True(result.CanExecute);
        Assert.Empty(result.Issues);
        Assert.False(result.Reconciliation.RequiresConfirmation);
        Assert.Equal(At(11, 18, 0), Assert.Single(result.Reconciliation.Draft.Items).StartTime);
        Assert.Equal(12, Assert.Single(result.Reconciliation.Draft.Items).VoteCount);
        Assert.Equal("Sân A", result.Reconciliation.Draft.Location);
    }

    [Fact]
    public void AddedOption_FailsClosedUntilOrganizerConfirms_AndOffersDeterministicSelector()
    {
        var source = Draft(Item("t6", "T6 11/9", "T6", 11, 17, 30, 8));
        var poll = Poll(
            "Vote sân UTE tuần này",
            Option("t6", "T6 11/9", 10),
            Option("cn", "CN 13/9", 6));

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(), source, source, Now);

        Assert.False(result.CanExecute);
        Assert.True(result.RequiresOrganizerConfirmation);
        Assert.Contains(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.OptionAdded && change.OptionId == "cn");
        Assert.False(result.Reconciliation.Draft.Items.Single(item => item.OptionId == "cn").Selected);
        var message = ZaloAutoSessionPollRevalidationWorkflowV4.BuildOrganizerMessage(result);
        Assert.Contains("thêm lựa chọn", message);
        Assert.Contains("CHƯA chọn", message);
        Assert.Contains("thêm CN", message);
        Assert.Contains("tạo đi", message);
    }

    [Fact]
    public void ExplicitSourceTimeChange_OverridesOldOrganizerCorrection_AndShowsAuthoritativeTime()
    {
        var source = Draft(Item("t6", "T6 11/9 17:45", "T6", 11, 17, 45, 8));
        var durable = source with
        {
            Items = [source.Items[0] with { StartTime = At(11, 18, 0) }]
        };
        var poll = Poll("Vote sân UTE", Option("t6", "T6 11/9 19:00", 9));

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(), source, durable, Now);

        Assert.False(result.CanExecute);
        Assert.True(result.RequiresOrganizerConfirmation);
        Assert.Equal(At(11, 19, 0), Assert.Single(result.Reconciliation.Draft.Items).StartTime);
        Assert.Contains(result.Reconciliation.Changes, change =>
            change.Kind == ZaloAutoSessionPollChangeKindV4.ExplicitStartTimeChanged);
        var message = ZaloAutoSessionPollRevalidationWorkflowV4.BuildOrganizerMessage(result);
        Assert.Contains("19:00", message);
        Assert.Contains("T6 18h", message);
        Assert.Contains("tạo đi", message);
    }

    [Fact]
    public void RemovedOption_ExplainsFailClosedDraftAndAuthoritativePollRecovery()
    {
        var source = Draft(
            Item("t6", "T6 11/9", "T6", 11, 17, 30, 8),
            Item("cn", "CN 13/9", "CN", 13, 17, 30, 6));
        var poll = Poll("Vote sân UTE", Option("t6", "T6 11/9", 9));

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(), source, source, Now);

        Assert.False(result.CanExecute);
        Assert.True(result.RequiresOrganizerConfirmation);
        Assert.DoesNotContain(result.Reconciliation.Draft.Items, item => item.OptionId == "cn");
        var message = ZaloAutoSessionPollRevalidationWorkflowV4.BuildOrganizerMessage(result);
        Assert.Contains("bỏ lựa chọn", message);
        Assert.Contains("loại khỏi bản nháp", message);
        Assert.Contains("sửa poll authoritative", message);
    }

    [Fact]
    public void InvalidWeekdayDateConflict_FailsClosedWithGroundedParserMessage()
    {
        var source = Draft(Item("t6", "T6 11/9", "T6", 11, 17, 30, 8));
        var poll = Poll("Vote sân UTE", Option("t6", "T5 11/9", 9));

        var result = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            poll, Tracked(), source, source, Now);

        Assert.False(result.CanExecute);
        Assert.False(result.RequiresOrganizerConfirmation);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("weekday_date_conflict", issue.Code);
        Assert.Contains("Website vẫn chưa được tạo", ZaloAutoSessionPollRevalidationWorkflowV4.BuildOrganizerMessage(result));
    }

    [Fact]
    public void SecondRevalidation_UsesLatestAuthoritativeSourceWithoutLosingOrganizerDecision()
    {
        var original = Draft(Item("t6", "T6 11/9", "T6", 11, 17, 30, 8));
        var organizer = original with { Location = "Sân A" };
        var firstPoll = Poll("Vote sân UTE", Option("t6", "T6 11/9", 10));
        var first = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            firstPoll, Tracked(), original, organizer, Now);
        Assert.True(first.CanExecute);

        var secondPoll = Poll("Vote sân UTE", Option("t6", "T6 11/9", 12));
        var second = ZaloAutoSessionPollRevalidationWorkflowV4.Evaluate(
            secondPoll,
            Tracked(),
            first.CurrentSourceDraft,
            first.Reconciliation.Draft,
            Now.AddMinutes(5));

        Assert.True(second.CanExecute);
        Assert.Equal("Sân A", second.Reconciliation.Draft.Location);
        Assert.Equal(12, Assert.Single(second.Reconciliation.Draft.Items).VoteCount);
    }

    private static ZaloTrackedGroupData Tracked() => new()
    {
        DefaultStartMinutes = 17 * 60 + 30,
        DefaultTeamSize = 6,
        AssumePmForHourUnder12 = true
    };

    private static ZaloAutoSessionConversationDraft Draft(params ZaloAutoSessionConversationDraftItem[] items) =>
        new(items, "UTE", 6);

    private static ZaloAutoSessionConversationDraftItem Item(
        string id, string content, string dayKey, int day, int hour, int minute, int votes) =>
        new(id, content, dayKey, At(day, hour, minute), votes, true);

    private static BridgePoll Poll(string question, params BridgePollOption[] options) => new(
        "poll-1",
        question,
        "organizer-1",
        options,
        true,
        false,
        false,
        false,
        options.Sum(option => option.VoteCount),
        Now.AddHours(-1).ToUnixTimeMilliseconds(),
        Now.ToUnixTimeMilliseconds(),
        Now.AddDays(7).ToUnixTimeMilliseconds());

    private static BridgePollOption Option(string id, string content, int votes) =>
        new(id, content, votes, []);

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(7));
}
