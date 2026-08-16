using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionSourceOfTruthTests
{
    [Fact]
    public void Chat_permission_parser_reads_existing_json_shape_used_by_settings()
    {
        var ids = ZaloOperatorPermissionCommandService.ParseOperatorIds("[\"u1\",\"u2\",\"u1\"]");

        Assert.Equal(2, ids.Count);
        Assert.Contains("u1", ids);
        Assert.Contains("u2", ids);
    }

    [Fact]
    public void Malformed_existing_operator_json_fails_closed()
    {
        Assert.Empty(ZaloOperatorPermissionCommandService.ParseOperatorIds("not-json"));
    }
}
