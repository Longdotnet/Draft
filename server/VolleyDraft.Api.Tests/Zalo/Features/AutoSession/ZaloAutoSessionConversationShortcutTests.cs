using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionConversationShortcutTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

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
    [InlineData("tạo lịch ngày mai")]
    [InlineData("tạo website ngày mai")]
    public void CreateTomorrowCommand_IsDeterministic(string text)
    {
        Assert.Equal(
            ZaloAutoSessionConversationService.ConversationShortcut.CreateTomorrow,
            ZaloAutoSessionConversationService.ParseConversationShortcut(text));
    }

    [Fact]
    public void NegatedCreateTomorrow_DoesNotBecomeShortcut()
    {
        Assert.Equal(
            ZaloAutoSessionConversationService.ConversationShortcut.None,
            ZaloAutoSessionConversationService.ParseConversationShortcut("không tạo trận ngày mai"));
    }

    [Fact]
    public void TomorrowOptions_UseExactVietnamCalendarDate()
    {
        var now = new DateTimeOffset(2026, 10, 5, 23, 30, 0, VietnamOffset);
        var draft = new ZaloAutoSessionConversationDraft(
        [
            new("today", "T2 23h45", "T2", new DateTimeOffset(2026, 10, 5, 23, 45, 0, VietnamOffset), 5),
            new("tomorrow", "T3 18h", "T3", new DateTimeOffset(2026, 10, 6, 18, 0, 0, VietnamOffset), 8),
            new("later", "T4 18h", "T4", new DateTimeOffset(2026, 10, 7, 18, 0, 0, VietnamOffset), 9)
        ],
        null,
        6);

        var options = ZaloAutoSessionConversationService.GetTomorrowOptions(draft, now);

        var option = Assert.Single(options);
        Assert.Equal("tomorrow", option.OptionId);
    }

    [Fact]
    public void SelectOnlyOption_UsesOptionIdentity_WhenDayKeysRepeat()
    {
        var draft = new ZaloAutoSessionConversationDraft(
        [
            new("early", "T3 18h", "T3", new DateTimeOffset(2026, 10, 6, 18, 0, 0, VietnamOffset), 8),
            new("late", "T3 20h", "T3", new DateTimeOffset(2026, 10, 6, 20, 0, 0, VietnamOffset), 7)
        ],
        "Sân A",
        6);

        var selected = ZaloAutoSessionConversationService.SelectOnlyOption(draft, "late");

        Assert.False(selected.Items.Single(item => item.OptionId == "early").Selected);
        Assert.True(selected.Items.Single(item => item.OptionId == "late").Selected);
    }
}
