using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSelfServiceIdentityNoOverwriteTests
{
    [Fact]
    public void Conflict_result_is_explicitly_distinct_from_linked()
    {
        Assert.NotEqual(ZaloSelfServiceIdentityLinkResult.Linked, ZaloSelfServiceIdentityLinkResult.Conflict);
    }
}
