using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionRevokeTargetTests
{
    [Fact]
    public void Revoke_command_keeps_target_uid()
    {
        var command = new ZaloOperatorPermissionCommand(ZaloOperatorPermissionCommandKind.Revoke, ["u1"]);
        Assert.Equal("u1", Assert.Single(command.TargetZaloUserIds));
    }
}
