using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloPollEventQueue
{
    private readonly record struct ScopeKey(string AccountId, string GroupId);

    private readonly Channel<ScopeKey> ready = Channel.CreateUnbounded<ScopeKey>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly ConcurrentDictionary<ScopeKey, ZaloPollBoardEvent> pending = new();
    private readonly ConcurrentDictionary<ScopeKey, byte> scheduled = new();

    public bool TryEnqueue(ZaloPollBoardEvent incoming)
    {
        var key = CreateScopeKey(incoming);
        if (key.AccountId.Length == 0 || key.GroupId.Length == 0) return false;

        pending.AddOrUpdate(
            key,
            incoming,
            (_, current) => IsNewer(incoming, current) ? incoming : current);

        if (!scheduled.TryAdd(key, 0)) return true;
        if (ready.Writer.TryWrite(key)) return true;

        scheduled.TryRemove(key, out _);
        pending.TryRemove(key, out _);
        return false;
    }

    public async IAsyncEnumerable<ZaloPollBoardEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var key in ready.Reader.ReadAllAsync(cancellationToken))
        {
            if (!pending.TryRemove(key, out var incoming))
            {
                scheduled.TryRemove(key, out _);
                ScheduleFollowUpIfDirty(key);
                continue;
            }

            // Release ownership before yielding so changes that arrive while the worker
            // processes this scope can schedule one follow-up turn. A producer that raced
            // with the pending removal but still saw this key as scheduled is recovered by
            // ScheduleFollowUpIfDirty below.
            scheduled.TryRemove(key, out _);
            ScheduleFollowUpIfDirty(key);
            yield return incoming;
        }
    }

    internal int PendingScopeCount => pending.Count;

    private void ScheduleFollowUpIfDirty(ScopeKey key)
    {
        if (!pending.ContainsKey(key) || !scheduled.TryAdd(key, 0)) return;
        if (ready.Writer.TryWrite(key)) return;
        scheduled.TryRemove(key, out _);
    }

    private static ScopeKey CreateScopeKey(ZaloPollBoardEvent incoming) =>
        new(NormalizeId(incoming.AccountId), NormalizeId(incoming.GroupId));

    private static bool IsNewer(ZaloPollBoardEvent candidate, ZaloPollBoardEvent current) =>
        candidate.OccurredAtUnixMs >= current.OccurredAtUnixMs;

    private static string NormalizeId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.EndsWith("_0", StringComparison.Ordinal) ? normalized[..^2] : normalized;
    }
}

public sealed class ZaloPollEventWorker(
    ZaloPollEventQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ZaloPollEventWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var incoming in queue.ReadAllAsync(stoppingToken))
        {
            if (!IsBoardChange(incoming.EventType)) continue;
            var accountId = NormalizeId(incoming.AccountId);
            var groupId = NormalizeId(incoming.GroupId);
            if (accountId.Length == 0 || groupId.Length == 0) continue;

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
                var activityTarget = await new ZaloProactiveTargetResolver(db)
                    .ResolveTargetAsync(accountId, groupId, stoppingToken);
                if (activityTarget is not null)
                    await activityBackfill.QueueGroupAsync(
                        activityTarget.ConnectionId,
                        activityTarget.GroupId,
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
