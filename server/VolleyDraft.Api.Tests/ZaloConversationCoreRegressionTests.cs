using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloConversationCoreRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 8, 40, 0, TimeSpan.Zero); // 15:40 VN

    private static readonly IReadOnlyList<ZaloSessionReference> Sessions =
    [
        new("wed-19", "Thứ 4 19/8", new DateTimeOffset(2026, 8, 19, 10, 30, 0, TimeSpan.Zero)),
        new("wed-26", "Thứ 4 26/8", new DateTimeOffset(2026, 8, 26, 10, 30, 0, TimeSpan.Zero)),
        new("wed-02", "T4 02/09 17:30 - Thứ 4 2/9", new DateTimeOffset(2026, 9, 2, 10, 30, 0, TimeSpan.Zero)),
        new("fri-04", "T6 04/09 17:30 - Thứ 6 4/9", new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.Zero)),
        new("sat-05", "T7 05/09 17:30 - Thứ 7 5/9", new DateTimeOffset(2026, 9, 5, 10, 30, 0, TimeSpan.Zero)),
        new("sun-06", "CN 6/9", new DateTimeOffset(2026, 9, 6, 10, 30, 0, TimeSpan.Zero))
    ];

    [Theory]
    [InlineData("T4 02/09")]
    [InlineData("thứ4 2/9")]
    [InlineData("T4 02/09 17:30")]
    [InlineData("Thứ 4 2/9 (17:30 thứ Tư 02/09)")]
    [InlineData("chia team sân 17:30 thứ Tư 02/09")]
    public void Explicit_calendar_date_never_drags_old_same_weekdays(string text)
    {
        var result = ZaloConversationCore.ResolveSessionReference(text, Sessions, Now);

        Assert.Equal(["wed-02"], result);
    }

    [Fact]
    public void Bare_weekday_resolves_nearest_still_relevant_occurrence()
    {
        var result = ZaloConversationCore.ResolveSessionReference("Thứ 4", Sessions, Now);

        Assert.Equal(["wed-02"], result);
    }

    [Fact]
    public void Relative_weekday_cn_nay_resolves_upcoming_sunday_only()
    {
        var result = ZaloConversationCore.ResolveSessionReference("cn này", Sessions, Now);

        Assert.Equal(["sun-06"], result);
    }

    [Fact]
    public void Canonical_session_name_is_exact_even_when_it_contains_a_weekday()
    {
        var result = ZaloConversationCore.ResolveSessionReference(
            "T4 02/09 17:30 - Thứ 4 2/9",
            Sessions,
            Now);

        Assert.Equal(["wed-02"], result);
    }

    [Theory]
    [InlineData("8 thứ 4", 8, "thu 4")]
    [InlineData("9 T4 02/09", 9, "t4 02/09")]
    [InlineData("10 CN", 10, "cn")]
    public void Menu_commands_share_one_session_reference_parser(string text, int expectedCommand, string expectedReference)
    {
        Assert.True(ZaloConversationCore.TryGetMenuCommand(text, out var command, out var reference));
        Assert.Equal(expectedCommand, command);
        Assert.Equal(expectedReference, reference);
    }

    [Theory]
    [InlineData("1 tuần đánh mấy lần")]
    [InlineData("help 1")]
    [InlineData("8 đồng bộ giúp tui")]
    public void Numeric_prefix_is_not_a_menu_command_without_a_valid_session_selector(string text)
    {
        Assert.False(ZaloConversationCore.TryGetMenuCommand(text, out _, out _));
    }

    [Theory]
    [InlineData("hỏi khỏi đi. Lười quá 🤣")]
    [InlineData("thôi khỏi")]
    [InlineData("khỏi đi")]
    [InlineData("không cần nữa")]
    public void Natural_cancel_phrases_clear_pending_work(string text)
    {
        Assert.True(ZaloConversationCore.IsNaturalCancel(text));
    }

    [Fact]
    public void Unrelated_ambient_chat_does_not_continue_draft_session_choice()
    {
        var disposition = ZaloConversationCore.ClassifyPendingSessionTurn(
            "DraftReadinessSessionChoice",
            "Nay chuyền mấy bạn team mình đau lưng",
            mentionedBot: false);

        Assert.Equal(ZaloPendingTurnDisposition.IgnoreCurrentTurn, disposition);
    }

    [Fact]
    public void Explicit_new_bot_turn_supersedes_stale_draft_session_choice()
    {
        var disposition = ZaloConversationCore.ClassifyPendingSessionTurn(
            "DraftReadinessSessionChoice",
            "@Npc test",
            mentionedBot: true);

        Assert.Equal(ZaloPendingTurnDisposition.SwitchToNewIntent, disposition);
    }

    [Theory]
    [InlineData("T4")]
    [InlineData("02/09")]
    [InlineData("T4 02/09")]
    [InlineData("@Npc T4 02/09")]
    public void Real_session_selector_continues_pending_choice(string text)
    {
        var disposition = ZaloConversationCore.ClassifyPendingSessionTurn(
            "DraftReadinessSessionChoice",
            text,
            mentionedBot: text.Contains("@Npc", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ZaloPendingTurnDisposition.ContinuePending, disposition);
    }

    [Fact]
    public void Intelligence_uses_the_same_operational_resolver()
    {
        var result = ZaloBotIntelligence.SelectOperationalSessionCandidateIds("T4 02/09", Sessions, Now);

        Assert.Equal(["wed-02"], result);
    }

    [Fact]
    public void Intelligence_accepts_command_8_with_a_session_reference()
    {
        Assert.True(ZaloBotIntelligence.TryGetMenuCommand("8 thứ 4", out var command, out var reference));
        Assert.Equal(8, command);
        Assert.Equal("thu 4", reference);
    }
}