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
    public void Action_and_fact_turns_are_not_help(string text)
    {
        Assert.Null(ZaloSlotWorkflowGuidance.TryBuild(text));
    }
}
