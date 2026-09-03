using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistConfigTests
{
    [Fact]
    public void Member_assist_is_enabled_by_default_but_can_be_disabled_independently()
    {
        Assert.True(ZaloMemberAssistSettings.FromConfiguration(new ConfigurationBuilder().Build()).Enabled);

        var disabled = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:MemberAssist:Enabled"] = "false"
            })
            .Build();
        Assert.False(ZaloMemberAssistSettings.FromConfiguration(disabled).Enabled);
    }
}
