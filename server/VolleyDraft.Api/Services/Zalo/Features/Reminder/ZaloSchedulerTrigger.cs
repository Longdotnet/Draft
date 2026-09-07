using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
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

internal sealed record ZaloSchedulerLeaseSnapshot(
    string OwnerId,
    DateTimeOffset LeaseUntil,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt);

internal sealed class ZaloSchedulerLeaseStore(VolleyDraftDbContext db)
{
    private const string LeaseName = "zalo-scheduler";

    internal async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ZaloSchedulerLeases" (
                "Name" TEXT PRIMARY KEY,
                "OwnerId" TEXT NOT NULL,
                "LeaseUntil" TEXT NOT NULL,
                "LastAttemptAt" TEXT NULL,
                "LastSuccessAt" TEXT NULL,
                "LastFailureAt" TEXT NULL
            );
            """,
            cancellationToken);
    }

    internal async Task<bool> TryAcquireAsync(
        string ownerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        await EnsureAsync(cancellationToken);

        var nowText = now.ToUniversalTime().ToString("O");
        var leaseUntilText = now.Add(leaseDuration).ToUniversalTime().ToString("O");
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloSchedulerLeases" ("Name", "OwnerId", "LeaseUntil")
            VALUES ({{LeaseName}}, {{ownerId}}, {{leaseUntilText}})
            ON CONFLICT ("Name") DO UPDATE SET
                "OwnerId" = excluded."OwnerId",
                "LeaseUntil" = excluded."LeaseUntil"
            WHERE "ZaloSchedulerLeases"."OwnerId" = {{ownerId}}
               OR "ZaloSchedulerLeases"."LeaseUntil" <= {{nowText}};
            """, cancellationToken);

        return affected > 0;
    }

    internal async Task MarkAttemptAsync(
        string ownerId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LastAttemptAt" = {{at.ToUniversalTime().ToString("O")}}
            WHERE "Name" = {{LeaseName}} AND "OwnerId" = {{ownerId}};
            """, cancellationToken);
    }

    internal async Task MarkSuccessAsync(
        string ownerId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LastSuccessAt" = {{at.ToUniversalTime().ToString("O")}}
            WHERE "Name" = {{LeaseName}} AND "OwnerId" = {{ownerId}};
            """, cancellationToken);
    }

    internal async Task MarkFailureAsync(
        string ownerId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LastFailureAt" = {{at.ToUniversalTime().ToString("O")}}
            WHERE "Name" = {{LeaseName}} AND "OwnerId" = {{ownerId}};
            """, cancellationToken);
    }

    internal async Task<ZaloSchedulerLeaseSnapshot?> GetAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var row = await db.Database.SqlQueryRaw<ZaloSchedulerLeaseRow>(
                """
                SELECT "OwnerId", "LeaseUntil", "LastAttemptAt", "LastSuccessAt", "LastFailureAt"
                FROM "ZaloSchedulerLeases"
                WHERE "Name" = 'zalo-scheduler'
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
            return null;

        return new ZaloSchedulerLeaseSnapshot(
            row.OwnerId,
            DateTimeOffset.Parse(row.LeaseUntil),
            Parse(row.LastAttemptAt),
            Parse(row.LastSuccessAt),
            Parse(row.LastFailureAt));
    }

    private static DateTimeOffset? Parse(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.Parse(value);

    private sealed class ZaloSchedulerLeaseRow
    {
        public string OwnerId { get; init; } = string.Empty;
        public string LeaseUntil { get; init; } = string.Empty;
        public string? LastAttemptAt { get; init; }
        public string? LastSuccessAt { get; init; }
        public string? LastFailureAt { get; init; }
    }
}

public sealed class ZaloSchedulerWorker(
    ZaloSchedulerTrigger trigger,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ZaloSchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromMinutes(15);
    private readonly string instanceId = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watchdogInterval = ResolveWatchdogInterval(configuration);

        // Durable reminders, pass-slot rescue and Auto Session lifecycle handoff should catch up
        // whenever the API process becomes available, even if the external scheduler missed the
        // wake-up that originally should have driven them.
        await RunCycleAsync(watchdogInterval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var wakeReason = await trigger.WaitAsync(watchdogInterval, stoppingToken);
            if (wakeReason == ZaloSchedulerWakeReason.ExternalTrigger)
                trigger.Drain();
            else
                logger.LogWarning(
                    "Zalo scheduler watchdog started a recovery cycle after {WatchdogMinutes} minutes without an external tick",
                    watchdogInterval.TotalMinutes);

            await RunCycleAsync(watchdogInterval, stoppingToken);
        }
    }

    internal static TimeSpan ResolveWatchdogInterval(IConfiguration configuration)
    {
        var configuredMinutes = configuration.GetValue<double?>("Scheduler:WatchdogIntervalMinutes");
        if (configuredMinutes is > 0 and <= 60)
            return TimeSpan.FromMinutes(configuredMinutes.Value);

        return DefaultWatchdogInterval;
    }

    private async Task RunCycleAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
        var lease = new ZaloSchedulerLeaseStore(db);
        var acquiredAt = DateTimeOffset.UtcNow;
        if (!await lease.TryAcquireAsync(instanceId, acquiredAt, leaseDuration, cancellationToken))
        {
            logger.LogInformation("Skipped Zalo scheduler cycle because another API instance owns the durable scheduler lease");
            return;
        }

        await lease.MarkAttemptAsync(instanceId, acquiredAt, cancellationToken);

        try
        {
            var coordinator = scope.ServiceProvider.GetRequiredService<ZaloListenerCoordinator>();
            await coordinator.EnsureAllAsync(cancellationToken);
            var result = await scope.ServiceProvider.GetRequiredService<ZaloReminderService>()
                .SendDueRemindersAsync(cancellationToken);

            // Long-running reminder/provider work may consume most of the lease. Renew only if this
            // instance still owns it; if ownership was lost after expiry, fail closed and let the
            // new owner continue durable rescue/reconciliation instead of overlapping it.
            if (!await lease.TryAcquireAsync(instanceId, DateTimeOffset.UtcNow, leaseDuration, cancellationToken))
            {
                logger.LogWarning("Stopped Zalo scheduler cycle after durable lease ownership changed");
                return;
            }

            var rescueService = new ZaloOpenSlotRescueService(
                db,
                scope.ServiceProvider.GetRequiredService<ZaloBridgeClient>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<ILogger<ZaloOpenSlotRescueService>>());
            var rescue = await rescueService.RunDueAsync(cancellationToken);

            if (!await lease.TryAcquireAsync(instanceId, DateTimeOffset.UtcNow, leaseDuration, cancellationToken))
            {
                logger.LogWarning("Stopped Zalo scheduler cycle before lifecycle reconciliation because durable lease ownership changed");
                return;
            }

            // Match creation commits before V5 lifecycle handoff. A transient failure in that
            // post-commit window must survive request loss/restart and be retried from durable
            // Created proposal + link state rather than depending on the original webhook.
            var handoff = await new ZaloAutoSessionLifecycleHandoffStoreV5(db)
                .ReconcileMissingAsync(logger, cancellationToken);

            await lease.MarkSuccessAsync(instanceId, DateTimeOffset.UtcNow, cancellationToken);
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
            await lease.MarkFailureAsync(instanceId, DateTimeOffset.UtcNow, CancellationToken.None);
            logger.LogError(exception, "Triggered Zalo scheduler cycle failed");
        }
    }
}
