using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPassSlotGuidanceTests
{
    [Theory]
    [InlineData("@Npc ai pass slot thì gõ sao?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc ai pass slot thì gõ gì?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc nhường suất thì viết gì?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc nghỉ trận này nhập gì?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc pass slot lệnh nào?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc pass slot gõ như nào?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc hướng dẫn nhường suất đi", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc cú pháp pass slot là gì?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc tui nghỉ trận này thì làm sao?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc cho người khác đánh thì phải làm gì?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc nhường chỗ kiểu gì?", ZaloBotIntent.SlotTransfer)]
    [InlineData("@Npc share slot dùng sao?", ZaloBotIntent.ShareSlot)]
    [InlineData("@Npc share slot gõ gì?", ZaloBotIntent.ShareSlot)]
    public void Explicit_help_questions_are_owned_deterministically(string content, ZaloBotIntent intent)
    {
        var guidance = ZaloOverbookService.TryBuildAddressedSlotWorkflowGuidance(Explicit(content));

        Assert.NotNull(guidance);
        Assert.Equal(intent, guidance!.Intent);
    }

    [Theory]
    [InlineData("@Npc tui pass slot T6 nha")]
    [InlineData("@Npc tui nhận slot T6")]
    [InlineData("@Npc ai đang pass slot?")]
    [InlineData("@Npc tui muốn share slot với @To An hôm nay")]
    [InlineData("@Npc tui nghỉ trận này nha")]
    [InlineData("@Npc đừng pass slot thì làm sao")]
    public void Real_actions_fact_queries_or_negated_requests_are_not_stolen_by_guidance(string content)
    {
        Assert.Null(ZaloOverbookService.TryBuildAddressedSlotWorkflowGuidance(Explicit(content)));
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
            "tui nghỉ trận này thì làm sao cho người khác đánh ta",
            [],
            false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Null(ZaloOverbookService.TryBuildAddressedSlotWorkflowGuidance(incoming));
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
            "tui nghỉ trận này thì làm sao?",
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

        var guidance = ZaloOverbookService.TryBuildAddressedSlotWorkflowGuidance(incoming);

        Assert.NotNull(guidance);
        Assert.Equal(ZaloBotIntent.SlotTransfer, guidance!.Intent);
    }

    [Fact]
    public void Pass_guidance_teaches_existing_deterministic_handoff_steps()
    {
        var guidance = ZaloSlotWorkflowGuidance.TryBuild("tui nghỉ trận này thì làm sao cho người khác đánh?");

        Assert.NotNull(guidance);
        var text = guidance!.Text;
        Assert.Contains("nhường suất chơi", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pass slot T6", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tui nhận T6", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xong", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chủ mới", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chốt", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ pass", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huỷ nhận", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc @A pass slot cho @B", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quyền", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chưa biết người nhận", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("đừng đoán người nhận", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Share_guidance_explains_that_share_is_not_a_pass()
    {
        var guidance = ZaloSlotWorkflowGuidance.TryBuild("share slot dùng sao?");

        Assert.NotNull(guidance);
        Assert.Equal(ZaloBotIntent.ShareSlot, guidance!.Intent);
        Assert.Contains("khác pass slot", guidance.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tui muốn share slot với @To An T6", guidance.Text, StringComparison.OrdinalIgnoreCase);
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
