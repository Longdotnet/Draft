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

internal sealed class ZaloSchedulerLeaseStore(
    VolleyDraftDbContext db,
    bool useDatabaseAuthorityClock = false)
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
                "AuthorityLeaseUntil" TEXT NULL,
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

        // Existing deployments predate the authority deadline. Keep the observable LeaseUntil
        // contract intact and add a separate shared-clock fence used only for distributed authority.
        var authorityColumnCount = await db.Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS "Value"
                FROM pragma_table_info('ZaloSchedulerLeases')
                WHERE "name" = 'AuthorityLeaseUntil'
                """)
            .SingleAsync(cancellationToken);
        if (authorityColumnCount == 0)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"ZaloSchedulerLeases\" ADD COLUMN \"AuthorityLeaseUntil\" TEXT NULL;",
                    cancellationToken);
            }
            catch
            {
                // Two API instances may race the additive compatibility migration. Only suppress
                // the loser when another instance really did create the same column.
                authorityColumnCount = await db.Database.SqlQueryRaw<int>(
                        """
                        SELECT COUNT(*) AS "Value"
                        FROM pragma_table_info('ZaloSchedulerLeases')
                        WHERE "name" = 'AuthorityLeaseUntil'
                        """)
                    .SingleAsync(cancellationToken);
                if (authorityColumnCount == 0)
                    throw;
            }
        }

        // Legacy LeaseUntil/LastAttemptAt were written from each API instance's wall clock. Their
        // absolute values are therefore not safe authority after an upgrade. Preserve only the
        // relative lease window (which survives a constant clock offset), clamp it to the product's
        // supported 2..60 minute lease range, and anchor the migrated authority to SQLite time.
        // Missing/malformed legacy attempt evidence gets the conservative 60 minute handoff window.
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "ZaloSchedulerLeases"
            SET "AuthorityLeaseUntil" = CASE
                WHEN "OwnerId" LIKE 'released:%'
                    THEN strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now')
                ELSE strftime(
                    '%Y-%m-%dT%H:%M:%f0000+00:00',
                    'now',
                    printf(
                        '+%f seconds',
                        CASE
                            WHEN "LastAttemptAt" IS NULL
                              OR julianday("LastAttemptAt") IS NULL
                              OR julianday("LeaseUntil") IS NULL
                                THEN 3600.0
                            WHEN (julianday("LeaseUntil") - julianday("LastAttemptAt")) * 86400.0 < 120.0
                                THEN 120.0
                            WHEN (julianday("LeaseUntil") - julianday("LastAttemptAt")) * 86400.0 > 3600.0
                                THEN 3600.0
                            ELSE (julianday("LeaseUntil") - julianday("LastAttemptAt")) * 86400.0
                        END))
            END
            WHERE "AuthorityLeaseUntil" IS NULL;
            """,
            cancellationToken);

        // Rolling upgrades can briefly run a pre-authority worker beside a new worker. Old code
        // changes OwnerId/LeaseUntil without touching AuthorityLeaseUntil. Fence an old fast-clock
        // takeover while the DB-clock authority is live, then translate any permitted legacy
        // acquire/renew into a fresh DB-clock authority window. New code changes AuthorityLeaseUntil
        // in the same statement, so these compatibility triggers stay out of its path.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER IF NOT EXISTS "ZaloSchedulerLegacyTakeoverFence"
            BEFORE UPDATE OF "OwnerId", "LeaseUntil" ON "ZaloSchedulerLeases"
            FOR EACH ROW
            WHEN NEW."OwnerId" <> OLD."OwnerId"
              AND NEW."AuthorityLeaseUntil" = OLD."AuthorityLeaseUntil"
              AND OLD."OwnerId" NOT LIKE 'released:%'
              AND OLD."AuthorityLeaseUntil" > strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now')
            BEGIN
                SELECT RAISE(IGNORE);
            END;

            CREATE TRIGGER IF NOT EXISTS "ZaloSchedulerLegacyAuthorityBridge"
            AFTER UPDATE OF "OwnerId", "LeaseUntil" ON "ZaloSchedulerLeases"
            FOR EACH ROW
            WHEN NEW."OwnerId" NOT LIKE 'released:%'
              AND NEW."AuthorityLeaseUntil" = OLD."AuthorityLeaseUntil"
              AND (NEW."OwnerId" <> OLD."OwnerId" OR NEW."LeaseUntil" <> OLD."LeaseUntil")
            BEGIN
                UPDATE "ZaloSchedulerLeases"
                SET "AuthorityLeaseUntil" = strftime(
                    '%Y-%m-%dT%H:%M:%f0000+00:00',
                    'now',
                    printf(
                        '+%f seconds',
                        CASE
                            WHEN NEW."LastAttemptAt" IS NULL
                              OR julianday(NEW."LastAttemptAt") IS NULL
                              OR julianday(NEW."LeaseUntil") IS NULL
                                THEN 3600.0
                            WHEN (julianday(NEW."LeaseUntil") - julianday(NEW."LastAttemptAt")) * 86400.0 < 120.0
                                THEN 120.0
                            WHEN (julianday(NEW."LeaseUntil") - julianday(NEW."LastAttemptAt")) * 86400.0 > 3600.0
                                THEN 3600.0
                            ELSE (julianday(NEW."LeaseUntil") - julianday(NEW."LastAttemptAt")) * 86400.0
                        END))
                WHERE "Name" = NEW."Name"
                  AND "OwnerId" = NEW."OwnerId"
                  AND "AuthorityLeaseUntil" = OLD."AuthorityLeaseUntil";
            END;
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

        var authorityNow = await ResolveAuthorityNowAsync(now, cancellationToken);
        var nowText = now.ToUniversalTime().ToString("O");
        var leaseUntilText = now.Add(leaseDuration).ToUniversalTime().ToString("O");
        var authorityNowText = authorityNow.ToUniversalTime().ToString("O");
        var authorityLeaseUntilText = authorityNow.Add(leaseDuration).ToUniversalTime().ToString("O");
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloSchedulerLeases" ("Name", "OwnerId", "LeaseUntil", "AuthorityLeaseUntil", "LastAttemptAt")
            VALUES ({{LeaseName}}, {{ownerId}}, {{leaseUntilText}}, {{authorityLeaseUntilText}}, {{nowText}})
            ON CONFLICT ("Name") DO UPDATE SET
                "OwnerId" = excluded."OwnerId",
                "LeaseUntil" = excluded."LeaseUntil",
                "AuthorityLeaseUntil" = excluded."AuthorityLeaseUntil",
                "LastAttemptAt" = CASE
                    WHEN "ZaloSchedulerLeases"."OwnerId" = excluded."OwnerId"
                        THEN "ZaloSchedulerLeases"."LastAttemptAt"
                    ELSE excluded."LastAttemptAt"
                END
            WHERE "ZaloSchedulerLeases"."OwnerId" = {{ownerId}}
               OR "ZaloSchedulerLeases"."OwnerId" LIKE 'released:%'
               OR "ZaloSchedulerLeases"."AuthorityLeaseUntil" <= {{authorityNowText}};
            """, cancellationToken);

        return affected > 0;
    }

    internal async Task<bool> TryRenewAsync(
        string ownerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        await EnsureAsync(cancellationToken);

        var authorityNow = await ResolveAuthorityNowAsync(now, cancellationToken);
        var leaseUntilText = now.Add(leaseDuration).ToUniversalTime().ToString("O");
        var authorityNowText = authorityNow.ToUniversalTime().ToString("O");
        var authorityLeaseUntilText = authorityNow.Add(leaseDuration).ToUniversalTime().ToString("O");
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LeaseUntil" = {{leaseUntilText}},
                "AuthorityLeaseUntil" = {{authorityLeaseUntilText}}
            WHERE "Name" = {{LeaseName}}
              AND "OwnerId" = {{ownerId}}
              AND "AuthorityLeaseUntil" > {{authorityNowText}};
            """, cancellationToken);

        return affected > 0;
    }

    internal async Task MarkAttemptAsync(
        string ownerId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var authorityAt = await ResolveAuthorityNowAsync(at, cancellationToken);
        var atText = at.ToUniversalTime().ToString("O");
        var authorityAtText = authorityAt.ToUniversalTime().ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LastAttemptAt" = {{atText}}
            WHERE "Name" = {{LeaseName}}
              AND "OwnerId" = {{ownerId}}
              AND "AuthorityLeaseUntil" > {{authorityAtText}};
            """, cancellationToken);
    }

    internal async Task MarkSuccessAsync(
        string ownerId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var authorityAt = await ResolveAuthorityNowAsync(at, cancellationToken);
        var atText = at.ToUniversalTime().ToString("O");
        var authorityAtText = authorityAt.ToUniversalTime().ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LastSuccessAt" = {{atText}}
            WHERE "Name" = {{LeaseName}}
              AND "OwnerId" = {{ownerId}}
              AND "AuthorityLeaseUntil" > {{authorityAtText}};
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
        var authorityAt = await ResolveAuthorityNowAsync(at, cancellationToken);
        var failureAt = at.ToUniversalTime().ToString("O");
        var authorityAtText = authorityAt.ToUniversalTime().ToString("O");
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "LastFailureAt" = {{failureAt}}
            WHERE "Name" = {{LeaseName}}
              AND "OwnerId" = {{ownerId}}
              AND "AuthorityLeaseUntil" > {{authorityAtText}};
            """, cancellationToken);

        // Failure diagnosis belongs to the same durable live lease owner as LastFailureAt.
        // Re-check the authority fence in the diagnostic statement too: ownership can change
        // after the timestamp update but before this second statement executes.
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
                  AND "AuthorityLeaseUntil" > {{authorityAtText}}
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
        var authorityAt = await ResolveAuthorityNowAsync(at, cancellationToken);
        var atText = at.ToUniversalTime().ToString("O");
        var authorityAtText = authorityAt.ToUniversalTime().ToString("O");
        var releasedOwnerId = $"released:{Guid.NewGuid():N}";
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ZaloSchedulerLeases"
            SET "OwnerId" = {{releasedOwnerId}},
                "LeaseUntil" = {{atText}},
                "AuthorityLeaseUntil" = {{authorityAtText}}
            WHERE "Name" = {{LeaseName}}
              AND "OwnerId" = {{ownerId}}
              AND "AuthorityLeaseUntil" > {{authorityAtText}};
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

    private async Task<DateTimeOffset> ResolveAuthorityNowAsync(
        DateTimeOffset callerNow,
        CancellationToken cancellationToken)
    {
        if (!useDatabaseAuthorityClock)
            return callerNow;

        var databaseNow = await db.Database.SqlQueryRaw<string>(
                """
                SELECT strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now') AS "Value"
                """)
            .SingleAsync(cancellationToken);
        return DateTimeOffset.Parse(databaseNow);
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
    private static readonly TimeSpan DefaultStageTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaximumStageTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LeaseContentionRetryDelay = TimeSpan.FromSeconds(5);
    private readonly string instanceId = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watchdogInterval = ResolveWatchdogInterval(configuration);
        var leaseDuration = ResolveLeaseDuration(configuration, watchdogInterval);
        var stageTimeout = ResolveStageTimeout(configuration);

        // Durable reminders, pass-slot rescue and Auto Session lifecycle handoff should catch up
        // whenever the API process becomes available, even if the external scheduler missed the
        // wake-up that originally should have driven them.
        await RunCycleAsync(leaseDuration, stageTimeout, retryOnLeaseContention: false, stoppingToken);

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

            await RunCycleAsync(leaseDuration, stageTimeout, retryOnLeaseContention: externalTrigger, stoppingToken);
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

    internal static TimeSpan ResolveStageTimeout(IConfiguration configuration)
    {
        var configuredSeconds = configuration.GetValue<double?>("Scheduler:StageTimeoutSeconds");
        if (configuredSeconds is > 0)
        {
            var configured = TimeSpan.FromSeconds(configuredSeconds.Value);
            return configured <= MaximumStageTimeout
                ? configured
                : MaximumStageTimeout;
        }

        return DefaultStageTimeout;
    }

    internal static TimeSpan ResolveLeaseRenewalInterval(TimeSpan leaseDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        return TimeSpan.FromTicks(Math.Max(1, leaseDuration.Ticks / 3));
    }

    internal static string CreateCycleOwnerId(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return $"{instanceId}:{Guid.NewGuid():N}";
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
        CancellationToken cancellationToken,
        TimeSpan? stageTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(renewLease);
        if (stageTimeout.HasValue)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stageTimeout.Value, TimeSpan.Zero);

        var renewalInterval = ResolveLeaseRenewalInterval(leaseDuration);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operationTask = operation(operationCancellation.Token);
        var timeoutTask = stageTimeout.HasValue
            ? Task.Delay(stageTimeout.Value, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        try
        {
            while (!operationTask.IsCompleted)
            {
                var heartbeatDelay = Task.Delay(renewalInterval, cancellationToken);
                var completed = await Task.WhenAny(operationTask, heartbeatDelay, timeoutTask);
                cancellationToken.ThrowIfCancellationRequested();
                if (completed == operationTask)
                    break;

                if (completed == timeoutTask)
                {
                    operationCancellation.Cancel();
                    await ObserveAfterLeaseCancellationAsync(operationTask);
                    throw new TimeoutException($"Zalo scheduler stage exceeded its {stageTimeout!.Value.TotalSeconds:0.###} second execution budget.");
                }

                bool renewed;
                try
                {
                    using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var renewalTask = renewLease(renewalCancellation.Token);
                    // Give every renewal attempt its own revocable authority. A stage timeout is a
                    // stronger execution-authority boundary than the heartbeat deadline, so it must
                    // also interrupt an in-flight renewal instead of waiting for that renewal window
                    // to elapse first.
                    var renewalCompleted = await Task.WhenAny(
                        renewalTask,
                        Task.Delay(renewalInterval, cancellationToken),
                        timeoutTask);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (renewalCompleted == timeoutTask)
                    {
                        renewalCancellation.Cancel();
                        operationCancellation.Cancel();
                        await ObserveAfterLeaseCancellationAsync(operationTask);
                        ObserveDetachedRenewalTask(renewalTask);
                        throw new TimeoutException($"Zalo scheduler stage exceeded its {stageTimeout!.Value.TotalSeconds:0.###} second execution budget.");
                    }

                    if (renewalCompleted != renewalTask)
                    {
                        renewalCancellation.Cancel();
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
            // A cycle/host cancellation owns the result, but the stage may still be unwinding
            // provider/domain cleanup with scoped services. Drain it before returning so the
            // owning scheduler scope cannot be disposed underneath active work.
            operationCancellation.Cancel();
            await ObserveAfterLeaseCancellationAsync(operationTask);
            throw;
        }
    }

    private async Task RunCycleAsync(
        TimeSpan leaseDuration,
        TimeSpan stageTimeout,
        bool retryOnLeaseContention,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
        var lease = new ZaloSchedulerLeaseStore(db, useDatabaseAuthorityClock: true);
        var acquiredAt = DateTimeOffset.UtcNow;
        var cycleOwnerId = CreateCycleOwnerId(instanceId);
        if (!await lease.TryAcquireAsync(cycleOwnerId, acquiredAt, leaseDuration, cancellationToken))
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

        // Lease acquisition is also the durable start-of-attempt boundary. Keeping those two
        // facts in one SQL statement prevents a successor owner from ever inheriting a dead
        // predecessor's unterminated attempt during the acquire -> MarkAttempt gap.
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
                cancellationToken,
                stageTimeout);

            stage = ZaloSchedulerStage.Reminder;
            var result = await RunWithLeaseHeartbeatAsync(
                stageToken => scope.ServiceProvider.GetRequiredService<ZaloReminderService>()
                    .SendDueRemindersAsync(stageToken),
                RenewLeaseAsync,
                leaseDuration,
                cancellationToken,
                stageTimeout);

            // Every potentially long provider/domain stage renews the lease from a separate scope.
            // This prevents a second API instance from taking ownership mid-stage merely because
            // the stage exceeded the lease duration.
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
                cancellationToken,
                stageTimeout);

            if (!await RenewLeaseAsync(cancellationToken))
                throw new ZaloSchedulerLeaseLostException();

            stage = ZaloSchedulerStage.Lifecycle;
            // Match creation commits before V5 lifecycle handoff. A transient failure in that
            // post-commit window must survive request loss/restart and be retried from durable
            // Created proposal + link state rather than depending on the original webhook.
            var handoffStore = new ZaloAutoSessionLifecycleHandoffStoreV5(db);
            var handoff = await RunWithLeaseHeartbeatAsync(
                stageToken => handoffStore.ReconcileMissingAsync(logger, stageToken),
                RenewLeaseAsync,
                leaseDuration,
                cancellationToken,
                stageTimeout);

            stage = ZaloSchedulerStage.Finalize;
            // Do not record or announce success if ownership changed in the narrow window after the
            // last stage. A successor is then responsible for the next authoritative cycle.
            if (!await RenewLeaseAsync(cancellationToken))
                throw new ZaloSchedulerLeaseLostException();

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
                var failureCode = ZaloSchedulerFailureCodes.BuildDegraded(
                    result.FailedCount,
                    rescue.FailedCount,
                    handoff.FailedCount);
                await lease.MarkFailureAsync(cycleOwnerId, completedAt, failureCode, cancellationToken);
                logger.LogWarning(
                    "Zalo scheduler cycle completed degraded FailureCode={FailureCode} ReminderFailed={ReminderFailed} RescueFailed={RescueFailed} LifecycleFailed={LifecycleFailed}",
                    failureCode,
                    result.FailedCount,
                    rescue.FailedCount,
                    handoff.FailedCount);
            }
            else
            {
                await lease.MarkSuccessAsync(cycleOwnerId, completedAt, cancellationToken);
            }

            await lease.ReleaseAsync(cycleOwnerId, completedAt, cancellationToken);
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
            await lease.MarkFailureAsync(cycleOwnerId, failedAt, failureCode, CancellationToken.None);
            await lease.ReleaseAsync(cycleOwnerId, failedAt, CancellationToken.None);
            logger.LogError(exception, "Triggered Zalo scheduler cycle failed FailureCode={FailureCode}", failureCode);
        }

        async Task<bool> RenewLeaseAsync(CancellationToken renewCancellationToken)
        {
            // Lease heartbeats must not share the stage DbContext: provider/domain work can be using
            // that context concurrently, and EF DbContext is not safe for concurrent operations.
            await using var renewalScope = scopeFactory.CreateAsyncScope();
            var renewalDb = renewalScope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
            return await new ZaloSchedulerLeaseStore(renewalDb, useDatabaseAuthorityClock: true)
                .TryRenewAsync(cycleOwnerId, DateTimeOffset.UtcNow, leaseDuration, renewCancellationToken);
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
            // The lease-loss/cancellation decision is authoritative here. The operation is awaited
            // only to avoid leaving scoped services running in the background after their owning cycle exits.
        }
    }
}
