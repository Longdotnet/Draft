using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSchedulerHealthTests
{
    [Theory]
    [InlineData(null, 45)]
    [InlineData("0", 45)]
    [InlineData("-1", 45)]
    [InlineData("1441", 45)]
    [InlineData("20", 20)]
    [InlineData("1440", 1440)]
    public void ResolveStaleAfter_uses_bounded_override_or_three_watchdog_intervals(
        string? configured,
        double expectedMinutes)
    {
        var values = new Dictionary<string, string?>
        {
            ["Scheduler:WatchdogIntervalMinutes"] = "15"
        };
        if (configured is not null)
            values["Scheduler:HealthStaleAfterMinutes"] = configured;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        Assert.Equal(expectedMinutes, ZaloSchedulerHealth.ResolveStaleAfter(configuration).TotalMinutes);
    }

    [Fact]
    public void Evaluate_reports_never_succeeded_when_no_durable_heartbeat_exists()
    {
        var now = new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);

        var health = ZaloSchedulerHealth.Evaluate(null, now, TimeSpan.FromMinutes(45));

        Assert.Equal(ZaloSchedulerHealthState.NeverSucceeded, health.State);
        Assert.False(health.IsHealthy);
    }

    [Fact]
    public void Evaluate_reports_recent_success_as_healthy()
    {
        var now = new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(
            leaseUntil: now.AddMinutes(10),
            lastAttemptAt: now.AddMinutes(-10),
            lastSuccessAt: now.AddMinutes(-9));

        var health = ZaloSchedulerHealth.Evaluate(snapshot, now, TimeSpan.FromMinutes(45));

        Assert.Equal(ZaloSchedulerHealthState.Healthy, health.State);
        Assert.True(health.IsHealthy);
    }

    [Fact]
    public void Evaluate_reports_newer_failure_instead_of_hiding_it_behind_old_success()
    {
        var now = new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(
            leaseUntil: now.AddMinutes(-1),
            lastAttemptAt: now.AddMinutes(-6),
            lastSuccessAt: now.AddMinutes(-20),
            lastFailureAt: now.AddMinutes(-5));

        var health = ZaloSchedulerHealth.Evaluate(snapshot, now, TimeSpan.FromMinutes(45));

        Assert.Equal(ZaloSchedulerHealthState.Failed, health.State);
        Assert.False(health.IsHealthy);
    }

    [Fact]
    public void Evaluate_reports_expired_unterminated_attempt_instead_of_falling_back_to_recent_success()
    {
        var now = new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(
            leaseUntil: now.AddSeconds(-1),
            lastAttemptAt: now.AddMinutes(-1),
            lastSuccessAt: now.AddMinutes(-10));

        var health = ZaloSchedulerHealth.Evaluate(snapshot, now, TimeSpan.FromMinutes(45));

        Assert.Equal(ZaloSchedulerHealthState.Failed, health.State);
        Assert.False(health.IsHealthy);
    }

    [Fact]
    public void Evaluate_reports_stale_when_scheduler_stopped_succeeding()
    {
        var now = new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(
            leaseUntil: now.AddMinutes(-30),
            lastAttemptAt: now.AddHours(-2),
            lastSuccessAt: now.AddHours(-2));

        var health = ZaloSchedulerHealth.Evaluate(snapshot, now, TimeSpan.FromMinutes(45));

        Assert.Equal(ZaloSchedulerHealthState.Stale, health.State);
        Assert.False(health.IsHealthy);
    }

    [Fact]
    public void Evaluate_treats_current_owned_attempt_as_running_while_it_recovers_old_failure()
    {
        var now = new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(
            leaseUntil: now.AddMinutes(10),
            lastAttemptAt: now.AddMinutes(-1),
            lastSuccessAt: now.AddHours(-2),
            lastFailureAt: now.AddMinutes(-20));

        var health = ZaloSchedulerHealth.Evaluate(snapshot, now, TimeSpan.FromMinutes(45));

        Assert.Equal(ZaloSchedulerHealthState.Running, health.State);
        Assert.True(health.IsHealthy);
    }

    [Fact]
    public void Evaluate_does_not_call_old_attempt_running_after_lease_expired()
    {
        var now = new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(
            leaseUntil: now.AddSeconds(-1),
            lastAttemptAt: now.AddMinutes(-1),
            lastSuccessAt: now.AddHours(-2),
            lastFailureAt: now.AddMinutes(-20));

        var health = ZaloSchedulerHealth.Evaluate(snapshot, now, TimeSpan.FromMinutes(45));

        Assert.Equal(ZaloSchedulerHealthState.Failed, health.State);
        Assert.False(health.IsHealthy);
    }

    private static ZaloSchedulerLeaseSnapshot Snapshot(
        DateTimeOffset leaseUntil,
        DateTimeOffset? lastAttemptAt = null,
        DateTimeOffset? lastSuccessAt = null,
        DateTimeOffset? lastFailureAt = null) =>
        new("instance-a", leaseUntil, lastAttemptAt, lastSuccessAt, lastFailureAt);
}
