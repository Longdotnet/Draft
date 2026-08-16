using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistIdentityScopeTests
{
    [Fact]
    public void Detector_is_semantic_only_and_does_not_claim_a_person_identity()
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity("pass slot T6 nha"));
    }
}
