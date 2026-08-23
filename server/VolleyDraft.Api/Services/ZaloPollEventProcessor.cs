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
            if (!IsBoardChange(incoming.EventType)) continue;
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
                var activityBackfill = scope.ServiceProvider.GetRequiredService<ZaloActivityBackfillCoordinator>();
                var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                var bridgeClient = scope.ServiceProvider.GetRequiredService<ZaloBridgeClient>();

                try
                {
                    var discoveryEvent = NormalizeForAutoSession(incoming);
                    await ZaloAutoSessionV2Service.Create(scope.ServiceProvider)
                        .ObservePollBoardEventAsync(discoveryEvent, stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Auto-session organizer preview skipped Account={AccountId} Group={GroupId}",
                        accountId,
                        groupId);
                }

                var domainEventShadow = new ZaloDomainEventShadowObserver(db);
                var domainNarrator = new ZaloDomainEventNarrator(configuration, bridgeClient);
                var domainNarrationTelemetry = new ZaloDomainEventNarrationTelemetry(db);
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
                                await domainNarrationTelemetry.RecordAsync(
                                    groupId,
                                    session.Id,
                                    decision,
                                    narration,
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
                    // Intentionally no automatic waitlist vacancy processing here.
                    // When a voter leaves, the freed slot stays open on Zalo and whoever
                    // votes first takes it. This matches the group's real-world rule.
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

    internal static bool IsBoardChange(string? eventType) =>
        string.Equals(eventType, "update_board", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(eventType, "remove_board", StringComparison.OrdinalIgnoreCase);

    internal static ZaloPollBoardEvent NormalizeForAutoSession(ZaloPollBoardEvent incoming) =>
        string.Equals(incoming.EventType, "remove_board", StringComparison.OrdinalIgnoreCase)
            ? incoming with { EventType = "update_board", BoardId = null }
            : incoming;

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }
}