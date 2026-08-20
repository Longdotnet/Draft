using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloStickerPolicyTests
{
    [Theory]
    [InlineData("@Long haha ông nói nghe ghê =))", ZaloStickerReaction.Laugh)]
    [InlineData("@Long nay cháy quá 🔥", ZaloStickerReaction.Cheer)]
    [InlineData("@Long thương ông 🫶", ZaloStickerReaction.Love)]
    [InlineData("@Long wow ghê vậy 😱", ZaloStickerReaction.Wow)]
    [InlineData("@Long buồn thiệt 😭", ZaloStickerReaction.Sad)]
    [InlineData("@Long xin lỗi nha 🙏", ZaloStickerReaction.Sorry)]
    [InlineData("@Long bó tay ông luôn 🤦", ZaloStickerReaction.Facepalm)]
    [InlineData("@Long đánh ngon 👏", ZaloStickerReaction.GoodJob)]
    [InlineData("@Long ngủ ngon nha 👋", ZaloStickerReaction.Bye)]
    public void InferReaction_maps_expressive_chat_to_supported_sticker(string message, ZaloStickerReaction expected)
    {
        Assert.Equal(expected, ZaloStickerPolicy.InferReaction(message));
        Assert.False(string.IsNullOrWhiteSpace(ZaloStickerPolicy.ToWireValue(expected)));
    }

    [Fact]
    public void Operational_reply_does_not_plan_a_sticker()
    {
        var configuration = Settings(chancePercent: 100, cooldownSeconds: 0);

        var planned = ZaloStickerPolicy.TryPlan(
            "bot-1",
            "group-business",
            "@Long T6 đang có 11/12 slot, còn thiếu 1 slot 😄",
            null,
            "bot-1:message-business",
            configuration,
            DateTimeOffset.UtcNow,
            out _);

        Assert.False(planned);
    }

    [Fact]
    public void Direct_expressive_reply_can_plan_a_sticker_but_respects_cooldown()
    {
        var configuration = Settings(chancePercent: 100, cooldownSeconds: 120);
        var now = DateTimeOffset.UtcNow;

        var first = ZaloStickerPolicy.TryPlan(
            "bot-2",
            "group-cooldown",
            "@Long haha chịu ông luôn =))",
            null,
            "bot-2:message-1",
            configuration,
            now,
            out var reaction);
        var second = ZaloStickerPolicy.TryPlan(
            "bot-2",
            "group-cooldown",
            "@Long cười xỉu =))",
            null,
            "bot-2:message-2",
            configuration,
            now.AddSeconds(10),
            out _);

        Assert.True(first);
        Assert.Equal(ZaloStickerReaction.Laugh, reaction);
        Assert.False(second);
    }

    [Fact]
    public void Media_reply_and_non_direct_send_do_not_plan_stickers()
    {
        var configuration = Settings(chancePercent: 100, cooldownSeconds: 0);
        var now = DateTimeOffset.UtcNow;

        Assert.False(ZaloStickerPolicy.TryPlan(
            "bot-3",
            "group-media",
            "@Long quá đỉnh 🔥",
            "https://example.test/card.jpg",
            "bot-3:message-media",
            configuration,
            now,
            out _));

        Assert.False(ZaloStickerPolicy.TryPlan(
            "bot-3",
            "group-proactive",
            "Morning cả nhà haha =))",
            null,
            "social-greeting:group-proactive:20260820:Morning",
            configuration,
            now,
            out _));
    }

    private static IConfiguration Settings(int chancePercent, int cooldownSeconds) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Sticker:Enabled"] = "true",
                ["ZaloBot:Sticker:ChancePercent"] = chancePercent.ToString(),
                ["ZaloBot:Sticker:CooldownSeconds"] = cooldownSeconds.ToString()
            })
            .Build();
}
