using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPassSlotGuidanceTests
{
    [Theory]
    [InlineData("@Npc ai pass slot thì gõ sao?")]
    [InlineData("@Npc pass slot gõ như nào?")]
    [InlineData("@Npc hướng dẫn nhường suất đi")]
    [InlineData("@Npc cú pháp pass slot là gì?")]
    public void Explicit_help_questions_are_owned_deterministically(string content)
    {
        var incoming = Explicit(content);

        Assert.True(ZaloOverbookService.IsPassSlotGuidanceQuestion(incoming));
    }

    [Theory]
    [InlineData("@Npc tui pass slot T6 nha")]
    [InlineData("@Npc tui nhận slot T6")]
    [InlineData("@Npc ai đang pass slot?")]
    [InlineData("@Npc tui muốn share slot với @To An hôm nay")]
    public void Real_actions_or_fact_queries_are_not_stolen_by_guidance(string content)
    {
        var incoming = Explicit(content);

        Assert.False(ZaloOverbookService.IsPassSlotGuidanceQuestion(incoming));
    }

    [Fact]
    public void Unmentioned_group_chatter_does_not_wake_guidance()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "bot-account",
            "bot-account",
            "g1",
            "m1",
            "user-1",
            "Long",
            "ai pass slot thì gõ sao ta",
            [],
            false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.False(ZaloOverbookService.IsPassSlotGuidanceQuestion(incoming));
    }

    [Fact]
    public void Reply_to_bot_can_request_guidance_without_a_fresh_mention()
    {
        var incoming = new ZaloIncomingMessageEvent(
            "bot-account",
            "bot-account",
            "g1",
            "m2",
            "user-1",
            "Long",
            "pass slot gõ sao?",
            [],
            false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ZaloBridgeMessageQuote(
                "bot-reply-1",
                "bot-account",
                "Npc",
                "Bạn muốn hỏi gì thêm?",
                "chat",
                DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
                null));

        Assert.True(ZaloOverbookService.IsPassSlotGuidanceQuestion(incoming));
    }

    [Fact]
    public void Guidance_teaches_only_existing_deterministic_paths_and_explains_share_difference()
    {
        var text = ZaloOverbookService.BuildPassSlotGuidance();

        Assert.Contains("pass slot T6", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tui nhận T6", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xong", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chốt", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ pass", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ nhận", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc @A pass slot cho @B", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("share slot", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("khác nhau", text, StringComparison.OrdinalIgnoreCase);
    }

    private static ZaloIncomingMessageEvent Explicit(string content) => new(
        "bot-account",
        "bot-account",
        "g1",
        Guid.NewGuid().ToString("n"),
        "user-1",
        "Long",
        content,
        [new ZaloBridgeMention("bot-account", 0, "@Npc".Length)],
        true,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
