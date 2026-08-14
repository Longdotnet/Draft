using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed class ZaloOverbookWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ZaloOverbookWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        var nextV2RetentionAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<ZaloOverbookService>()
                    .ProcessDueAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Overbook reminder cycle sent {SentCount} message(s)", sent);

                var now = DateTimeOffset.UtcNow;
                if (now >= nextV2RetentionAt)
                {
                    var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
                    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    var policy = ZaloRetentionPolicy.FromConfiguration(configuration);
                    var cleanup = await new ZaloV2RetentionService(db)
                        .CleanupAsync(policy, now, stoppingToken);
                    if (cleanup.DeletedTraces + cleanup.DeletedMessageRelations + cleanup.DeletedUserConcepts > 0)
                    {
                        logger.LogInformation(
                            "Zalo V2 retention cleanup deleted traces={TraceCount}, relations={RelationCount}, concepts={ConceptCount}",
                            cleanup.DeletedTraces,
                            cleanup.DeletedMessageRelations,
                            cleanup.DeletedUserConcepts);
                    }
                    nextV2RetentionAt = now.AddHours(6);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Overbook reminder/V2 retention cycle failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
