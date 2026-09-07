using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionPollReconcilerV4Tests
{
    [Fact]
    public void VoteOnlyChange_RefreshesFactsWithoutDiscardingOrganizerDecisions()
    {
        var source = Draft(
            Item("t6", "T6 11/9", "T6", 11, 17, 45, 8),
            Item("cn", "CN 13/9", "CN", 13, 17, 45, 7));
        var durable = source with
        {
            Location = "Sân A",
            Items = source.Items.Select(item => item.OptionId == "t6"
                ? item with { StartTime = At(11, 18, 0), Selected = true }
                : item with { Selected = false }).ToList()
        };
        var current = source with
        {
            Items = source.Items.Select(item => item.OptionId == "t6"
                ? item with { VoteCount = 12 }
                : item with { VoteCount = 9 }).ToList()
        };

        var result = ZaloAutoSessionPollReconcilerV4.Reconcile(source, durable, current);

        Assert.True(result.HasChanges);
        Assert.False(result.RequiresConfirmation);
        Assert.All(result.Changes, change => Assert.Equal(ZaloAutoSessionPollChangeKindV4.VoteCountChanged, change.Kind));
        Assert.Equal("Sân A", result.Draft.Location);
        Assert.Equal(At(11, 18, 0), result.Draft.Items.Single(item => item.OptionId == "t6").StartTime);
        Assert.True(result.Draft.Items.Single(item => item.OptionId == "t6").Selected);
        Assert.False(result.Draft.Items.Single(item => item.OptionId == "cn").Selected);
        Assert.Equal(12, result.Draft.Items.Single(item => item.OptionId == "t6").VoteCount);
    }

    [Fact]
    public void AddedOption_IsVisibleButNeverSilentlySelected()
    {
        var source = Draft(Item("t6", "T6 11/9", "T6", 11, 17, 45, 8));
        var durable = source;
        var current = Draft(
            Item("t6", "T6 11/9", "T6", 11, 17, 45, 10),
            Item("cn", "CN 13/9", "CN", 13, 17, 45, 6));

        var result = ZaloAutoSessionPollReconcilerV4.Reconcile(source, durable, current);

        Assert.True(result.RequiresConfirmation);
        var change = Assert.Single(result.Changes.Where(item => item.Kind == ZaloAutoSessionPollChangeKindV4.OptionAdded));
        Assert.Equal("cn", change.OptionId);
        Assert.False(result.Draft.Items.Single(item => item.OptionId == "cn").Selected);
        Assert.True(result.Draft.Items.Single(item => item.OptionId == "t6").Selected);
    }

    [Fact]
    public void RemovedOption_IsDroppedFromExecutableDraftAndRequiresConfirmation()
    {
        var source = Draft(
            Item("t6", "T6 11/9", "T6", 11, 17, 45, 8),
            Item("cn", "CN 13/9", "CN", 13, 17, 45, 7));
        var durable = source with
        {
            Items = source.Items.Select(item => item with { Selected = true }).ToList()
        };
        var current = Draft(Item("t6", "T6 11/9", "T6", 11, 17, 45, 10));

        var result = ZaloAutoSessionPollReconcilerV4.Reconcile(source, durable, current);

        Assert.True(result.RequiresConfirmation);
        Assert.Contains(result.Changes, item =>
            item.Kind == ZaloAutoSessionPollChangeKindV4.OptionRemoved && item.OptionId == "cn");
        Assert.DoesNotContain(result.Draft.Items, item => item.OptionId == "cn");
    }

    [Fact]
    public void PollIdentityChange_OverridesPollOwnedIdentityAndFailsClosedForExecution()
    {
        var source = Draft(Item("t6", "T6 11/9", "T6", 11, 17, 45, 8));
        var durable = source with
        {
            Items = [source.Items[0] with { StartTime = At(11, 18, 0), Selected = true }]
        };
        var current = Draft(Item("t6", "CN 13/9", "CN", 13, 17, 45, 9));

        var result = ZaloAutoSessionPollReconcilerV4.Reconcile(source, durable, current);

        Assert.True(result.RequiresConfirmation);
        Assert.Contains(result.Changes, item => item.Kind == ZaloAutoSessionPollChangeKindV4.OptionIdentityChanged);
        var reconciled = Assert.Single(result.Draft.Items);
        Assert.Equal("CN 13/9", reconciled.OptionContent);
        Assert.Equal("CN", reconciled.DayKey);
    }

    [Fact]
    public void SourceTimeChange_WinsOrganizerTimeCorrectionAndRequiresFreshConfirmation()
    {
        var source = Draft(Item("t6", "T6 11/9", "T6", 11, 17, 45, 8));
        var durable = source with
        {
            Items = [source.Items[0] with { StartTime = At(11, 18, 0) }]
        };
        var current = Draft(Item("t6", "T6 11/9", "T6", 11, 19, 0, 9));

        var result = ZaloAutoSessionPollReconcilerV4.Reconcile(source, durable, current);

        Assert.True(result.RequiresConfirmation);
        Assert.Contains(result.Changes, item => item.Kind == ZaloAutoSessionPollChangeKindV4.ExplicitStartTimeChanged);
        Assert.Equal(At(11, 19, 0), Assert.Single(result.Draft.Items).StartTime);
    }

    [Fact]
    public void OrganizerTimeCorrection_SurvivesWhenPollTimeDidNotChange()
    {
        var source = Draft(Item("t6", "T6 11/9", "T6", 11, 17, 45, 8));
        var durable = source with
        {
            Items = [source.Items[0] with { StartTime = At(11, 18, 0) }]
        };
        var current = Draft(Item("t6", "T6 11/9", "T6", 11, 17, 45, 11));

        var result = ZaloAutoSessionPollReconcilerV4.Reconcile(source, durable, current);

        Assert.False(result.RequiresConfirmation);
        Assert.Equal(At(11, 18, 0), Assert.Single(result.Draft.Items).StartTime);
    }

    private static ZaloAutoSessionConversationDraft Draft(params ZaloAutoSessionConversationDraftItem[] items) =>
        new(items, "UTE", 9);

    private static ZaloAutoSessionConversationDraftItem Item(
        string id,
        string content,
        string dayKey,
        int day,
        int hour,
        int minute,
        int votes) =>
        new(id, content, dayKey, At(day, hour, minute), votes, true);

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(7));
}
