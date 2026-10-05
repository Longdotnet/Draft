using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionConversationShortcutTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, VietnamOffset);

    [Theory]
    [InlineData("option")]
    [InlineData("xem option")]
    [InlineData("lấy option hiện tại đang có")]
    [InlineData("cho tui xem các options")]
    public void ShowOptionsCommand_IsDeterministic(string text)
    {
        Assert.Equal(
            ZaloAutoSessionConversationService.ConversationShortcut.ShowOptions,
            ZaloAutoSessionConversationService.ParseConversationShortcut(text));
    }

    [Theory]
    [InlineData("tạo trận cho ngày mai")]
    [InlineData("tạo trận cho thứ 6 ngày 9/10")]
    [InlineData("tạo lịch T6")]
    [InlineData("tạo website 11/10")]
    public void CreateSelectionCommand_IsDeterministic(string text)
    {
        Assert.Equal(
            ZaloAutoSessionConversationService.ConversationShortcut.CreateSelection,
            ZaloAutoSessionConversationService.ParseConversationShortcut(text));
    }

    [Fact]
    public void FinalCreateConfirmation_IsNotASelectionShortcut()
    {
        Assert.Equal(
            ZaloAutoSessionConversationService.ConversationShortcut.None,
            ZaloAutoSessionConversationService.ParseConversationShortcut("tạo đi"));
    }

    [Fact]
    public void NegatedCreate_DoesNotBecomeShortcut()
    {
        Assert.Equal(
            ZaloAutoSessionConversationService.ConversationShortcut.None,
            ZaloAutoSessionConversationService.ParseConversationShortcut("không tạo trận ngày mai"));
    }

    [Fact]
    public void TomorrowPronounChatter_DoesNotBecomeCreateShortcut()
    {
        Assert.Equal(
            ZaloAutoSessionConversationService.ConversationShortcut.None,
            ZaloAutoSessionConversationService.ParseConversationShortcut("ngày mai tao đi"));
    }

    [Fact]
    public void ExactCalendarDate_DominatesWeekdayAndSelectsExactOption()
    {
        var draft = BuildDraft();

        var resolution = ZaloAutoSessionConversationService.ResolveDraftSelection(
            "tạo trận cho thứ 6 ngày 9/10",
            draft,
            Now);
        var options = ZaloAutoSessionConversationService.GetResolvedOptions(draft, resolution);

        Assert.Equal("calendar_date", resolution.Reason);
        var option = Assert.Single(options);
        Assert.Equal("fri-09", option.OptionId);
    }

    [Fact]
    public void BareWeekday_SelectsNearestRelevantOccurrence()
    {
        var draft = BuildDraft();

        var resolution = ZaloAutoSessionConversationService.ResolveDraftSelection(
            "tạo trận thứ 6",
            draft,
            Now);
        var option = Assert.Single(ZaloAutoSessionConversationService.GetResolvedOptions(draft, resolution));

        Assert.Equal("nearest_weekday", resolution.Reason);
        Assert.Equal("fri-09", option.OptionId);
    }

    [Fact]
    public void MultipleOptionsOnSameDate_RemainAmbiguous()
    {
        var draft = BuildDraft() with
        {
            Items =
            [
                new("early", "T6 18h", "T6", new DateTimeOffset(2026, 10, 9, 18, 0, 0, VietnamOffset), 8),
                new("late", "T6 20h", "T6", new DateTimeOffset(2026, 10, 9, 20, 0, 0, VietnamOffset), 7)
            ]
        };

        var resolution = ZaloAutoSessionConversationService.ResolveDraftSelection("9/10", draft, Now);
        var options = ZaloAutoSessionConversationService.GetResolvedOptions(draft, resolution);

        Assert.Equal(2, options.Count);
        Assert.False(resolution.IsExact);
    }

    [Theory]
    [InlineData("9/10")]
    [InlineData("đổi sang 11/10")]
    [InlineData("ngày mai nha")]
    public void FollowUpSelector_IsRecognizedAsDraftMutation(string text)
    {
        var resolution = ZaloAutoSessionConversationService.ResolveDraftSelection(text, BuildDraft(), Now);

        Assert.True(ZaloAutoSessionConversationService.ShouldApplySelectorFollowUp(text, resolution));
    }

    [Fact]
    public void DateMentionInOrdinaryComment_DoesNotMutateSelection()
    {
        const string text = "9/10 đông quá";
        var resolution = ZaloAutoSessionConversationService.ResolveDraftSelection(text, BuildDraft(), Now);

        Assert.False(ZaloAutoSessionConversationService.ShouldApplySelectorFollowUp(text, resolution));
    }

    [Fact]
    public void CreateSelection_FirstTurnNeverExecutesImmediately()
    {
        var draft = ZaloAutoSessionConversationService.SelectOnlyOption(BuildDraft(), "fri-09");

        var interpretation = ZaloAutoSessionConversationService.InterpretSelectorMutation(
            "tạo trận cho thứ 6 ngày 9/10",
            draft,
            ZaloAutoSessionConversationState.PreviewSent,
            null);

        Assert.Equal(ZaloAutoSessionConversationIntent.ModifyDraft, interpretation.Intent);
        Assert.False(interpretation.ExplicitExecute);
        Assert.Empty(interpretation.Days);
        Assert.Equal(ZaloAutoSessionSelectionMode.None, interpretation.SelectionMode);
    }

    [Fact]
    public void FollowUpDateChange_SelectsNewExactOption()
    {
        var current = ZaloAutoSessionConversationService.SelectOnlyOption(BuildDraft(), "fri-09");
        var resolution = ZaloAutoSessionConversationService.ResolveDraftSelection("đổi sang 11/10", current, Now);
        var option = Assert.Single(ZaloAutoSessionConversationService.GetResolvedOptions(current, resolution));

        var changed = ZaloAutoSessionConversationService.SelectOnlyOption(current, option.OptionId);

        Assert.Equal("sun-11", option.OptionId);
        Assert.True(changed.Items.Single(item => item.OptionId == "sun-11").Selected);
        Assert.False(changed.Items.Single(item => item.OptionId == "fri-09").Selected);
    }

    [Fact]
    public void FollowUpTime_ChangesOnlyPreviouslySelectedOption_WhenWeekdayRepeats()
    {
        var draft = ZaloAutoSessionConversationService.SelectOnlyOption(BuildDraft(), "fri-09");
        var interpretation = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "đổi 19h",
            draft,
            ZaloAutoSessionConversationState.ReadyToConfirm,
            null);

        Assert.True(ZaloAutoSessionConversationService.ApplyInterpretation(ref draft, interpretation));

        Assert.Equal(19, draft.Items.Single(item => item.OptionId == "fri-09").StartTime.Hour);
        Assert.Equal(18, draft.Items.Single(item => item.OptionId == "fri-16").StartTime.Hour);
    }

    [Fact]
    public void FollowUpLocationAndCapacity_KeepPreviouslySelectedDate()
    {
        var draft = ZaloAutoSessionConversationService.SelectOnlyOption(BuildDraft(), "fri-09");
        var interpretation = ZaloAutoSessionConversationInterpreter.InterpretByRules(
            "sân B, 21 người",
            draft,
            ZaloAutoSessionConversationState.ReadyToConfirm,
            null);

        Assert.True(ZaloAutoSessionConversationService.ApplyInterpretation(ref draft, interpretation));

        Assert.Equal("B", draft.Location);
        Assert.Equal(7, draft.TeamSize);
        Assert.True(draft.Items.Single(item => item.OptionId == "fri-09").Selected);
        Assert.Single(draft.Items.Where(item => item.Selected));
    }

    private static ZaloAutoSessionConversationDraft BuildDraft() => new(
    [
        new("mon-05", "T2 18h", "T2", new DateTimeOffset(2026, 10, 5, 18, 0, 0, VietnamOffset), 5),
        new("fri-09", "T6 18h", "T6", new DateTimeOffset(2026, 10, 9, 18, 0, 0, VietnamOffset), 8),
        new("sun-11", "CN 17h30", "CN", new DateTimeOffset(2026, 10, 11, 17, 30, 0, VietnamOffset), 9),
        new("fri-16", "T6 18h", "T6", new DateTimeOffset(2026, 10, 16, 18, 0, 0, VietnamOffset), 7)
    ],
    "Sân UTE",
    6);
}
