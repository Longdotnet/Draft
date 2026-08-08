namespace VolleyDraft.Api.Services;

public sealed class ZaloOverbookWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ZaloOverbookWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<ZaloOverbookService>()
                    .ProcessDueAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Overbook reminder cycle sent {SentCount} message(s)", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Overbook reminder cycle failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
