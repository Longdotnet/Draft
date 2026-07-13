using System.Threading.Channels;

namespace VolleyDraft.Api.Services;

public sealed class ZaloSchedulerTrigger
{
    private readonly Channel<byte> channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public bool TryTrigger() => channel.Writer.TryWrite(1);

    internal ValueTask<byte> WaitAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAsync(cancellationToken);

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
    ILogger<ZaloSchedulerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await trigger.WaitAsync(stoppingToken);
            trigger.Drain();
            await RunCycleAsync(stoppingToken);
        }
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
            logger.LogInformation(
                "Triggered Zalo scheduler completed Groups={Groups} Sent={Sent} Failed={Failed}",
                result.GroupCount,
                result.SentCount,
                result.FailedCount);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Triggered Zalo scheduler cycle failed");
        }
    }
}
