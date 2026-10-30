using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGroupLeaveCommandTests
{
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
