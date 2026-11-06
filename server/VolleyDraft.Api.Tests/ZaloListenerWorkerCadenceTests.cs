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
    public void Listener_reconcile_defaults_to_short_worker_cadence_for_bridge_restart_recovery()
    {
        var configuration = new ConfigurationBuilder().Build();

        var interval = ZaloListenerWorkerCadence.ResolveInterval(
            configuration,
            "Zalo:ListenerReconcileSeconds",
            defaultSeconds: 45,
            minSeconds: 30,
            maxSeconds: 300);

        Assert.Equal(TimeSpan.FromSeconds(45), interval);
    }

    [Theory]
    [InlineData("1", 30)]
    [InlineData("600", 300)]
    [InlineData("90", 90)]
    public void Listener_recovery_interval_is_bounded(string configuredSeconds, int expectedSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Zalo:ListenerReconcileSeconds"] = configuredSeconds
            })
            .Build();

        var interval = ZaloListenerWorkerCadence.ResolveInterval(
            configuration,
            "Zalo:ListenerReconcileSeconds",
            defaultSeconds: 45,
            minSeconds: 30,
            maxSeconds: 300);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
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

    [Fact]
    public void Cold_start_runs_listener_control_plane_immediately_but_defers_provider_safety_scan()
    {
        var now = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
        var listenerInterval = TimeSpan.FromMinutes(5);
        var pollSafetyInterval = TimeSpan.FromMinutes(15);

        var listenerNext = ZaloListenerWorkerCadence.InitialNext(
            now,
            listenerInterval,
            runImmediately: true);
        var pollSafetyNext = ZaloListenerWorkerCadence.InitialNext(
            now,
            pollSafetyInterval,
            runImmediately: false);

        Assert.True(ZaloListenerWorkerCadence.IsDue(now, listenerNext));
        Assert.False(ZaloListenerWorkerCadence.IsDue(now, pollSafetyNext));
        Assert.False(ZaloListenerWorkerCadence.IsDue(now.AddMinutes(14).AddSeconds(59), pollSafetyNext));
        Assert.True(ZaloListenerWorkerCadence.IsDue(now.AddMinutes(15), pollSafetyNext));
    }
}
