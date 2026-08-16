using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionIdNormalizationTests
{
    [Fact]
    public void Legacy_uid_suffix_is_normalized_in_operator_storage()
    {
        var ids = ZaloOperatorPermissionCommandService.ParseOperatorIds("[\"user-1_0\"]");
        Assert.Contains("user-1", ids);
        Assert.DoesNotContain("user-1_0", ids);
    }
}
