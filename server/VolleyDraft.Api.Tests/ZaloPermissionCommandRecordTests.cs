using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPermissionCommandRecordTests
{
    [Fact]
    public void Grant_command_exposes_target_collection()
    {
        Assert.Single(new ZaloOperatorPermissionCommand(ZaloOperatorPermissionCommandKind.Grant, ["u"]).TargetZaloUserIds);
    }
}
