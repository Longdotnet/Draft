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

    [Fact]
    public async Task Duplicate_delivery_is_terminal_before_any_pre_route_or_bot_side_effect()
    {
        var overbookCalls = 0;
        var botCalls = 0;
        var completeCalls = 0;
        var releaseCalls = 0;

        var result = await ZaloInboundCoordinator.DispatchClaimedAsync(
            Incoming("duplicate-before-routing"),
            (_, _) => Task.FromResult(ZaloInboundClaim.Duplicate),
            (_, _) =>
            {
                overbookCalls++;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                botCalls++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                completeCalls++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                releaseCalls++;
                return Task.CompletedTask;
            });

        Assert.True(result.Accepted);
        Assert.Equal("duplicate", result.HandledBy);
        Assert.Equal(0, overbookCalls);
        Assert.Equal(0, botCalls);
        Assert.Equal(0, completeCalls);
        Assert.Equal(0, releaseCalls);
    }

    [Fact]
    public async Task Pre_route_winner_marks_ingress_claim_terminal_without_releasing_to_bot()
    {
        var claim = new ZaloInboundClaim(true, false, "row-1", "token-1");
        var completed = new List<ZaloInboundClaim>();
        var released = new List<ZaloInboundClaim>();
        var botCalls = 0;

        var result = await ZaloInboundCoordinator.DispatchClaimedAsync(
            Incoming("claimed-pre-route"),
            (_, _) => Task.FromResult(claim),
            (_, _) => Task.FromResult(true),
            (_, _) =>
            {
                botCalls++;
                return Task.CompletedTask;
            },
            (current, _) =>
            {
                completed.Add(current);
                return Task.CompletedTask;
            },
            (current, _) =>
            {
                released.Add(current);
                return Task.CompletedTask;
            });

        Assert.Equal("overbook-confirmation", result.HandledBy);
        Assert.Single(completed);
        Assert.Equal(claim, completed[0]);
        Assert.Empty(released);
        Assert.Equal(0, botCalls);
    }

    [Fact]
    public async Task Declined_pre_route_releases_ingress_claim_before_bot_owns_message_lease()
    {
        var claim = new ZaloInboundClaim(true, false, "row-2", "token-2");
        var sequence = new List<string>();

        var result = await ZaloInboundCoordinator.DispatchClaimedAsync(
            Incoming("claimed-bot"),
            (_, _) => Task.FromResult(claim),
            (_, _) =>
            {
                sequence.Add("pre-route");
                return Task.FromResult(false);
            },
            (_, _) =>
            {
                sequence.Add("bot");
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                sequence.Add("complete");
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                sequence.Add("release");
                return Task.CompletedTask;
            });

        Assert.Equal("bot", result.HandledBy);
        Assert.Equal(["pre-route", "release", "bot"], sequence);
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
