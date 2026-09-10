using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionReconcilePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan MaxSilence = TimeSpan.FromHours(1);

    [Fact]
    public void NeverReconciled_AlwaysRunsSafetyScan()
    {
        var health = Health(
            lastPollEventAt: Now.AddMinutes(-2),
            lastReconcileAt: null,
            lastSuccessAt: Now.AddMinutes(-1));

        Assert.True(ZaloAutoSessionReconcilePolicy.ShouldRunProviderSafetyScan(
            health, Now, Freshness, MaxSilence));
    }

    [Fact]
    public void FreshRealtimeEventWithSuccessfulProcessing_DefersRedundantSafetyScan()
    {
        var health = Health(
            lastPollEventAt: Now.AddMinutes(-5),
            lastReconcileAt: Now.AddMinutes(-30),
            lastSuccessAt: Now.AddMinutes(-4));

        Assert.False(ZaloAutoSessionReconcilePolicy.ShouldRunProviderSafetyScan(
            health, Now, Freshness, MaxSilence));
    }

    [Fact]
    public void EventWithoutSuccess_DoesNotSuppressSafetyScan()
    {
        var health = Health(
            lastPollEventAt: Now.AddMinutes(-5),
            lastReconcileAt: Now.AddMinutes(-30),
            lastSuccessAt: Now.AddMinutes(-6));

        Assert.True(ZaloAutoSessionReconcilePolicy.ShouldRunProviderSafetyScan(
            health, Now, Freshness, MaxSilence));
    }

    [Fact]
    public void StaleRealtimeEvent_DoesNotSuppressSafetyScan()
    {
        var health = Health(
            lastPollEventAt: Now.AddMinutes(-21),
            lastReconcileAt: Now.AddMinutes(-30),
            lastSuccessAt: Now.AddMinutes(-20));

        Assert.True(ZaloAutoSessionReconcilePolicy.ShouldRunProviderSafetyScan(
            health, Now, Freshness, MaxSilence));
    }

    [Fact]
    public void MaximumSafetySilence_ForcesScanEvenWithContinuousHealthyRealtimeEvents()
    {
        var health = Health(
            lastPollEventAt: Now.AddMinutes(-2),
            lastReconcileAt: Now.AddHours(-1),
            lastSuccessAt: Now.AddMinutes(-1));

        Assert.True(ZaloAutoSessionReconcilePolicy.ShouldRunProviderSafetyScan(
            health, Now, Freshness, MaxSilence));
    }

    [Fact]
    public void FutureDatedEvent_DoesNotSuppressSafetyScan()
    {
        var health = Health(
            lastPollEventAt: Now.AddMinutes(1),
            lastReconcileAt: Now.AddMinutes(-10),
            lastSuccessAt: Now.AddMinutes(2));

        Assert.True(ZaloAutoSessionReconcilePolicy.ShouldRunProviderSafetyScan(
            health, Now, Freshness, MaxSilence));
    }

    private static ZaloAutoSessionHealthData Health(
        DateTimeOffset? lastPollEventAt,
        DateTimeOffset? lastReconcileAt,
        DateTimeOffset? lastSuccessAt) =>
        new(
            "tracked-1",
            lastPollEventAt,
            lastReconcileAt,
            lastSuccessAt,
            null,
            null,
            0,
            null);
}
