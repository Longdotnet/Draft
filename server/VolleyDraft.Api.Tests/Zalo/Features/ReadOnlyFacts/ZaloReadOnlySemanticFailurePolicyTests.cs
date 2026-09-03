using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloReadOnlySemanticFailurePolicyTests
{
    [Theory]
    [InlineData("semantic_ai_error")]
    [InlineData("semantic_malformed_json")]
    [InlineData("semantic_budget_exhausted")]
    public void Planner_failures_suppress_downstream_ai_fallback(string reason)
    {
        var plan = ZaloReadOnlySemanticPlan.None(reason);

        Assert.True(ZaloReadOnlySemanticFailurePolicy.ShouldSuppressFallback(plan));
    }

    [Theory]
    [InlineData("semantic_disabled")]
    [InlineData("semantic_ai_not_configured")]
    [InlineData("not_a_readonly_question")]
    public void Non_failure_none_routes_preserve_existing_fallback_behavior(string reason)
    {
        var plan = ZaloReadOnlySemanticPlan.None(reason);

        Assert.False(ZaloReadOnlySemanticFailurePolicy.ShouldSuppressFallback(plan));
    }
}
