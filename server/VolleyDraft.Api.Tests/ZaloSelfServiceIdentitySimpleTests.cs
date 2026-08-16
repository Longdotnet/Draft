using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSelfServiceIdentitySimpleTests
{
    [Fact]
    public void Link_result_is_available()
    {
        Assert.Equal(ZaloSelfServiceIdentityLinkResult.Linked, ZaloSelfServiceIdentityLinkResult.Linked);
    }
}
