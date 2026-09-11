using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloSchedulerWakeReason
{
    ExternalTrigger,
    Watchdog
}

internal enum ZaloSchedulerStage
{
    Listener,
    Reminder,
    Rescue,
    Lifecycle,
    Finalize
}

internal static class ZaloSchedulerFailureCodes
{
    internal static string BuildDegraded(
        int reminderFailedCount,
        int rescueFailedCount,
        int lifecycleFailedCount)
    {
        var stages = new List<string>(3);
        if (reminderFailedCount > 0)
            stages.Add("reminder");
        if (rescueFailedCount > 0)
            stages.Add("rescue");
        if (lifecycleFailedCount > 0)
            stages.Add("lifecycle");

        return stages.Count == 0
            ? "degraded:unknown"
            : $"degraded:{string.Join('+', stages)}";
    }

    internal static string ForException(ZaloSchedulerStage stage) =>
        $"exception:{stage.ToString().ToLowerInvariant()}";

    internal static bool IsValid(string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 96)
            return false;

        return failureCode.All(ch =>
            char.IsAsciiLetterLower(ch) || ch is ':' or '+');
    }
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
    string? LastFailureCode);

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
                "FailureCode" TEXT NOT NULL
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
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        if (!ZaloSchedulerFailureCodes.IsValid(failureCode))
            throw new ArgumentException("Scheduler failure code must be a bounded deterministic code.", nameof(failureCode));

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
            INSERT INTO "ZaloSchedulerFailureDiagnostics" ("Name", "FailureAt", "FailureCode")
            SELECT {{LeaseName}}, {{failureAt}}, {{failureCode}}
            WHERE EXISTS (
                SELECT 1
                FROM "ZaloSchedulerLeases"
                WHERE "Name" = {{LeaseName}}
                  AND "OwnerId" = {{ownerId}}
                  AND "LastFailureAt" = {{failureAt}}
            )
            ON CONFLICT ("Name") DO UPDATE SET
                "FailureAt" = excluded."FailureAt",
                "FailureCode" = excluded."FailureCode";
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
                SELECT lease."OwnerId", lease."LeaseUntil", lease."LastAttemptAt", lease."LastSuccessAt", lease."LastFailureAt",
                       CASE
                           WHEN diagnostic."FailureAt" = lease."LastFailureAt" THEN diagnostic."FailureCode"
                           ELSE NULL
                       END AS "LastFailureCode"
                FROM "ZaloSchedulerLeases" AS lease
                LEFT JOIN "ZaloSchedulerFailureDiagnostics" AS diagnostic
                  ON diagnostic."Name" = lease."Name"
                WHERE lease."Name" = 'zalo-scheduler'
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
            return null;

        return new ZaloSchedulerLeaseSnapshot(
            row.OwnerId,
            DateTimeOffset.Parse(row.LeaseUntil),
            Parse(row.LastAttemptAt),
            Parse(row.LastSuccessAt),
            Parse(row.LastFailureAt),
            row.LastFailureCode);
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
        public string? LastFailureCode { get; init; }
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

        try
        {
            while (!operationTask.IsCompleted)
            {
                await Task.WhenAny(operationTask, Task.Delay(renewalInterval, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                if (operationTask.IsCompleted)
                    break;

                bool renewed;
                try
                {
                    var renewalTask = renewLease(cancellationToken);
                    var renewalCompleted = await Task.WhenAny(
                        renewalTask,
                        Task.Delay(renewalInterval, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();

                    if (renewalCompleted != renewalTask)
                    {
                        operationCancellation.Cancel();
                        await ObserveAfterLeaseCancellationAsync(operationTask);
                        ObserveDetachedRenewalTask(renewalTask);
                        throw new ZaloSchedulerLeaseLostException();
                    }

                    renewed = await renewalTask;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            await ObserveAfterLeaseCancellationAsync(operationTask);
            throw;
        }
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
        var stage = ZaloSchedulerStage.Listener;

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

            stage = ZaloSchedulerStage.Reminder;
            var result = await RunWithLeaseHeartbeatAsync(
                stageToken => scope.ServiceProvider.GetRequiredService<ZaloReminderService>()
                    .SendDueRemindersAsync(stageToken),
                RenewLeaseAsync,
                leaseDuration,
                cancellationToken);

            if (!await RenewLeaseAsync(cancellationToken))
                throw new ZaloSchedulerLeaseLostException();

            stage = ZaloSchedulerStage.Rescue;
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

            stage = ZaloSchedulerStage.Lifecycle;
            var handoffStore = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
            var handoff = await RunWithLeaseHeartbeatAsync(
                stageToken => handoffStore.ReconcileMissingAsync(logger, stageToken),
                RenewLeaseAsync,
                leaseDuration,
                cancellationToken);

            stage = ZaloSchedulerStage.Finalize;
            if (!await RenewLeaseAsync(cancellationToken))
                throw new ZaloSchedulerLeaseLostException();

            var completedAt = DateTimeOffset.UtcNow;
            var degraded = HasStageFailures(
                result.FailedCount,
                rescue.FailedCount,
                handoff.FailedCount);
            if (degraded)
            {
                var failureCode = ZaloSchedulerFailureCodes.BuildDegraded(
                    result.FailedCount,
                    rescue.FailedCount,
                    handoff.FailedCount);
                await lease.MarkFailureAsync(instanceId, completedAt, failureCode, cancellationToken);
                logger.LogWarning(
                    "Zalo scheduler cycle completed degraded FailureCode={FailureCode} ReminderFailed={ReminderFailed} RescueFailed={RescueFailed} LifecycleFailed={LifecycleFailed}",
                    failureCode,
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
            var failureCode = ZaloSchedulerFailureCodes.ForException(stage);
            await lease.MarkFailureAsync(instanceId, failedAt, failureCode, CancellationToken.None);
            await lease.ReleaseAsync(instanceId, failedAt, CancellationToken.None);
            logger.LogError(exception, "Triggered Zalo scheduler cycle failed FailureCode={FailureCode}", failureCode);
        }

        async Task<bool> RenewLeaseAsync(CancellationToken renewCancellationToken)
        {
            await using var renewalScope = scopeFactory.CreateAsyncScope();
            var renewalDb = renewalScope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
            return await new ZaloSchedulerLeaseStore(renewalDb)
                .TryAcquireAsync(instanceId, DateTimeOffset.UtcNow, leaseDuration, renewCancellationToken);
        }
    }

    private static void ObserveDetachedRenewalTask(Task renewalTask)
    {
        _ = renewalTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task ObserveAfterLeaseCancellationAsync(Task operationTask)
    {
        try
        {
            await operationTask;
        }
        catch
        {
        }
    }
}
