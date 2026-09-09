using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPollClassifierAuthorityTests
{
    [Fact]
    public void Ai_positive_cannot_promote_weak_deterministic_candidate_to_authoritative_poll()
    {
        var result = ZaloPollClassifierService.ResolveWithAi(
            ruleScore: 0.66,
            ruleReason: "has_schedule_option,multiple_schedule_options",
            aiIsSignup: true,
            aiConfidence: 0.99,
            aiReason: "looks_like_volleyball");

        Assert.False(result.IsVolleyballSignupPoll);
        Assert.True(result.SemanticCandidate);
        Assert.True(result.ShouldOfferManualReview);
        Assert.False(result.CanAutoExecute(requireOrganizerApproval: false));
        Assert.Equal(0.66, result.Confidence, 6);
        Assert.True(result.UsedAi);
        Assert.Contains("ai_suggest:looks_like_volleyball", result.Reason, StringComparison.Ordinal);
        Assert.Contains("deterministic_authority_required", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_positive_may_strengthen_confidence_after_deterministic_authority_exists()
    {
        var result = ZaloPollClassifierService.ResolveWithAi(
            ruleScore: 0.74,
            ruleReason: "has_schedule_option,volleyball_context",
            aiIsSignup: true,
            aiConfidence: 0.96,
            aiReason: "clear_signup_poll");

        Assert.True(result.IsVolleyballSignupPoll);
        Assert.True(result.ShouldOfferManualReview);
        Assert.True(result.CanAutoExecute(requireOrganizerApproval: false));
        Assert.False(result.CanAutoExecute(requireOrganizerApproval: true));
        Assert.Equal(0.96, result.Confidence, 6);
        Assert.True(result.UsedAi);
        Assert.Contains("ai:clear_signup_poll", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void High_confidence_ai_rejection_cannot_revoke_deterministic_authority()
    {
        var result = ZaloPollClassifierService.ResolveWithAi(
            ruleScore: 0.88,
            ruleReason: "has_schedule_option,volleyball_context,weekday_pattern",
            aiIsSignup: false,
            aiConfidence: 0.93,
            aiReason: "travel_poll");

        Assert.True(result.IsVolleyballSignupPoll);
        Assert.True(result.ShouldOfferManualReview);
        Assert.True(result.CanAutoExecute(requireOrganizerApproval: false));
        Assert.False(result.CanAutoExecute(requireOrganizerApproval: true));
        Assert.True(result.UsedAi);
        Assert.Contains("ai_disagrees:travel_poll", result.Reason, StringComparison.Ordinal);
        Assert.Contains("deterministic_authority_preserved", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void High_confidence_ai_rejection_can_reject_candidate_without_deterministic_authority()
    {
        var result = ZaloPollClassifierService.ResolveWithAi(
            ruleScore: 0.66,
            ruleReason: "has_schedule_option,multiple_schedule_options",
            aiIsSignup: false,
            aiConfidence: 0.93,
            aiReason: "travel_poll");

        Assert.False(result.IsVolleyballSignupPoll);
        Assert.False(result.SemanticCandidate);
        Assert.False(result.ShouldOfferManualReview);
        Assert.False(result.CanAutoExecute(requireOrganizerApproval: false));
        Assert.Equal(0.07, result.Confidence, 6);
        Assert.True(result.UsedAi);
        Assert.Contains("ai_reject:travel_poll", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Low_confidence_ai_negative_cannot_remove_deterministic_authority()
    {
        var result = ZaloPollClassifierService.ResolveWithAi(
            ruleScore: 0.81,
            ruleReason: "has_schedule_option,volleyball_context",
            aiIsSignup: false,
            aiConfidence: 0.40,
            aiReason: "uncertain");

        Assert.True(result.IsVolleyballSignupPoll);
        Assert.Equal(0.81, result.Confidence, 6);
        Assert.True(result.UsedAi);
    }
}
