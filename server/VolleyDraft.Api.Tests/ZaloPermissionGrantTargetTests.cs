using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionGrantTargetTests
{
    [Fact]
    public void Grant_command_keeps_target_uid()
    {
        var command = new ZaloOperatorPermissionCommand(ZaloOperatorPermissionCommandKind.Grant, ["u1"]);
        Assert.Equal("u1", Assert.Single(command.TargetZaloUserIds));
    }
}
