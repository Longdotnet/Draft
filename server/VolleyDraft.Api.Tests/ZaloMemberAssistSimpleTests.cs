using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistSimpleTests
{
    [Fact]
    public void Pass_slot_is_detected()
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity("pass slot"));
    }
}
