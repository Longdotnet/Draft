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

    [Fact]
    public async Task Queue_coalesces_a_burst_for_one_normalized_scope_to_the_newest_event()
    {
        var queue = new ZaloPollEventQueue();
        var older = new ZaloPollBoardEvent(
            "account-1_0", "group-1_0", "update_board", "leader-1", "poll", "poll-old", 1000);
        var newer = new ZaloPollBoardEvent(
            "account-1", "group-1", "update_board", "leader-2", "poll", "poll-new", 2000);

        Assert.True(queue.TryEnqueue(older));
        Assert.True(queue.TryEnqueue(newer));
        Assert.Equal(1, queue.PendingScopeCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("poll-new", reader.Current.BoardId);
        Assert.Equal("leader-2", reader.Current.ActorId);
        Assert.Equal(0, queue.PendingScopeCount);
    }

    [Fact]
    public void Queue_does_not_drop_unique_groups_when_more_than_the_old_capacity_arrive()
    {
        var queue = new ZaloPollEventQueue();

        for (var index = 0; index < 750; index++)
        {
            Assert.True(queue.TryEnqueue(new ZaloPollBoardEvent(
                "account-1",
                $"group-{index}",
                "update_board",
                "leader-1",
                "poll",
                $"poll-{index}",
                index)));
        }

        Assert.Equal(750, queue.PendingScopeCount);
    }

    [Fact]
    public async Task Event_arriving_while_scope_is_being_processed_gets_a_follow_up_turn()
    {
        var queue = new ZaloPollEventQueue();
        var first = new ZaloPollBoardEvent(
            "account-1", "group-1", "update_board", "leader-1", "poll", "poll-1", 1000);
        var second = first with { BoardId = "poll-2", OccurredAtUnixMs = 2000 };

        Assert.True(queue.TryEnqueue(first));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("poll-1", reader.Current.BoardId);

        Assert.True(queue.TryEnqueue(second));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("poll-2", reader.Current.BoardId);
    }
}
