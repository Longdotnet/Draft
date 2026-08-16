using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistFalsePositiveTests
{
    [Theory]
    [InlineData("pass bóng qua đây")]
    [InlineData("pass wifi coi")]
    [InlineData("slot còn mấy chỗ")]
    [InlineData("ai share slot với tui")]
    [InlineData("đừng pass kèo nha")]
    [InlineData("thôi pass slot")]
    [InlineData("huỷ pass T6")]
    [InlineData("không pass nữa")]
    [InlineData("đừng bỏ slot nha")]
    public void Common_or_negated_group_chat_does_not_accidentally_open_pass_slot_help(string text)
    {
        Assert.False(ZaloMemberAssistService.IsPassSlotHelpOpportunity(text));
    }
}
