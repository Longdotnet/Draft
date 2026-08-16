using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionResultTests
{
    [Fact]
    public void Permission_result_defaults_to_operator_permission_intent()
    {
        var result = new ZaloOperatorPermissionResult(true, "ok");
        Assert.Equal("OperatorPermission", result.Intent);
    }
}
