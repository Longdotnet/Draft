using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloListenerCoordinator(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloCredentialProtector credentialProtector,
    IConfiguration configuration,
    ILogger<ZaloListenerCoordinator> logger)
{
    public async Task EnsureAllAsync(CancellationToken cancellationToken = default)
    {
        var accountIds = await db.ZaloConnections
            .AsNoTracking()
            .Select(connection => connection.AccountZaloId)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var accountId in accountIds)
        {
            await EnsureAccountAsync(accountId, cancellationToken);
        }
    }

    public async Task<bool> EnsureConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var accountId = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.Id == connectionId)
            .Select(item => item.AccountZaloId)
            .SingleOrDefaultAsync(cancellationToken);
        if (accountId is null) return false;
        return await EnsureAccountAsync(accountId, cancellationToken);
    }

    private async Task<bool> EnsureAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        var connections = await db.ZaloConnections
            .AsNoTracking()
            .Where(item => item.AccountZaloId == accountId && item.Status == ZaloConnectionStatus.Connected)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);
        var connection = connections.FirstOrDefault();
        if (connection is null)
        {
            await bridge.StopListenerAsync(accountId);
            return false;
        }
        var connectionIds = connections.Select(item => item.Id).ToList();
        var groupIds = await ResolveListenerGroupIdsAsync(db, connectionIds, cancellationToken);
        try
        {
            if (groupIds.Count == 0)
            {
                await bridge.StopListenerAsync(connection.AccountZaloId);
                return true;
            }

            using var document = JsonDocument.Parse(credentialProtector.Unprotect(connection.EncryptedCredentials));
            var credentials = document.RootElement.Clone();
            var webhookUrl = configuration["Zalo:WebhookUrl"]
                ?? "http://localhost:5030/api/internal/zalo/events";
            var webhookKey = configuration["Zalo:WebhookKey"]
                ?? configuration["Zalo:BridgeInternalKey"]
                ?? "development-zalo-bridge-key";
            await StartListenerWithRetryAsync(
                connection.AccountZaloId,
                credentials,
                groupIds,
                webhookUrl,
                webhookKey,
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(exception, "Could not reconcile Zalo listener for account {AccountId}", accountId);
            return false;
        }
    }

    /// <summary>
    /// Listener subscriptions follow durable tracked-group ownership, not the current
    /// MatchSession or Auto Session enable flag. Feature-specific processors still
    /// decide whether they act on an event; the listener's job is to preserve realtime
    /// delivery for configured group analytics and conversations. The proactive target
    /// resolver also supplies the legacy bot-enabled-session fallback for pre-seed data.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ResolveListenerGroupIdsAsync(
        VolleyDraftDbContext db,
        IReadOnlyList<string> connectionIds,
        CancellationToken cancellationToken = default)
    {
        var allowedConnections = connectionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (allowedConnections.Count == 0) return [];

        var targets = await new ZaloProactiveTargetResolver(db).GetTargetsAsync(cancellationToken);
        return targets
            .Where(target => allowedConnections.Contains(target.ConnectionId))
            .Select(target => target.GroupId)
            .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<BridgeListenerResponse> StartListenerWithRetryAsync(
        string accountId,
        JsonElement credentials,
        IReadOnlyList<string> groupIds,
        string webhookUrl,
        string webhookKey,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await bridge.StartListenerAsync(
                    accountId,
                    credentials,
                    groupIds,
                    webhookUrl,
                    webhookKey);
            }
            catch (Exception exception) when (IsTransientBridgeFailure(exception) && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(attempt * 5);
                logger.LogWarning(
                    exception,
                    "ZaloBridge listener start attempt {Attempt}/{MaxAttempts} failed for account {AccountId}; retrying in {DelaySeconds}s",
                    attempt,
                    maxAttempts,
                    accountId,
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("ZaloBridge listener start failed after all retry attempts.");
    }

    private static bool IsTransientBridgeFailure(Exception exception) => exception switch
    {
        TaskCanceledException => true,
        JsonException => true,
        HttpRequestException http => http.StatusCode is null ||
                                      (int)http.StatusCode >= 500 ||
                                      (int)http.StatusCode is 408 or 429,
        _ => false
    };
}

internal static class ZaloListenerWorkerCadence
{
    internal static TimeSpan ResolveInterval(
        IConfiguration configuration,
        string key,
        int defaultSeconds,
        int minSeconds,
        int maxSeconds)
    {
        var seconds = configuration.GetValue(key, defaultSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, minSeconds, maxSeconds));
    }

    internal static bool IsDue(DateTimeOffset now, DateTimeOffset nextAt) => now >= nextAt;

    internal static DateTimeOffset Next(DateTimeOffset now, TimeSpan interval) => now.Add(interval);
}

public sealed class ZaloListenerWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ZaloListenerWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep the short loop for timing-sensitive deterministic jobs, but do not let it
        // imply provider polling. Realtime Zalo board/message events are the primary path;
        // these slower cadences are only control-plane and missed-event safety nets.
        var loopInterval = ZaloListenerWorkerCadence.ResolveInterval(
            configuration,
            "Zalo:WorkerLoopSeconds",
            defaultSeconds: 45,
            minSeconds: 30,
            maxSeconds: 300);
        var listenerReconcileInterval = ZaloListenerWorkerCadence.ResolveInterval(
            configuration,
            "Zalo:ListenerReconcileSeconds",
            defaultSeconds: 300,
            minSeconds: 120,
            maxSeconds: 1800);
        var pollSafetyReconcileInterval = ZaloListenerWorkerCadence.ResolveInterval(
            configuration,
            "AutoSession:SafetyReconcileSeconds",
            defaultSeconds: 900,
            minSeconds: 300,
            maxSeconds: 3600);

        var nextListenerReconcileAt = DateTimeOffset.MinValue;
        var nextPollSafetyReconcileAt = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Daily greetings/social presence are timing-sensitive. Keep their failure
            // boundary independent from Auto Session reconciliation so a poll/parser/
            // discovery regression cannot make Morning/Night silently miss its window.
            try
            {
                await using var socialScope = scopeFactory.CreateAsyncScope();
                await ZaloSocialPresenceService.Create(socialScope.ServiceProvider)
                    .ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Zalo social-presence reconciliation failed");
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var autoSessions = ZaloAutoSessionV2Service.Create(scope.ServiceProvider);
                await autoSessions.EnsureAsync(stoppingToken);

                var now = DateTimeOffset.UtcNow;
                if (ZaloListenerWorkerCadence.IsDue(now, nextListenerReconcileAt))
                {
                    // Bridge restart/cold-start recovery. The active websocket listener is
                    // otherwise left alone instead of re-ensuring it every short worker tick.
                    nextListenerReconcileAt = ZaloListenerWorkerCadence.Next(now, listenerReconcileInterval);
                    await scope.ServiceProvider.GetRequiredService<ZaloListenerCoordinator>()
                        .EnsureAllAsync(stoppingToken);
                }

                if (ZaloListenerWorkerCadence.IsDue(now, nextPollSafetyReconcileAt))
                {
                    // Full board listing is deliberately a sparse missed-event safety net.
                    // update_board websocket events are handled immediately by
                    // ObservePollBoardEventAsync and do not wait for this interval.
                    nextPollSafetyReconcileAt = ZaloListenerWorkerCadence.Next(now, pollSafetyReconcileInterval);
                    await autoSessions.ReconcileAsync(stoppingToken);
                }

                // These jobs are already state/due-time gated before they touch Zalo and
                // may keep the short loop for reminders and lead-window correctness.
                await ZaloUpcomingMatchDiscoveryService.Create(scope.ServiceProvider)
                    .RunAsync(stoppingToken);
                await ZaloAutoSessionConversationService.Create(scope.ServiceProvider)
                    .ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Zalo listener/auto-session reconciliation failed");
            }

            await Task.Delay(loopInterval, stoppingToken);
        }
    }
}
