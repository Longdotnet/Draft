using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Draft;

public sealed class ZaloScheduledDraftPolicyTests
{
    [Theory]
    [InlineData("bật tự draft", ZaloScheduledDraftCommandKind.Enable, 17 * 60 + 30)]
    [InlineData("bật tự draft lúc 18h", ZaloScheduledDraftCommandKind.Enable, 18 * 60)]
    [InlineData("bật tự draft lúc 17:45", ZaloScheduledDraftCommandKind.Enable, 17 * 60 + 45)]
    [InlineData("tắt tự draft", ZaloScheduledDraftCommandKind.Disable, null)]
    [InlineData("hôm nay không tự draft", ZaloScheduledDraftCommandKind.SkipSession, null)]
    [InlineData("hoãn draft đến 18h", ZaloScheduledDraftCommandKind.DeferSession, 18 * 60)]
    public void Scheduled_draft_commands_are_deterministic(
        string input,
        ZaloScheduledDraftCommandKind expectedKind,
        int? expectedMinute)
    {
        Assert.True(ZaloScheduledDraftService.TryParseCommand(input, out var command));
        Assert.Equal(expectedKind, command.Kind);
        if (expectedKind == ZaloScheduledDraftCommandKind.Enable && expectedMinute == 17 * 60 + 30 && command.LocalMinuteOfDay is null)
            return;
        Assert.Equal(expectedMinute, command.LocalMinuteOfDay);
    }

    [Theory]
    [InlineData("draft đi")]
    [InlineData("mai chơi nha")]
    [InlineData("hôm nay không đi")]
    [InlineData("hoãn lịch nhắc đến 18h")]
    public void Unrelated_messages_do_not_change_scheduled_draft_policy(string input)
    {
        Assert.False(ZaloScheduledDraftService.TryParseCommand(input, out _));
    }
}
