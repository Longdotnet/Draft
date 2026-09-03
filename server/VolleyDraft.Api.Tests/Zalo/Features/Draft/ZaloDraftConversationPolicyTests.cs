using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftConversationPolicyTests
{
    [Theory]
    [InlineData("khi nào T6 có đội hình vậy?")]
    [InlineData("team đâu rồi")]
    [InlineData("T6 có team chưa?")]
    [InlineData("sắp đánh rồi chưa chia team à")]
    [InlineData("chưa draft hả?")]
    public void Natural_readiness_questions_are_detected(string message)
    {
        Assert.True(ZaloDraftConversationPolicy.IsReadinessQuestion(message));
    }

    [Theory]
    [InlineData("team B đánh căng đó")]
    [InlineData("draft này nhìn vui")]
    [InlineData("chia team B mạnh ghê")]
    public void Ordinary_team_chat_is_not_treated_as_readiness_question(string message)
    {
        Assert.False(ZaloDraftConversationPolicy.IsReadinessQuestion(message));
    }

    [Theory]
    [InlineData("draft đi")]
    [InlineData("chạy draft")]
    [InlineData("xác nhận draft")]
    [InlineData("chia team đi")]
    [InlineData("chốt team luôn")]
    [InlineData("triển draft")]
    public void Strong_confirmation_requires_explicit_draft_action(string message)
    {
        Assert.True(ZaloDraftConversationPolicy.IsStrongDraftConfirmation(message));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("ừ")]
    [InlineData("được")]
    [InlineData("chốt")]
    [InlineData("👍")]
    public void Weak_confirmation_never_becomes_strong_confirmation(string message)
    {
        Assert.True(ZaloDraftConversationPolicy.IsWeakConfirmation(message));
        Assert.False(ZaloDraftConversationPolicy.IsStrongDraftConfirmation(message));
    }

    [Theory]
    [InlineData("ừ tag đi")]
    [InlineData("gọi trưởng nhóm đi")]
    [InlineData("kêu phó nhóm giúp")]
    [InlineData("tag admin luôn")]
    public void Escalation_consent_requires_a_tag_or_call_action(string message)
    {
        Assert.True(ZaloDraftConversationPolicy.IsEscalationConsent(message));
    }

    [Theory]
    [InlineData("khỏi tag")]
    [InlineData("không cần gọi nữa")]
    [InlineData("dừng tag")]
    [InlineData("hủy yêu cầu")]
    public void Escalation_can_be_cancelled_naturally(string message)
    {
        Assert.True(ZaloDraftConversationPolicy.IsEscalationCancel(message));
    }
}
