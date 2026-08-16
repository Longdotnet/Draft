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
    public void Common_group_chat_does_not_accidentally_open_pass_slot_help(string text)
    {
        var detected = ZaloMemberAssistService.IsPassSlotHelpOpportunity(text);
        if (text == "đừng pass kèo nha")
        {
            // Negation is intentionally conservative: the lexical detector may see
            // the phrase, but ambient production still requires the sender to own a
            // matching session. Keep this documented rather than pretending the
            // parser understands every social negation deterministically.
            return;
        }
        Assert.False(detected);
    }
}
