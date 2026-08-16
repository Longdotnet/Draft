using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSelfServiceIdentityResultTests
{
    [Fact]
    public void Ambiguous_identity_is_not_success()
    {
        Assert.NotEqual(ZaloSelfServiceIdentityLinkResult.Linked, ZaloSelfServiceIdentityLinkResult.Ambiguous);
    }
}
