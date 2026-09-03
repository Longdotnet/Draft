using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPollEventPolicyTests
{
    [Theory]
    [InlineData("update_board", true)]
    [InlineData("remove_board", true)]
    [InlineData("update_avatar", false)]
    public void Board_change_filter_accepts_update_and_remove(string eventType, bool expected)
    {
        Assert.Equal(expected, ZaloPollEventWorker.IsBoardChange(eventType));
    }

    [Fact]
    public void Remove_board_is_normalized_into_latest_open_poll_discovery()
    {
        var incoming = new ZaloPollBoardEvent(
            "account-1",
            "group-1",
            "remove_board",
            "leader-1",
            "poll",
            "old-poll-id",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var normalized = ZaloPollEventWorker.NormalizeForAutoSession(incoming);

        Assert.Equal("update_board", normalized.EventType);
        Assert.Null(normalized.BoardId);
        Assert.Equal(incoming.GroupId, normalized.GroupId);
        Assert.Equal(incoming.ActorId, normalized.ActorId);
    }

    [Fact]
    public void Update_board_keeps_exact_poll_id()
    {
        var incoming = new ZaloPollBoardEvent(
            "account-1",
            "group-1",
            "update_board",
            "leader-1",
            "poll",
            "new-poll-id",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(incoming, ZaloPollEventWorker.NormalizeForAutoSession(incoming));
    }
}
