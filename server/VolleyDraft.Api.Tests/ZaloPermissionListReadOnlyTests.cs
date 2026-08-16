using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionListReadOnlyTests
{
    [Fact]
    public void List_command_requires_no_target_mentions()
    {
        var command = new ZaloOperatorPermissionCommand(ZaloOperatorPermissionCommandKind.List, []);
        Assert.Empty(command.TargetZaloUserIds);
    }
}
