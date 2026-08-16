using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistBooleanTests
{
    [Fact]
    public void Detector_returns_boolean_true_for_pass_slot()
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity("pass slot T6"));
    }
}
