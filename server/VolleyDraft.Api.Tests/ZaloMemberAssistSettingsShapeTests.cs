using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistSettingsShapeTests
{
    [Fact]
    public void Settings_only_gate_enablement()
    {
        Assert.True(new ZaloMemberAssistSettings(true).Enabled);
        Assert.False(new ZaloMemberAssistSettings(false).Enabled);
    }
}
