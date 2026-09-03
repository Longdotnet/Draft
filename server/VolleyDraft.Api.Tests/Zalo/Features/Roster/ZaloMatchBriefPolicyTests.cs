using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMatchBriefPolicyTests
{
    [Theory]
    [InlineData("bot tình hình T6 sao rồi")]
    [InlineData("bot T6 sao rồi?")]
    [InlineData("NPC CN đang sao rồi")]
    [InlineData("bot 28/08 tới đâu rồi?")]
    [InlineData("NPC kèo CN tới đâu rồi?")]
    [InlineData("@volleybot status roster T4")]
    [InlineData("bot có cần vào web không")]
    [InlineData("bot mở website không")]
    public void Match_brief_questions_are_detected(string message)
    {
        Assert.True(ZaloMatchBriefPolicy.IsQuestion(message));
    }

    [Theory]
    [InlineData("T6 có team chưa?")]
    [InlineData("khi nào T6 có đội hình vậy?")]
    [InlineData("chưa draft hả?")]
    public void Existing_readiness_questions_are_not_stolen(string message)
    {
        Assert.True(ZaloDraftConversationPolicy.IsReadinessQuestion(message));
        Assert.False(ZaloMatchBriefPolicy.IsQuestion(message));
    }

    [Theory]
    [InlineData("website đẹp không?")]
    [InlineData("web hôm nay lag ghê")]
    [InlineData("team B đánh căng đó")]
    [InlineData("slot đẹp nha")]
    [InlineData("bot sao rồi?")]
    public void Unrelated_chat_is_not_match_brief(string message)
    {
        Assert.False(ZaloMatchBriefPolicy.IsQuestion(message));
    }
}
