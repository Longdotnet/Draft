using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloPollEventQueue
{
    private readonly Channel<ZaloPollBoardEvent> channel = Channel.CreateBounded<ZaloPollBoardEvent>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public bool TryEnqueue(ZaloPollBoardEvent incoming) => channel.Writer.TryWrite(incoming);
    public IAsyncEnumerable<ZaloPollBoardEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class ZaloPollEventWorker(
    ZaloPollEventQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ZaloPollEventWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> lastProcessed = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var incoming in queue.ReadAllAsync(stoppingToken))
        {
            if (!string.Equals(incoming.EventType, "update_board", StringComparison.OrdinalIgnoreCase)) continue;
            var accountId = NormalizeId(incoming.AccountId);
            var groupId = NormalizeId(incoming.GroupId);
            if (accountId.Length == 0 || groupId.Length == 0) continue;
            var key = $"{accountId}:{groupId}";
            var now = DateTimeOffset.UtcNow;
            if (lastProcessed.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromSeconds(2)) continue;
            lastProcessed[key] = now;
            foreach (var stale in lastProcessed.Where(item => now - item.Value > TimeSpan.FromHours(1)).Select(item => item.Key).ToList())
                lastProcessed.Remove(stale);

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<VolleyDraftDbContext>();
                var integration = scope.ServiceProvider.GetRequiredService<ZaloIntegrationService>();
                var overbook = scope.ServiceProvider.GetRequiredService<ZaloOverbookService>();
                var waitlist = scope.ServiceProvider.GetRequiredService<SessionWaitlistService>();
                var activityBackfill = scope.ServiceProvider.GetRequiredService<ZaloActivityBackfillCoordinator>();
                var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                var bridgeClient = scope.ServiceProvider.GetRequiredService<ZaloBridgeClient>();
                var domainEventShadow = new ZaloDomainEventShadowObserver(db);
                var domainNarrator = new ZaloDomainEventNarrator(configuration, bridgeClient);
                var linkedConnectionId = await db.MatchSessions
                    .AsNoTracking()
                    .Where(session =>
                        session.BotEnabled &&
                        session.ZaloGroupId == groupId &&
                        session.ZaloConnection != null &&
                        session.ZaloConnection.AccountZaloId == accountId)
                    .Select(session => session.ZaloConnectionId)
                    .FirstOrDefaultAsync(stoppingToken);
                if (!string.IsNullOrWhiteSpace(linkedConnectionId))
                    await activityBackfill.QueueGroupAsync(
                        linkedConnectionId,
                        groupId,
                        false,
                        stoppingToken);
                var sessions = await db.MatchSessions.AsNoTracking()
                    .Where(session => session.BotEnabled && session.ZaloGroupId == groupId &&
                                      session.ZaloConnection != null && session.ZaloConnection.AccountZaloId == accountId &&
                                      session.Status != SessionStatus.Cancelled &&
                                      session.Status != SessionStatus.Drafting && session.Status != SessionStatus.Finished &&
                                      session.PollImports.Any())
                    .Select(session => new { session.Id, session.Name, session.AdminUserId })
                    .ToListAsync(stoppingToken);
                foreach (var session in sessions)
                {
                    var before = await domainEventShadow.CaptureAsync(session.Id, stoppingToken);
                    var result = await integration.SyncLatestPollAsync(session.AdminUserId, session.Id);
                    if (!result.IsSuccess)
                    {
                        logger.LogDebug("Poll event sync skipped Session={SessionId}: {Reason}", session.Id, result.Error);
                        continue;
                    }
                    if (before is not null)
                    {
                        try
                        {
                            var decision = await domainEventShadow.ObserveAfterPollSyncAsync(
                                before,
                                incoming.ActorId,
                                incoming.BoardId,
                                incoming.OccurredAtUnixMs,
                                stoppingToken);
                            if (decision is not null)
                            {
                                var narration = await domainNarrator.HandleAsync(
                                    accountId,
                                    groupId,
                                    session.Id,
                                    session.Name,
                                    decision,
                                    stoppingToken);
                                if (narration.Eligible && !narration.Sent)
                                    logger.LogDebug(
                                        "Domain event narration suppressed Session={SessionId} Event={EventKind} Reason={Reason}",
                                        session.Id,
                                        decision.EventKind,
                                        narration.Reason);
                            }
                        }
                        catch (Exception exception)
                        {
                            logger.LogDebug(exception, "Domain event shadow/narration skipped Session={SessionId}", session.Id);
                        }
                    }
                    try
                    {
                        await overbook.ObserveAsync(session.Id, incoming.ActorId, stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogDebug(exception, "Overbook observation skipped Session={SessionId}", session.Id);
                    }
                    await waitlist.ProcessVacanciesAsync(session.Id, stoppingToken);
                }
                if (sessions.Count > 0)
                    logger.LogInformation("Processed Zalo poll board event Account={AccountId} Group={GroupId} Sessions={Count}", accountId, groupId, sessions.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not process Zalo poll board event Account={AccountId} Group={GroupId}", accountId, groupId);
            }
        }
    }

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }
}
