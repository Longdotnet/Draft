using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloMemberIntegrationDeliverySettings(
    bool Enabled,
    int PresenceQuietMinutes,
    int QuietMinutes,
    int PassiveQuietMinutes,
    int RecentActivityMinutes,
    int TargetPerMemberPerDay,
    int MinMemberIntervalMinutes,
    int MinGroupIntervalMinutes,
    int MaxPerDay,
    int StartHour,
    int EndHour)
{
    public static ZaloMemberIntegrationDeliverySettings FromConfiguration(IConfiguration configuration) => new(
        Enabled: configuration.GetValue("ZaloBot:MemberIntegration:Delivery:Enabled", true),
        PresenceQuietMinutes: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:PresenceQuietMinutes", 10), 5, 120),
        QuietMinutes: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:QuietMinutes", 20), 5, 240),
        PassiveQuietMinutes: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:PassiveQuietMinutes", 90), 20, 720),
        RecentActivityMinutes: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:RecentActivityMinutes", 360), 30, 1440),
        TargetPerMemberPerDay: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:TargetPerMemberPerDay", 2), 1, 4),
        MinMemberIntervalMinutes: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:MinMemberIntervalMinutes", 180), 30, 720),
        MinGroupIntervalMinutes: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:MinGroupIntervalMinutes", 20), 15, 720),
        MaxPerDay: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:MaxPerDay", 40), 1, 100),
        StartHour: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:StartHour", 9), 0, 23),
        EndHour: Math.Clamp(configuration.GetValue("ZaloBot:MemberIntegration:Delivery:EndHour", 22), 1, 24));
}

internal sealed record ZaloMemberIntegrationActivityHint(
    string ZaloUserId,
    DateTimeOffset? LastActivityAt,
    string? Source);

internal static class ZaloMemberIntegrationDeliveryPolicy
{
    public static bool IsInsideWindow(int hour, ZaloMemberIntegrationDeliverySettings settings) =>
        settings.EndHour == 24
            ? hour >= settings.StartHour
            : settings.StartHour <= settings.EndHour
                ? hour >= settings.StartHour && hour < settings.EndHour
                : hour >= settings.StartHour || hour < settings.EndHour;

    public static int RequiredQuietMinutes(
        DateTimeOffset now,
        ZaloMemberIntegrationActivityHint activity,
        ZaloMemberIntegrationDeliverySettings settings)
    {
        var recent = activity.LastActivityAt is { } at &&
                     now - at <= TimeSpan.FromMinutes(settings.RecentActivityMinutes);
        if (!recent) return settings.PassiveQuietMinutes;
        return string.Equals(activity.Source, "seen", StringComparison.Ordinal)
            ? settings.PresenceQuietMinutes
            : settings.QuietMinutes;
    }

    public static bool IsMemberDue(
        DateTimeOffset now,
        int sentToday,
        DateTimeOffset? lastSentAt,
        ZaloMemberIntegrationDeliverySettings settings)
    {
        if (sentToday >= settings.TargetPerMemberPerDay) return false;
        return lastSentAt is null ||
               now - lastSentAt >= TimeSpan.FromMinutes(settings.MinMemberIntervalMinutes);
    }
}

public sealed class ZaloMemberIntegrationNudgeService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    IConfiguration configuration,
    ILogger<ZaloMemberIntegrationNudgeService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private readonly ZaloMemberIntegrationDeliverySettings settings =
        ZaloMemberIntegrationDeliverySettings.FromConfiguration(configuration);
    private readonly ZaloProactiveMessageStore proactiveStore = new(db);

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled) return 0;
        await proactiveStore.EnsureAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var localNow = now.ToOffset(VietnamOffset);
        if (!ZaloMemberIntegrationDeliveryPolicy.IsInsideWindow(localNow.Hour, settings)) return 0;

        var targets = (await new ZaloProactiveTargetResolver(db).GetTargetsAsync(cancellationToken))
            .Take(100)
            .ToList();
        var sent = 0;

        foreach (var target in targets)
        {
            var connectionId = CleanId(target.ConnectionId);
            var groupId = CleanId(target.GroupId);
            var accountId = CleanId(target.AccountId);
            if (connectionId.Length == 0 || groupId.Length == 0 || accountId.Length == 0) continue;

            var contextBuilder = new ZaloMemberIntegrationContextBuilder(db, bridge, configuration);
            await contextBuilder.EnsureConfiguredRecoveriesAsync(connectionId, groupId, cancellationToken);

            var history = await proactiveStore.GetHistoryAsync(connectionId, groupId, 800, cancellationToken);
            var laneHistory = history
                .Where(item => string.Equals(item.Lane, ZaloProactiveLane.MemberIntegration, StringComparison.Ordinal))
                .OrderByDescending(item => item.SentAt)
                .ToList();
            if (laneHistory.Count(item => item.SentAt.ToOffset(VietnamOffset).Date == localNow.Date) >= settings.MaxPerDay)
                continue;
            if (laneHistory.FirstOrDefault() is { } lastLane &&
                now - lastLane.SentAt < TimeSpan.FromMinutes(settings.MinGroupIntervalMinutes))
                continue;

            var selections = await new ZaloMemberIntegrationService(db, bridge, configuration)
                .BuildAsync(connectionId, groupId, history, now, cancellationToken);
            if (selections.Count == 0) continue;

            var memberHistoryToday = history
                .Where(item =>
                    (item.Lane == ZaloProactiveLane.MemberIntegration ||
                     item.Lane == ZaloProactiveLane.Community) &&
                    item.Kind.StartsWith("member_integration_", StringComparison.Ordinal) &&
                    item.SubjectUserId is not null &&
                    item.SentAt.ToOffset(VietnamOffset).Date == localNow.Date)
                .GroupBy(item => item.SubjectUserId!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => new DailyMemberDelivery(
                        group.Count(),
                        group.Max(item => item.SentAt)),
                    StringComparer.Ordinal);

            var recentMessages = await LoadRecentHumanMessagesAsync(connectionId, groupId, now, cancellationToken);
            var lastHumanMessageAt = recentMessages.Count == 0
                ? (DateTimeOffset?)null
                : recentMessages.Max(item => item.SentAt);

            var activityHints = await BuildActivityHintsAsync(
                connectionId,
                groupId,
                selections,
                recentMessages,
                now,
                cancellationToken);
            var ordered = selections
                .Where(selection =>
                {
                    memberHistoryToday.TryGetValue(selection.Context.ZaloUserId, out var delivery);
                    return ZaloMemberIntegrationDeliveryPolicy.IsMemberDue(
                        now,
                        delivery?.Count ?? 0,
                        delivery?.LastSentAt,
                        settings);
                })
                .Select(selection => new
                {
                    Selection = selection,
                    DailyDeliveryCount = memberHistoryToday.TryGetValue(selection.Context.ZaloUserId, out var delivery)
                        ? delivery.Count
                        : 0,
                    Activity = activityHints.GetValueOrDefault(
                        selection.Context.ZaloUserId,
                        new ZaloMemberIntegrationActivityHint(selection.Context.ZaloUserId, null, null))
                })
                .OrderBy(item => item.DailyDeliveryCount)
                .ThenByDescending(item =>
                    item.Activity.LastActivityAt is { } activityAt &&
                    now - activityAt <= TimeSpan.FromMinutes(settings.RecentActivityMinutes))
                .ThenByDescending(item => item.Activity.LastActivityAt)
                .ThenByDescending(item => item.Selection.Suggestion.Priority)
                .ThenBy(item => item.Selection.Context.IntegrationStartedAt ?? item.Selection.Context.JoinedAt)
                .ToList();

            var chosen = ordered.FirstOrDefault(item => IsQuietEnough(
                now,
                lastHumanMessageAt,
                item.Activity));
            if (chosen is null) continue;

            var suggestion = chosen.Selection.Suggestion;
            var context = chosen.Selection.Context;
            var deliverySlot = chosen.DailyDeliveryCount + 1;
            var idempotencyKey = $"member-integration:{connectionId}:{groupId}:{context.ZaloUserId}:{suggestion.ContentKey}:{localNow:yyyyMMdd}:{deliverySlot}";
            if (!await proactiveStore.TryAcquireLeaseAsync(
                    connectionId,
                    groupId,
                    idempotencyKey,
                    now,
                    LeaseDuration,
                    cancellationToken))
                continue;

            var accepted = false;
            try
            {
                var response = await bridge.SendGroupMessageAsync(
                    accountId,
                    groupId,
                    suggestion.Text,
                    ZaloCommunityNudgeService.BuildExplicitMentions(suggestion),
                    idempotencyKey: idempotencyKey);
                if (!response.Sent)
                {
                    await proactiveStore.ReleaseLeaseAsync(connectionId, groupId, idempotencyKey, cancellationToken);
                    continue;
                }

                accepted = true;
                await proactiveStore.RecordAsync(new ZaloProactiveMessageHistoryData(
                    Guid.NewGuid().ToString("n"),
                    connectionId,
                    groupId,
                    localNow.ToString("yyyy-MM-dd"),
                    ZaloProactiveLane.MemberIntegration,
                    $"member_integration_{suggestion.CapabilityKey}",
                    suggestion.ContentKey,
                    context.ZaloUserId,
                    context.DisplayName,
                    suggestion.Text,
                    now,
                    NormalizeProviderMessageId(response.MessageId),
                    idempotencyKey), cancellationToken);
                await proactiveStore.CommitCooldownAsync(
                    connectionId,
                    groupId,
                    idempotencyKey,
                    now.AddMinutes(20),
                    cancellationToken);
                sent += 1;
            }
            catch (Exception exception)
            {
                if (!accepted)
                {
                    try
                    {
                        await proactiveStore.ReleaseLeaseAsync(connectionId, groupId, idempotencyKey, cancellationToken);
                    }
                    catch (Exception releaseException) when (releaseException is not OperationCanceledException)
                    {
                        logger.LogWarning(releaseException,
                            "Could not release MemberIntegration lease Group={GroupId} User={UserId}",
                            groupId,
                            context.ZaloUserId);
                    }
                }
                logger.LogWarning(exception,
                    "Could not send MemberIntegration nudge Group={GroupId} User={UserId} Capability={Capability}",
                    groupId,
                    context.ZaloUserId,
                    suggestion.CapabilityKey);
            }
        }

        return sent;
    }

    private async Task<IReadOnlyList<RecentHumanMessage>> LoadRecentHumanMessagesAsync(
        string connectionId,
        string groupId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddHours(-12);
        return await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                !item.IsFromBot &&
                item.SentAt >= cutoff)
            .Select(item => new RecentHumanMessage(
                item.SenderId,
                item.SentAt,
                item.ObservationSource))
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<string, ZaloMemberIntegrationActivityHint>> BuildActivityHintsAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloMemberIntegrationSelection> selections,
        IReadOnlyList<RecentHumanMessage> messages,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidateIds = selections
            .Select(item => CleanId(item.Context.ZaloUserId))
            .ToHashSet(StringComparer.Ordinal);
        var result = messages
            .Where(item => candidateIds.Contains(CleanId(item.SenderId)) && !IsHistoricalSource(item.ObservationSource))
            .GroupBy(item => CleanId(item.SenderId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new ZaloMemberIntegrationActivityHint(
                    group.Key,
                    group.Max(item => item.SentAt),
                    "message"),
                StringComparer.Ordinal);

        if (candidateIds.Count == 0) return result;
        var activityCutoff = now.AddMinutes(-settings.RecentActivityMinutes);
        var recentPollCutoff = now.AddDays(-14);
        var votes = await db.ZaloPollVoteActivities
            .AsNoTracking()
            .Where(item =>
                candidateIds.Contains(item.ZaloUserId) &&
                item.IsCurrentlySelected &&
                item.FirstObservedAt >= activityCutoff &&
                item.PollSnapshot.ZaloConnectionId == connectionId &&
                item.PollSnapshot.GroupId == groupId &&
                item.PollSnapshot.IsAnalyticsEligible &&
                item.PollSnapshot.CreatedAtFromZalo != null &&
                item.PollSnapshot.CreatedAtFromZalo >= recentPollCutoff)
            .Select(item => new { item.ZaloUserId, item.FirstObservedAt })
            .ToListAsync(cancellationToken);

        foreach (var group in votes.GroupBy(item => CleanId(item.ZaloUserId), StringComparer.Ordinal))
        {
            var at = group.Max(item => item.FirstObservedAt);
            if (!result.TryGetValue(group.Key, out var current) || current.LastActivityAt is null || at > current.LastActivityAt)
                result[group.Key] = new ZaloMemberIntegrationActivityHint(group.Key, at, "poll");
        }

        var presence = await new ZaloMemberPresenceSignalStore(db)
            .GetRecentAsync(
                connectionId,
                groupId,
                candidateIds,
                activityCutoff,
                cancellationToken);
        foreach (var signal in presence)
        {
            if (!result.TryGetValue(signal.ZaloUserId, out var current) ||
                current.LastActivityAt is null ||
                signal.ObservedAt > current.LastActivityAt)
            {
                result[signal.ZaloUserId] = new ZaloMemberIntegrationActivityHint(
                    signal.ZaloUserId,
                    signal.ObservedAt,
                    signal.SignalType);
            }
        }
        return result;
    }

    private bool IsQuietEnough(
        DateTimeOffset now,
        DateTimeOffset? lastHumanMessageAt,
        ZaloMemberIntegrationActivityHint activity)
    {
        if (string.Equals(activity.Source, "typing", StringComparison.Ordinal) &&
            activity.LastActivityAt is { } typingAt &&
            now - typingAt < TimeSpan.FromMinutes(3))
            return false;
        var required = ZaloMemberIntegrationDeliveryPolicy.RequiredQuietMinutes(now, activity, settings);
        return lastHumanMessageAt is null || now - lastHumanMessageAt >= TimeSpan.FromMinutes(required);
    }

    private static bool IsHistoricalSource(string? source) =>
        string.Equals(source, "HistoricalBackfill", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "DesktopBackupImport", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeProviderMessageId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string CleanId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    private sealed record RecentHumanMessage(string SenderId, DateTimeOffset SentAt, string ObservationSource);
    private sealed record DailyMemberDelivery(int Count, DateTimeOffset LastSentAt);
}
