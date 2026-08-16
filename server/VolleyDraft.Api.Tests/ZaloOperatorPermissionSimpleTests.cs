using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOperatorPermissionSimpleTests
{
    [Fact]
    public void Grant_kind_is_available()
    {
        Assert.Equal(ZaloOperatorPermissionCommandKind.Grant, ZaloOperatorPermissionCommandKind.Grant);
    }
}
