using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemoryV2CommandTests
{
    [Theory]
    [InlineData("@bot nhớ gì về tui?", ZaloMemoryCommandKind.List, null)]
    [InlineData("bot quên tên gọi của tui", ZaloMemoryCommandKind.ForgetKey, "preferred_name")]
    [InlineData("bot đừng nhớ lịch chơi của tui", ZaloMemoryCommandKind.ForgetKey, "session_availability")]
    [InlineData("bot quên vị trí libero của tui", ZaloMemoryCommandKind.ForgetKey, "volleyball_role")]
    [InlineData("bot xóa hết memory của tui", ZaloMemoryCommandKind.ForgetAll, null)]
    public void Parser_only_emits_explicit_memory_commands(
        string text,
        ZaloMemoryCommandKind kind,
        string? key)
    {
        Assert.True(ZaloMemoryV2Service.TryParseCommand(text, out var command));
        Assert.Equal(kind, command.Kind);
        Assert.Equal(key, command.ConceptKey);
    }

    [Theory]
    [InlineData("T6 còn slot không")]
    [InlineData("tui hay đánh T6")]
    [InlineData("Long đánh libero")]
    [InlineData("xóa player Long khỏi team")]
    public void Parser_does_not_confuse_domain_commands_with_memory_controls(string text)
    {
        Assert.False(ZaloMemoryV2Service.TryParseCommand(text, out _));
    }
}
