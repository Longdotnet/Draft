using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistSessionSelectorTests
{
    [Fact]
    public void Pass_slot_lexical_detector_accepts_common_unaccented_typo()
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity("em pass si lot toi nay a"));
    }
}
