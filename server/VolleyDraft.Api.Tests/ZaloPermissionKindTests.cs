using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionKindTests
{
    [Fact]
    public void Permission_mutation_kinds_are_distinct()
    {
        Assert.NotEqual(ZaloOperatorPermissionCommandKind.Grant, ZaloOperatorPermissionCommandKind.Revoke);
    }
}
