using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionHandledResultTests
{
    [Fact]
    public void Handled_result_carries_response()
    {
        var result = new ZaloOperatorPermissionResult(true, "ok");
        Assert.True(result.Handled);
        Assert.Equal("ok", result.Response);
    }
}
