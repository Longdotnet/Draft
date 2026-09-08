using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Proactive;

public sealed class ZaloSchedulerLeaseDurationTests
{
    [Fact]
    public void ResolveLeaseDuration_UsesTwoMinuteFloor_WhenWatchdogIsAggressive()
    {
        var configuration = BuildConfiguration();

        var lease = ZaloSchedulerWorker.ResolveLeaseDuration(
            configuration,
            TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromMinutes(2), lease);
    }

    [Fact]
    public void ResolveLeaseDuration_PreservesLongerWatchdog_WhenNoExplicitLeaseConfigured()
    {
        var configuration = BuildConfiguration();

        var lease = ZaloSchedulerWorker.ResolveLeaseDuration(
            configuration,
            TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromMinutes(15), lease);
    }

    [Fact]
    public void ResolveLeaseDuration_UsesExplicitLeaseConfiguration()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Scheduler:LeaseDurationMinutes"] = "3"
        });

        var lease = ZaloSchedulerWorker.ResolveLeaseDuration(
            configuration,
            TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromMinutes(3), lease);
    }

    [Theory]
    [InlineData("0.25")]
    [InlineData("1")]
    [InlineData("1.999")]
    public void ResolveLeaseDuration_ClampsExplicitConfigurationToSafetyFloor(string value)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Scheduler:LeaseDurationMinutes"] = value
        });

        var lease = ZaloSchedulerWorker.ResolveLeaseDuration(
            configuration,
            TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromMinutes(2), lease);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("61")]
    public void ResolveLeaseDuration_IgnoresInvalidExplicitConfiguration(string value)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Scheduler:LeaseDurationMinutes"] = value
        });

        var lease = ZaloSchedulerWorker.ResolveLeaseDuration(
            configuration,
            TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromMinutes(2), lease);
    }

    private static IConfiguration BuildConfiguration(
        Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();
}
