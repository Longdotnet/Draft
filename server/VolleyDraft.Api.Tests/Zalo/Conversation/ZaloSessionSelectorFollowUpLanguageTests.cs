using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSessionSelectorFollowUpLanguageTests
{
    [Theory]
    [InlineData("trận CN")]
    [InlineData("buổi chủ nhật")]
    [InlineData("kèo T6")]
    [InlineData("chọn trận CN 13/9")]
    [InlineData("buổi 13/9 17h45")]
    public void Pending_session_follow_up_accepts_short_natural_selector_nouns(string content)
    {
        Assert.True(ZaloSessionResolver.LooksLikeStandaloneSelector(content));
    }

    [Theory]
    [InlineData("trận CN đi nhậu không?")]
    [InlineData("buổi CN đổi sang sân khác nhé")]
    [InlineData("kèo T6 ai đi?")]
    [InlineData("trận Mai")]
    [InlineData("Mai")]
    public void Ambient_human_chat_is_not_promoted_as_pending_session_selector(string content)
    {
        Assert.False(ZaloSessionResolver.LooksLikeStandaloneSelector(content));
    }
}
