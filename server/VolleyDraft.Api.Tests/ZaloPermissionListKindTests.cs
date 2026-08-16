using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionListKindTests
{
    [Fact]
    public void List_command_is_read_only_kind()
    {
        Assert.Equal(ZaloOperatorPermissionCommandKind.List, new ZaloOperatorPermissionCommand(ZaloOperatorPermissionCommandKind.List, []).Kind);
    }
}
