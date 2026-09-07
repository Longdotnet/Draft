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
    DateTimeOffset? LastFailureAt);

internal static class ZaloSchedulerHealth
{
    private const double MaximumConfiguredStaleMinutes = 24 * 60;

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
                null);
        }

        var latestTerminal = Max(snapshot.LastSuccessAt, snapshot.LastFailureAt);
        var activeAttempt = snapshot.LastAttemptAt is not null
            && (latestTerminal is null || snapshot.LastAttemptAt > latestTerminal)
            && snapshot.LeaseUntil > now;
        if (activeAttempt)
            return Create(ZaloSchedulerHealthState.Running, true);

        if (snapshot.LastSuccessAt is null)
        {
            return Create(
                snapshot.LastFailureAt is null
                    ? ZaloSchedulerHealthState.NeverSucceeded
                    : ZaloSchedulerHealthState.Failed,
                false);
        }

        if (snapshot.LastFailureAt > snapshot.LastSuccessAt)
            return Create(ZaloSchedulerHealthState.Failed, false);

        if (now - snapshot.LastSuccessAt.Value > staleAfter)
            return Create(ZaloSchedulerHealthState.Stale, false);

        return Create(ZaloSchedulerHealthState.Healthy, true);

        ZaloSchedulerHealthAssessment Create(ZaloSchedulerHealthState state, bool isHealthy) =>
            new(
                state,
                isHealthy,
                now,
                staleAfter,
                snapshot.LeaseUntil,
                snapshot.LastAttemptAt,
                snapshot.LastSuccessAt,
                snapshot.LastFailureAt);
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
                        lastFailureAt = assessment.LastFailureAt
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
