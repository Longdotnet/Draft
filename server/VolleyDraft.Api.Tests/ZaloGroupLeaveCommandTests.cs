using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGroupLeaveCommandTests
{
    [Fact]
    public void Farewell_is_fixed_and_matches_approved_text()
    {
        const string expected =
            "🥹 Vậy là đến lúc tui phải chào tạm biệt cả nhà rồi.\n\n" +
            "Cảm ơn mọi người đã tin tưởng, tương tác và hợp tác với tui trong suốt thời gian qua. Được đồng hành cùng mọi người trong những buổi chơi, những lần xếp team và cả những màn cà khịa vui vẻ là một trải nghiệm rất đáng nhớ.\n\n" +
            "Chúc cả nhà luôn vui vẻ, chơi hết mình và có thật nhiều trận đấu đáng nhớ nha! 🏐❤️\n\n" +
            "Tui xin phép rời nhóm đây. Hẹn gặp lại mọi người một ngày không xa! 👋";

        Assert.Equal(expected, ZaloGroupLeaveCommand.FarewellText);
        Assert.DoesNotContain("@", ZaloGroupLeaveCommand.FarewellText);
    }

    [Theory]
    [InlineData("bot ơi out nhóm đi", false)]
    [InlineData("Bott ơi rời nhóm đi", false)]
    [InlineData("bot ơi thoát group", false)]
    [InlineData("out nhóm đi", true)]
    [InlineData("rời khỏi nhóm", true)]
    [InlineData("bot ơi out nhóm đi", true)]
    public void Explicit_group_departure_is_a_request(string text, bool tagged)
    {
        Assert.Equal(ZaloGroupLeaveAction.Request, ZaloGroupLeaveCommand.Parse(text, tagged));
    }

    [Theory]
    [InlineData("bot ơi xác nhận out nhóm", false)]
    [InlineData("xác nhận rời nhóm", true)]
    [InlineData("đồng ý out nhóm", true)]
    public void Confirmation_requires_an_explicit_group_leave_phrase(string text, bool tagged)
    {
        Assert.Equal(ZaloGroupLeaveAction.Confirm, ZaloGroupLeaveCommand.Parse(text, tagged));
    }

    [Theory]
    [InlineData("bot ơi hủy out nhóm", false)]
    [InlineData("thôi không out nhóm", true)]
    [InlineData("hủy lệnh rời nhóm", true)]
    public void Cancellation_is_explicit(string text, bool tagged)
    {
        Assert.Equal(ZaloGroupLeaveAction.Cancel, ZaloGroupLeaveCommand.Parse(text, tagged));
    }

    [Theory]
    [InlineData("out nhóm đi", false)]
    [InlineData("bạn kêu bot ơi out nhóm đi", false)]
    [InlineData("bot ơi hôm nay ai out nhóm?", false)]
    [InlineData("tui tính out nhóm đi", true)]
    [InlineData("xác nhận", true)]
    [InlineData("hủy", true)]
    [InlineData("@Minh out nhóm đi", false)]
    public void Ordinary_discussion_cannot_trigger_departure(string text, bool tagged)
    {
        Assert.Equal(ZaloGroupLeaveAction.None, ZaloGroupLeaveCommand.Parse(text, tagged));
    }
}
