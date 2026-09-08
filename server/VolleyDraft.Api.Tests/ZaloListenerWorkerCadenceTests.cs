using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloListenerWorkerCadenceTests
{
    [Fact]
    public void Safety_reconcile_defaults_to_fifteen_minutes_instead_of_short_worker_tick()
    {
        var configuration = new ConfigurationBuilder().Build();

        var interval = ZaloListenerWorkerCadence.ResolveInterval(
            configuration,
            "AutoSession:SafetyReconcileSeconds",
            defaultSeconds: 900,
            minSeconds: 300,
            maxSeconds: 3600);

        Assert.Equal(TimeSpan.FromMinutes(15), interval);
        Assert.True(interval > TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void Listener_reconcile_defaults_to_five_minutes()
    {
        var configuration = new ConfigurationBuilder().Build();

        var interval = ZaloListenerWorkerCadence.ResolveInterval(
            configuration,
            "Zalo:ListenerReconcileSeconds",
            defaultSeconds: 300,
            minSeconds: 120,
            maxSeconds: 1800);

        Assert.Equal(TimeSpan.FromMinutes(5), interval);
    }

    [Theory]
    [InlineData("1", 300)]
    [InlineData("7200", 3600)]
    [InlineData("600", 600)]
    public void Provider_safety_interval_is_bounded(string configuredSeconds, int expectedSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AutoSession:SafetyReconcileSeconds"] = configuredSeconds
            })
            .Build();

        var interval = ZaloListenerWorkerCadence.ResolveInterval(
            configuration,
            "AutoSession:SafetyReconcileSeconds",
            defaultSeconds: 900,
            minSeconds: 300,
            maxSeconds: 3600);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [Fact]
    public void Reconcile_is_due_on_cold_start_then_waits_until_next_window()
    {
        var now = DateTimeOffset.Parse("2026-09-08T15:00:00Z");
        var next = DateTimeOffset.MinValue;

        Assert.True(ZaloListenerWorkerCadence.IsDue(now, next));

        next = ZaloListenerWorkerCadence.Next(now, TimeSpan.FromMinutes(15));
        Assert.False(ZaloListenerWorkerCadence.IsDue(now.AddMinutes(14).AddSeconds(59), next));
        Assert.True(ZaloListenerWorkerCadence.IsDue(now.AddMinutes(15), next));
    }
}
