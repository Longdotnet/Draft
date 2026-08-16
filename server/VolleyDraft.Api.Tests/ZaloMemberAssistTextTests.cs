using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistTextTests
{
    [Fact]
    public void Pass_slot_phrase_remains_supported()
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity("pass slot"));
    }
}
