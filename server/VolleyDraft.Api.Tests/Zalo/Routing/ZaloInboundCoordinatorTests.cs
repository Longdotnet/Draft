using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloInboundCoordinatorTests
{
    [Fact]
    public async Task Overbook_winner_is_terminal_and_bot_lane_is_not_invoked()
    {
        var overbookCalls = 0;
        var botCalls = 0;

        var result = await ZaloInboundCoordinator.DispatchAsync(
            Incoming("single-owner-overbook"),
            (_, _) =>
            {
                overbookCalls++;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                botCalls++;
                return Task.CompletedTask;
            });

        Assert.True(result.Accepted);
        Assert.Equal("overbook-confirmation", result.HandledBy);
        Assert.Equal(1, overbookCalls);
        Assert.Equal(0, botCalls);
    }

    [Fact]
    public async Task Bot_lane_runs_once_only_when_overbook_declines_turn()
    {
        var overbookCalls = 0;
        var botCalls = 0;

        var result = await ZaloInboundCoordinator.DispatchAsync(
            Incoming("single-owner-bot"),
            (_, _) =>
            {
                overbookCalls++;
                return Task.FromResult(false);
            },
            (_, _) =>
            {
                botCalls++;
                return Task.CompletedTask;
            });

        Assert.True(result.Accepted);
        Assert.Equal("bot", result.HandledBy);
        Assert.Equal(1, overbookCalls);
        Assert.Equal(1, botCalls);
    }

    private static ZaloIncomingMessageEvent Incoming(string messageId) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-1",
        senderName: "Long",
        content: "@Npc test",
        mentions: [],
        mentionedBot: true,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
