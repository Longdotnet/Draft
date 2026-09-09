using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloSchedulerWakeReason
{
    ExternalTrigger,
    Watchdog
}

internal static class ZaloSchedulerFailureKinds
{
    internal const string ListenerException = "listener_exception";
    internal const string ReminderException = "reminder_exception";
    internal const string RescueException = "rescue_exception";
    internal const string LifecycleException = "lifecycle_exception";
    internal const string SchedulerException = "scheduler_exception";
    internal const string ReminderFailed = "reminder_failed";
    internal const string RescueFailed = "rescue_failed";
    internal const string LifecycleFailed = "lifecycle_failed";
    internal const string ReminderRescueFailed = "reminder_rescue_failed";
    internal const string ReminderLifecycleFailed = "reminder_lifecycle_failed";
    internal const string RescueLifecycleFailed = "rescue_lifecycle_failed";
    internal const string MultipleStagesFailed = "multiple_stages_failed";
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

    internal async Task RequeueAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        await Task.Delay(delay, cancellationToken);
        TryTrigger();
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
    DateTimeOffset? LastFailureAt,
    string? LastFailureKind = null);

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

            CREATE TABLE IF NOT EXISTS "ZaloSchedulerFailureDiagnostics" (
                "Name" TEXT PRIMARY KEY,
                "FailureAt" TEXT NOT NULL,
                "FailureKind" TEXT NOT NULL
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
        string failureKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureKind);
        await EnsureAsync(cancellationToken);
        var failureAt = at.ToUniversalTime().ToString("O");
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LastFailureAt" = {{failureAt}}
            WHERE "Name" = {{LeaseName}} AND "OwnerId" = {{ownerId}};
            """, cancellationToken);

        if (affected == 0)
            return;

        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloSchedulerFailureDiagnostics" ("Name", "FailureAt", "FailureKind")
            VALUES ({{LeaseName}}, {{failureAt}}, {{failureKind}})
            ON CONFLICT ("Name") DO UPDATE SET
                "FailureAt" = excluded."FailureAt",
                "FailureKind" = excluded."FailureKind";
            """, cancellationToken);
    }

    internal async Task<bool> ReleaseAsync(
        string ownerId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await EnsureAsync(cancellationToken);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LeaseUntil" = {{at.ToUniversalTime().ToString("O")}}
            WHERE "Name" = {{LeaseName}} AND "OwnerId" = {{ownerId}};
            """, cancellationToken);
        return affected > 0;
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

        var diagnostic = await db.Database.SqlQueryRaw<ZaloSchedulerFailureDiagnosticRow>(
                """
                SELECT "FailureAt", "FailureKind"
                FROM "ZaloSchedulerFailureDiagnostics"
                WHERE "Name" = 'zalo-scheduler'
                """)
            .SingleOrDefaultAsync(cancellationToken);
        var lastFailureAt = Parse(row.LastFailureAt);
        var lastFailureKind = diagnostic is not null
            && Parse(diagnostic.FailureAt) == lastFailureAt
                ? diagnostic.FailureKind
                : null;

        return new ZaloSchedulerLeaseSnapshot(
            row.OwnerId,
            DateTimeOffset.Parse(row.LeaseUntil),
            Parse(row.LastAttemptAt),
            Parse(row.LastSuccessAt),
            lastFailureAt,
            lastFailureKind);
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

    private sealed class ZaloSchedulerFailureDiagnosticRow
    {
        public string FailureAt { get; init; } = string.Empty;
        public string FailureKind { get; init; } = string.Empty;
    }
}

internal sealed class ZaloSchedulerLeaseLostException : Exception
{
    internal ZaloSchedulerLeaseLostException()
        : base("Durable scheduler lease ownership changed while a scheduler stage was running.")
    {
    }
}

public sealed class ZaloSchedulerWorker(
    ZaloSchedulerTrigger trigger,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ZaloSchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LeaseContentionRetryDelay = TimeSpan.FromSeconds(5);
    private readonly string instanceId = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watchdogInterval = ResolveWatchdogInterval(configuration);
        var leaseDuration = ResolveLeaseDuration(configuration, watchdogInterval);

        // Durable reminders, pass-slot rescue and Auto Session lifecycle handoff should catch up
        // whenever the API process becomes available, even if the external scheduler missed the
        // wake-up that originally should have driven them.
        await RunCycleAsync(leaseDuration, retryOnLeaseContention: false, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var wakeReason = await trigger.WaitAsync(watchdogInterval, stoppingToken);
            var externalTrigger = wakeReason == ZaloSchedulerWakeReason.ExternalTrigger;
            if (externalTrigger)
                trigger.Drain();
            else
                logger.LogWarning(
                    "Zalo scheduler watchdog started a recovery cycle after {WatchdogMinutes} minutes without an external tick",
                    watchdogInterval.TotalMinutes);

            await RunCycleAsync(leaseDuration, retryOnLeaseContention: externalTrigger, stoppingToken);
        }
    }

    internal static TimeSpan ResolveWatchdogInterval(IConfiguration configuration)
    {
        var configuredMinutes = configuration.GetValue<double?>("Scheduler:WatchdogIntervalMinutes");
        if (configuredMinutes is > 0 and <= 60)
            return TimeSpan.FromMinutes(configuredMinutes.Value);

        return DefaultWatchdogInterval;
    }

    internal static TimeSpan ResolveLeaseDuration(
        IConfiguration configuration,
        TimeSpan watchdogInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(watchdogInterval, TimeSpan.Zero);

        var configuredMinutes = configuration.GetValue<double?>("Scheduler:LeaseDurationMinutes");
        if (configuredMinutes is > 0 and <= 60)
        {
            var configuredDuration = TimeSpan.FromMinutes(configuredMinutes.Value);
            return configuredDuration > MinimumLeaseDuration
                ? configuredDuration
                : MinimumLeaseDuration;
        }

        return watchdogInterval > MinimumLeaseDuration
            ? watchdogInterval
            : MinimumLeaseDuration;
    }

    internal static TimeSpan ResolveLeaseRenewalInterval(TimeSpan leaseDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        return TimeSpan.FromTicks(Math.Max(1, leaseDuration.Ticks / 3));
    }

    internal static bool HasStageFailures(
        int reminderFailedCount,
        int rescueFailedCount,
        int lifecycleFailedCount) =>
        reminderFailedCount > 0 || rescueFailedCount > 0 || lifecycleFailedCount > 0;

    internal static string ClassifyStageFailures(
        int reminderFailedCount,
        int rescueFailedCount,
        int lifecycleFailedCount)
    {
        var reminderFailed = reminderFailedCount > 0;
        var rescueFailed = rescueFailedCount > 0;
        var lifecycleFailed = lifecycleFailedCount > 0;

        return (reminderFailed, rescueFailed, lifecycleFailed) switch
        {
            (true, false, false) => ZaloSchedulerFailureKinds.ReminderFailed,
            (false, true, false) => ZaloSchedulerFailureKinds.RescueFailed,
            (false, false, true) => ZaloSchedulerFailureKinds.LifecycleFailed,
            (true, true, false) => ZaloSchedulerFailureKinds.ReminderRescueFailed,
            (true, false, true) => ZaloSchedulerFailureKinds.ReminderLifecycleFailed,
            (false, true, true) => ZaloSchedulerFailureKinds.RescueLifecycleFailed,
            (true, true, true) => ZaloSchedulerFailureKinds.MultipleStagesFailed,
            _ => throw new InvalidOperationException("Cannot classify a scheduler cycle without stage failures.")
        };
    }

    internal static async Task<T> RunWithLeaseHeartbeatAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task<bool>> renewLease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(renewLease);

        var renewalInterval = ResolveLeaseRenewalInterval(leaseDuration);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operationTask = operation(operationCancellation.Token);

        while (!operationTask.IsCompleted)
        {
            await Task.WhenAny(operationTask, Task.Delay(renewalInterval, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (operationTask.IsCompleted)
                break;

            bool renewed;
            try
            {
                renewed = await renewLease(cancellationToken);
            }
            catch
            {
                operationCancellation.Cancel();
                await ObserveAfterLeaseCancellationAsync(operationTask);
                throw;
            }

            if (renewed)
                continue;

            operationCancellation.Cancel();
            await ObserveAfterLeaseCancellationAsync(operationTask);
            throw new ZaloSchedulerLeaseLostException();
        }

        return await operationTask;
    }

    private async Task RunCycleAsync(
        TimeSpan leaseDuration,
        bool retryOnLeaseContention,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
        var lease = new ZaloSchedulerLeaseStore(db);
        var acquiredAt = DateTimeOffset.UtcNow;
        if (!await lease.TryAcquireAsync(instanceId, acquiredAt, leaseDuration, cancellationToken))
        {
            logger.LogInformation("Skipped Zalo scheduler cycle because another API instance owns the durable scheduler lease");
            if (retryOnLeaseContention)
            {
                logger.LogInformation(
                    "Preserving external Zalo scheduler wake and retrying lease acquisition in {RetrySeconds} seconds",
                    LeaseContentionRetryDelay.TotalSeconds);
                await trigger.RequeueAfterAsync(LeaseContentionRetryDelay, cancellationToken);
            }

            return;
        }

        await lease.MarkAttemptAsync(instanceId, acquiredAt, cancellationToken);
        var currentFailureKind = ZaloSchedulerFailureKinds.ListenerException;

        try
        {
            var coordinator = scope.ServiceProvider.GetRequiredService<ZaloListenerCoordinator>();
            await RunWithLeaseHeartbeatAsync(
                async stageToken =>
                {
                    await coordinator.EnsureAllAsync(stageToken);
                    return true;
                },
                RenewLeaseAsync,
                leaseDuration,
                cancellationToken);

            currentFailureKind = ZaloSchedulerFailureKinds.ReminderException;
            var result = await RunWithLeaseHeartbeatAsync(
                stageToken => scope.ServiceProvider.GetRequiredService<ZaloReminderService>()
                    .SendDueRemindersAsync(stageToken),
                RenewLeaseAsync,
                leaseDuration,
                cancellationToken);

            // Every potentially long provider/domain stage renews the lease from a separate scope.
            // This prevents a second API instance from taking ownership mid-stage merely because
            // the stage exceeded the lease duration.
            if (!await RenewLeaseAsync(cancellationToken))
                throw new ZaloSchedulerLeaseLostException();

            currentFailureKind = ZaloSchedulerFailureKinds.RescueException;
            var rescueService = new ZaloOpenSlotRescueService(
                db,
                scope.ServiceProvider.GetRequiredService<ZaloBridgeClient>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<ILogger<ZaloOpenSlotRescueService>>());
            var rescue = await RunWithLeaseHeartbeatAsync(
                rescueService.RunDueAsync,
                RenewLeaseAsync,
                leaseDuration,
                cancellationToken);

            if (!await RenewLeaseAsync(cancellationToken))
                throw new ZaloSchedulerLeaseLostException();

            // Match creation commits before V5 lifecycle handoff. A transient failure in that
            // post-commit window must survive request loss/restart and be retried from durable
            // Created proposal + link state rather than depending on the original webhook.
            currentFailureKind = ZaloSchedulerFailureKinds.LifecycleException;
            var handoffStore = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
            var handoff = await RunWithLeaseHeartbeatAsync(
                stageToken => handoffStore.ReconcileMissingAsync(logger, stageToken),
                RenewLeaseAsync,
                leaseDuration,
                cancellationToken);

            // Do not record or announce success if ownership changed in the narrow window after the
            // last stage. A successor is then responsible for the next authoritative cycle.
            if (!await RenewLeaseAsync(cancellationToken))
                throw new ZaloSchedulerLeaseLostException();

            currentFailureKind = ZaloSchedulerFailureKinds.SchedulerException;
            var completedAt = DateTimeOffset.UtcNow;
            var degraded = HasStageFailures(
                result.FailedCount,
                rescue.FailedCount,
                handoff.FailedCount);
            if (degraded)
            {
                // A cycle that executed to completion but failed durable/user-facing work is not a
                // successful recovery signal. Keep LastSuccessAt unchanged so /health/scheduler and
                // the external verifier cannot certify a Zalo delivery/reconciliation outage as healthy.
                var failureKind = ClassifyStageFailures(
                    result.FailedCount,
                    rescue.FailedCount,
                    handoff.FailedCount);
                await lease.MarkFailureAsync(instanceId, completedAt, failureKind, cancellationToken);
                logger.LogWarning(
                    "Zalo scheduler cycle completed degraded FailureKind={FailureKind} ReminderFailed={ReminderFailed} RescueFailed={RescueFailed} LifecycleFailed={LifecycleFailed}",
                    failureKind,
                    result.FailedCount,
                    rescue.FailedCount,
                    handoff.FailedCount);
            }
            else
            {
                await lease.MarkSuccessAsync(instanceId, completedAt, cancellationToken);
            }

            await lease.ReleaseAsync(instanceId, completedAt, cancellationToken);
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
        catch (ZaloSchedulerLeaseLostException)
        {
            logger.LogWarning("Stopped Zalo scheduler cycle because durable lease ownership changed while work was in progress");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var failedAt = DateTimeOffset.UtcNow;
            await lease.MarkFailureAsync(instanceId, failedAt, currentFailureKind, CancellationToken.None);
            await lease.ReleaseAsync(instanceId, failedAt, CancellationToken.None);
            logger.LogError(exception, "Triggered Zalo scheduler cycle failed FailureKind={FailureKind}", currentFailureKind);
        }

        async Task<bool> RenewLeaseAsync(CancellationToken renewCancellationToken)
        {
            // Lease heartbeats must not share the stage DbContext: provider/domain work can be using
            // that context concurrently, and EF DbContext is not safe for concurrent operations.
            await using var renewalScope = scopeFactory.CreateAsyncScope();
            var renewalDb = renewalScope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
            return await new ZaloSchedulerLeaseStore(renewalDb)
                .TryAcquireAsync(instanceId, DateTimeOffset.UtcNow, leaseDuration, renewCancellationToken);
        }
    }

    private static async Task ObserveAfterLeaseCancellationAsync(Task operationTask)
    {
        try
        {
            await operationTask;
        }
        catch
        {
            // The lease-loss decision is authoritative here. The operation is awaited only to avoid
            // leaving scoped services running in the background after their owning cycle exits.
        }
    }
}
