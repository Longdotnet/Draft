using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistNamingTests
{
    [Fact]
    public void Common_pass_slot_phrase_is_detected()
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity("pass slot nha"));
    }
}
