using System.Threading.Channels;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloSchedulerWakeReason
{
    ExternalTrigger,
    Watchdog
}

public sealed class ZaloSchedulerTrigger
{
    private readonly Channel<byte> channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public bool TryTrigger() => channel.Writer.TryWrite(1);

    internal async ValueTask<ZaloSchedulerWakeReason> WaitAsync(
        TimeSpan watchdogInterval,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(watchdogInterval, TimeSpan.Zero);

        using var watchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdogCancellation.CancelAfter(watchdogInterval);

        try
        {
            await channel.Reader.ReadAsync(watchdogCancellation.Token);
            return ZaloSchedulerWakeReason.ExternalTrigger;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ZaloSchedulerWakeReason.Watchdog;
        }
    }

    internal void Drain()
    {
        while (channel.Reader.TryRead(out _))
        {
        }
    }
}

public sealed class ZaloSchedulerWorker(
    ZaloSchedulerTrigger trigger,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ZaloSchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watchdogInterval = ResolveWatchdogInterval(configuration);

        // Durable reminders, pass-slot rescue and Auto Session lifecycle handoff should catch up
        // whenever the API process becomes available, even if the external scheduler missed the
        // wake-up that originally should have driven them.
        await RunCycleAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var wakeReason = await trigger.WaitAsync(watchdogInterval, stoppingToken);
            if (wakeReason == ZaloSchedulerWakeReason.ExternalTrigger)
                trigger.Drain();
            else
                logger.LogWarning(
                    "Zalo scheduler watchdog started a recovery cycle after {WatchdogMinutes} minutes without an external tick",
                    watchdogInterval.TotalMinutes);

            await RunCycleAsync(stoppingToken);
        }
    }

    internal static TimeSpan ResolveWatchdogInterval(IConfiguration configuration)
    {
        var configuredMinutes = configuration.GetValue<double?>("Scheduler:WatchdogIntervalMinutes");
        if (configuredMinutes is > 0 and <= 60)
            return TimeSpan.FromMinutes(configuredMinutes.Value);

        return DefaultWatchdogInterval;
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<ZaloListenerCoordinator>();
            await coordinator.EnsureAllAsync(cancellationToken);
            var result = await scope.ServiceProvider.GetRequiredService<ZaloReminderService>()
                .SendDueRemindersAsync(cancellationToken);

            var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
            var rescueService = new ZaloOpenSlotRescueService(
                db,
                scope.ServiceProvider.GetRequiredService<ZaloBridgeClient>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<ILogger<ZaloOpenSlotRescueService>>());
            var rescue = await rescueService.RunDueAsync(cancellationToken);

            // Match creation commits before V5 lifecycle handoff. A transient failure in that
            // post-commit window must survive request loss/restart and be retried from durable
            // Created proposal + link state rather than depending on the original webhook.
            var handoff = await new ZaloAutoSessionLifecycleHandoffStoreV5(db)
                .ReconcileMissingAsync(logger, cancellationToken);

            logger.LogInformation(
                "Triggered Zalo scheduler completed Groups={Groups} Sent={Sent} Failed={Failed} OpenSlotCandidates={OpenSlotCandidates} OpenSlotNudged={OpenSlotNudged} ClaimsReleased={ClaimsReleased} OffersClosed={OffersClosed} RescueFailed={RescueFailed} LifecycleCandidates={LifecycleCandidates} LifecycleHandedOff={LifecycleHandedOff} LifecycleFailed={LifecycleFailed}",
                result.GroupCount,
                result.SentCount,
                result.FailedCount,
                rescue.CandidateCount,
                rescue.NudgedCount,
                rescue.ClaimReleasedCount,
                rescue.ClosedCount,
                rescue.FailedCount,
                handoff.CandidateCount,
                handoff.HandedOffCount,
                handoff.FailedCount);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Triggered Zalo scheduler cycle failed");
        }
    }
}
