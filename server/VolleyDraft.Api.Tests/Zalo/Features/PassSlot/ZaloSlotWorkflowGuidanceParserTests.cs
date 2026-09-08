using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSlotWorkflowGuidanceParserTests
{
    [Theory]
    [InlineData("ai pass slot thì gõ sao")]
    [InlineData("pass slot ghi thế nào")]
    [InlineData("nhường suất nhập sao")]
    [InlineData("cách pass slot")]
    [InlineData("hướng dẫn pass slot")]
    [InlineData("tui nghỉ trận này thì làm sao")]
    [InlineData("em không đánh kèo này, phải làm gì")]
    [InlineData("cho người khác đánh thì làm sao")]
    [InlineData("để người khác vào thì làm gì")]
    [InlineData("nhường chỗ kiểu gì")]
    public void Pass_help_variants_map_to_pass_workflow(string text)
    {
        var result = ZaloSlotWorkflowGuidance.TryBuild(text);

        Assert.NotNull(result);
        Assert.Equal(ZaloBotIntent.SlotTransfer, result!.Intent);
    }

    [Theory]
    [InlineData("share slot dùng sao")]
    [InlineData("share slot sử dụng sao")]
    [InlineData("cú pháp share slot")]
    [InlineData("hướng dẫn chung slot")]
    [InlineData("share slot thì phải làm gì")]
    public void Share_help_variants_map_to_share_workflow(string text)
    {
        var result = ZaloSlotWorkflowGuidance.TryBuild(text);

        Assert.NotNull(result);
        Assert.Equal(ZaloBotIntent.ShareSlot, result!.Intent);
    }

    [Theory]
    [InlineData("tui pass slot T6")]
    [InlineData("tui nhận T6")]
    [InlineData("ai đang pass slot")]
    [InlineData("tui muốn share slot với To An")]
    [InlineData("đang nói chuyện về slot thôi")]
    [InlineData("tui nghỉ trận này nha")]
    [InlineData("cho người khác đánh trận này")]
    public void Action_and_fact_turns_are_not_help(string text)
    {
        Assert.Null(ZaloSlotWorkflowGuidance.TryBuild(text));
    }

    [Theory]
    [InlineData("đừng pass slot thì làm sao")]
    [InlineData("không nghỉ trận này thì làm sao")]
    [InlineData("đừng cho người khác đánh thì làm gì")]
    public void Negated_pass_language_does_not_teach_a_pass_mutation(string text)
    {
        Assert.Null(ZaloSlotWorkflowGuidance.TryBuild(text));
    }

    [Fact]
    public void Explicit_share_language_wins_over_nearby_pass_words()
    {
        var result = ZaloSlotWorkflowGuidance.TryBuild("share slot khác pass slot thế nào");

        Assert.NotNull(result);
        Assert.Equal(ZaloBotIntent.ShareSlot, result!.Intent);
    }
}
