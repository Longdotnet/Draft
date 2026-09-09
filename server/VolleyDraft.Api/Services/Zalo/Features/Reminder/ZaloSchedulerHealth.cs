using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal enum ZaloSchedulerHealthState
{
    Healthy,
    Running,
    NeverSucceeded,
    Failed,
    Stale
}

internal sealed record ZaloSchedulerHealthAssessment(
    ZaloSchedulerHealthState State,
    bool IsHealthy,
    DateTimeOffset ObservedAt,
    TimeSpan StaleAfter,
    DateTimeOffset? LeaseUntil,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? FailureCode);

internal static class ZaloSchedulerHealth
{
    private const double MaximumConfiguredStaleMinutes = 24 * 60;
    internal const string AbandonedLeaseFailureCode = "abandoned:leaseexpired";

    internal static TimeSpan ResolveStaleAfter(IConfiguration configuration)
    {
        var configuredMinutes = configuration.GetValue<double?>("Scheduler:HealthStaleAfterMinutes");
        if (configuredMinutes is > 0 and <= MaximumConfiguredStaleMinutes)
            return TimeSpan.FromMinutes(configuredMinutes.Value);

        return TimeSpan.FromTicks(ZaloSchedulerWorker.ResolveWatchdogInterval(configuration).Ticks * 3);
    }

    internal static ZaloSchedulerHealthAssessment Evaluate(
        ZaloSchedulerLeaseSnapshot? snapshot,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);

        if (snapshot is null)
        {
            return new ZaloSchedulerHealthAssessment(
                ZaloSchedulerHealthState.NeverSucceeded,
                false,
                now,
                staleAfter,
                null,
                null,
                null,
                null,
                null);
        }

        var latestTerminal = Max(snapshot.LastSuccessAt, snapshot.LastFailureAt);
        var unterminatedAttempt = snapshot.LastAttemptAt is not null
            && (latestTerminal is null || snapshot.LastAttemptAt > latestTerminal);
        if (unterminatedAttempt)
        {
            // An attempt newer than every terminal marker is only healthy while its
            // distributed lease is still owned. Once that lease expires, the process
            // died or lost ownership before recording success/failure. Never fall back
            // to an older success and report this abandoned cycle as healthy.
            var running = snapshot.LeaseUntil > now;
            return Create(
                running ? ZaloSchedulerHealthState.Running : ZaloSchedulerHealthState.Failed,
                running,
                running ? null : AbandonedLeaseFailureCode);
        }

        if (snapshot.LastSuccessAt is null)
        {
            return Create(
                snapshot.LastFailureAt is null
                    ? ZaloSchedulerHealthState.NeverSucceeded
                    : ZaloSchedulerHealthState.Failed,
                false,
                snapshot.LastFailureAt is null ? null : snapshot.LastFailureCode);
        }

        if (snapshot.LastFailureAt > snapshot.LastSuccessAt)
            return Create(ZaloSchedulerHealthState.Failed, false, snapshot.LastFailureCode);

        if (now - snapshot.LastSuccessAt.Value > staleAfter)
            return Create(ZaloSchedulerHealthState.Stale, false, null);

        return Create(ZaloSchedulerHealthState.Healthy, true, null);

        ZaloSchedulerHealthAssessment Create(
            ZaloSchedulerHealthState state,
            bool isHealthy,
            string? failureCode) =>
            new(
                state,
                isHealthy,
                now,
                staleAfter,
                snapshot.LeaseUntil,
                snapshot.LastAttemptAt,
                snapshot.LastSuccessAt,
                snapshot.LastFailureAt,
                failureCode);
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;
        return left > right ? left : right;
    }
}

public static class ZaloSchedulerHealthEndpoint
{
    public static IEndpointConventionBuilder MapZaloSchedulerHealth(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/health/scheduler", async (
            VolleyDraftDbContext db,
            IConfiguration configuration,
            ILogger<ZaloSchedulerWorker> logger,
            CancellationToken cancellationToken) =>
        {
            var observedAt = DateTimeOffset.UtcNow;
            var staleAfter = ZaloSchedulerHealth.ResolveStaleAfter(configuration);

            try
            {
                var snapshot = await new ZaloSchedulerLeaseStore(db).GetAsync(cancellationToken);
                var assessment = ZaloSchedulerHealth.Evaluate(snapshot, observedAt, staleAfter);
                return Results.Json(
                    new
                    {
                        status = assessment.State.ToString().ToLowerInvariant(),
                        healthy = assessment.IsHealthy,
                        observedAt = assessment.ObservedAt,
                        staleAfterMinutes = assessment.StaleAfter.TotalMinutes,
                        leaseUntil = assessment.LeaseUntil,
                        lastAttemptAt = assessment.LastAttemptAt,
                        lastSuccessAt = assessment.LastSuccessAt,
                        lastFailureAt = assessment.LastFailureAt,
                        failureCode = assessment.FailureCode
                    },
                    statusCode: assessment.IsHealthy
                        ? StatusCodes.Status200OK
                        : StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unable to read durable Zalo scheduler health state");
                return Results.Json(
                    new
                    {
                        status = "unavailable",
                        healthy = false,
                        observedAt,
                        staleAfterMinutes = staleAfter.TotalMinutes
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
}
