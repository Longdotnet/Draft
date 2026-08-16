using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistFinalTests
{
    [Fact]
    public void Pass_slot_contract_is_detected()
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity("pass slot T6"));
    }
}
