using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloIdentityLinkResultEnumTests
{
    [Fact]
    public void Linked_and_conflict_outcomes_are_distinct()
    {
        Assert.NotEqual(ZaloSelfServiceIdentityLinkResult.Linked, ZaloSelfServiceIdentityLinkResult.Conflict);
    }
}
