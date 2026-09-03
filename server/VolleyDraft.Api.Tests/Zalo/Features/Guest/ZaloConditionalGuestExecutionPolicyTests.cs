using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloConditionalGuestExecutionPolicyTests
{
    [Fact]
    public void ExecuteNotBefore_IsDeferredUntilGuestSignupWindow()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:DraftAutopilot:GuestSignupHoursBeforeStart"] = "2"
            })
            .Build();
        var start = new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero);
        var requested = new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

        var actual = ZaloConditionalGuestIntentPolicy.ResolveExecuteNotBefore(requested, start, configuration);

        Assert.Equal(start.AddHours(-2), actual);
    }

    [Fact]
    public void ExecuteNotBefore_PreservesLaterRequestedTime()
    {
        var configuration = new ConfigurationBuilder().Build();
        var start = new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero);
        var requested = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

        var actual = ZaloConditionalGuestIntentPolicy.ResolveExecuteNotBefore(requested, start, configuration);

        Assert.Equal(requested, actual);
    }
}
