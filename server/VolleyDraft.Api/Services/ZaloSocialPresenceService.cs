using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloSocialPresenceSettings(
    bool Enabled,
    bool SendEnabled,
    int QuietMinutes,
    int MinBotIntervalMinutes,
    int MaxProactivePerDay,
    int StartHour,
    int EndHour,
    int TrashTalkLevel)
{
    public static ZaloSocialPresenceSettings FromConfiguration(IConfiguration configuration) => new(
        Enabled: configuration.GetValue("ZaloBot:Ambient:Presence:Enabled", true),
        SendEnabled: configuration.GetValue("ZaloBot:Ambient:Presence:SendEnabled", true),
        QuietMinutes: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:QuietMinutes", 90), 20, 720),
        MinBotIntervalMinutes: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:MinBotIntervalMinutes", 60), 15, 720),
        MaxProactivePerDay: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:MaxProactivePerDay", 4), 1, 10),
        StartHour: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:StartHour", 6), 0, 23),
        EndHour: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:EndHour", 1), 1, 24),
        TrashTalkLevel: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:TrashTalkLevel", 3), 0, 3));
}

internal enum ZaloEngagementMoveKind
{
    QuietWake,
    HotTake,
    PregameBanter,
    PostgameDebrief
}

internal sealed record ZaloSocialPresenceSnapshot(
    string GroupId,
    DateTimeOffset Now,
    DateTimeOffset? LastUserMessageAt,
    DateTimeOffset? LastBotMessageAt,
    int BotMessagesToday,
    int RecentTwoMinuteMessageCount,
    string? UpcomingSessionName,
    DateTimeOffset? UpcomingSessionAt,
    string? RecentFinishedSessionName,
    DateTimeOffset? RecentFinishedSessionAt,
    IReadOnlyList<ZaloProactiveMessageHistoryData>? ProactiveHistory = null,
    bool LegacyAmbientTrashSentToday = false);

internal sealed record ZaloEngagementMove(
    ZaloEngagementMoveKind Kind,
    string Message,
    string ContentKey);

internal static class ZaloGroupEngagementDirector
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    private static readonly string[] QuietWakeMild =
    [
        "nay group im dữ, mọi người đang dưỡng tay hay dưỡng mồm vậy =))",
        "hello mấy cha, group còn thở không hay đợi tới sát giờ mới gáy =))",
        "im quá nha, ai mở combat nhẹ cho có không khí coi =))"
    ];

    private static readonly string[] QuietWakeStreet =
    [
        "đm nay group im như chưa ai dám gáy vậy =)) sống hết không mấy cha",
        "vl nay ngoan dữ, chưa thấy ông nào lên Zalo gáy trước trận luôn =))",
        "đm im gì dữ vậy, ai quăng miếng content coi chứ NPC buồn ngủ rồi =))"
    ];

    private static readonly string[] HotTakeMild =
    [
        "hot take: team toàn người đập mạnh chưa chắc ngon bằng team biết chuyền với thủ nha, ai phản đối =))",
        "hỏi thiệt: setter ngon hay chủ công khỏe mới là thứ cứu team nhiều hơn? combat văn minh coi =))",
        "nay mở debate nhẹ: thắng do draft ngon hay do vô sân bớt gáy mới quan trọng hơn =))"
    ];

    private static readonly string[] HotTakeStreet =
    [
        "hot take: team mạnh trên giấy mà vô sân chuyền như cc thì cũng vậy thôi =)) ai phản đối vô combat",
        "đm hỏi thật, setter ngon với mấy ông đập khỏe mà đỡ bước 1 bay nóc thì chọn bên nào =))",
        "vl mở debate coi: draft ngon cứu được mấy ông vào sân mất não không =))"
    ];

    public static ZaloEngagementMove? Plan(
        ZaloSocialPresenceSnapshot snapshot,
        ZaloSocialPresenceSettings settings)
    {
        if (!settings.Enabled) return null;

        var localNow = snapshot.Now.ToOffset(VietnamOffset);
        if (!IsInsideWindow(localNow.Hour, settings.StartHour, settings.EndHour)) return null;
        if (snapshot.RecentTwoMinuteMessageCount >= 6) return null;
        if (snapshot.BotMessagesToday >= settings.MaxProactivePerDay) return null;

        var history = snapshot.ProactiveHistory ?? [];
        var latestDurable = history
            .OrderByDescending(item => item.SentAt)
            .FirstOrDefault()?.SentAt;
        var lastBotOrProactive = Max(snapshot.LastBotMessageAt, latestDurable);
        if (lastBotOrProactive is { } lastBot &&
            snapshot.Now - lastBot < TimeSpan.FromMinutes(settings.MinBotIntervalMinutes))
            return null;
        if (snapshot.LastUserMessageAt is { } lastUser &&
            snapshot.Now - lastUser < TimeSpan.FromMinutes(settings.QuietMinutes))
            return null;

        if (snapshot.UpcomingSessionAt is { } upcoming)
        {
            var until = upcoming - snapshot.Now;
            if (until >= TimeSpan.FromMinutes(30) && until <= TimeSpan.FromHours(8))
            {
                var contentKey = EventContentKey("pregame", snapshot.UpcomingSessionName, upcoming);
                if (!HasContentKey(history, contentKey))
                {
                    return new(
                        ZaloEngagementMoveKind.PregameBanter,
                        BuildPregame(settings.TrashTalkLevel),
                        contentKey);
                }
            }
        }

        if (snapshot.RecentFinishedSessionAt is { } finished &&
            snapshot.Now - finished <= TimeSpan.FromHours(4))
        {
            var contentKey = EventContentKey("postgame", snapshot.RecentFinishedSessionName, finished);
            if (!HasContentKey(history, contentKey))
            {
                return new(
                    ZaloEngagementMoveKind.PostgameDebrief,
                    BuildPostgame(settings.TrashTalkLevel),
                    contentKey);
                }
            }

        // Ambient trash/debate is a once-per-local-day lane. Session-specific pre/post
        // banter above has an occurrence key of its own and does not consume this slot.
        if (snapshot.LegacyAmbientTrashSentToday || history.Any(item =>
                string.Equals(item.Lane, ZaloProactiveLane.SocialPresence, StringComparison.Ordinal) &&
                IsAmbientKind(item.Kind) &&
                item.SentAt.ToOffset(VietnamOffset).Date == localNow.Date))
            return null;

        var kind = StableIndex($"{snapshot.GroupId}:{localNow:yyyy-MM-dd}:ambient-kind", 2) == 0
            ? ZaloEngagementMoveKind.QuietWake
            : ZaloEngagementMoveKind.HotTake;
        var phrase = SelectAmbientPhrase(
            snapshot.GroupId,
            localNow.Date,
            kind,
            settings.TrashTalkLevel,
            history);
        return new(kind, phrase.Message, phrase.ContentKey);
    }

    internal static bool IsAmbientTrashMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return QuietWakeMild.Contains(message, StringComparer.Ordinal) ||
               QuietWakeStreet.Contains(message, StringComparer.Ordinal) ||
               HotTakeMild.Contains(message, StringComparer.Ordinal) ||
               HotTakeStreet.Contains(message, StringComparer.Ordinal);
    }

    private static (string Message, string ContentKey) SelectAmbientPhrase(
        string groupId,
        DateTime localDate,
        ZaloEngagementMoveKind kind,
        int level,
        IReadOnlyList<ZaloProactiveMessageHistoryData> history)
    {
        var pool = kind switch
        {
            ZaloEngagementMoveKind.QuietWake when level >= 3 => QuietWakeStreet,
            ZaloEngagementMoveKind.QuietWake => QuietWakeMild,
            ZaloEngagementMoveKind.HotTake when level >= 3 => HotTakeStreet,
            _ => HotTakeMild
        };
        var family = kind == ZaloEngagementMoveKind.QuietWake ? "quiet" : "hot";
        var tone = level >= 3 ? "street" : "mild";

        var ranked = pool
            .Select((message, index) =>
            {
                var contentKey = $"ambient:{family}:{tone}:{index}";
                var matching = history
                    .Where(item =>
                        string.Equals(item.Lane, ZaloProactiveLane.SocialPresence, StringComparison.Ordinal) &&
                        string.Equals(item.ContentKey, contentKey, StringComparison.Ordinal))
                    .ToArray();
                return new
                {
                    Message = message,
                    ContentKey = contentKey,
                    Usage = matching.Length,
                    LastSentAt = matching.Length == 0
                        ? DateTimeOffset.MinValue
                        : matching.Max(item => item.SentAt)
                };
            })
            .OrderBy(item => item.Usage)
            .ThenBy(item => item.LastSentAt)
            .ThenBy(item => StableIndex(
                $"{groupId}:{localDate:yyyy-MM-dd}:{item.ContentKey}",
                10000))
            .ToArray();

        var selected = ranked[0];
        return (selected.Message, selected.ContentKey);
    }

    private static bool IsInsideWindow(int hour, int startHour, int endHour) =>
        endHour == 24
            ? hour >= startHour
            : startHour <= endHour
                ? hour >= startHour && hour < endHour
                : hour >= startHour || hour < endHour;

    private static string BuildPregame(int level) =>
        level >= 3
            ? "đm tối nay ai gáy trước thì nhớ đánh cho đúng lời nha =)) đừng để Zalo nóng hơn trên sân"
            : "tối nay ai gáy trước thì nhớ đánh cho đúng lời nha =)) đừng để Zalo nóng hơn trên sân";

    private static string BuildPostgame(int level) =>
        level >= 3
            ? "rồi khai đi mấy cha, nãy ai gáy dữ nhất mà vô sân im nhất vậy =)))"
            : "rồi khai đi, nãy ai gáy dữ nhất mà vô sân im nhất vậy =)))";

    private static string EventContentKey(string prefix, string? sessionName, DateTimeOffset at)
    {
        var raw = $"{prefix}|{sessionName?.Trim()}|{at.ToUniversalTime():O}";
        return $"{prefix}:{StableFingerprint(raw)}";
    }

    private static bool HasContentKey(
        IReadOnlyList<ZaloProactiveMessageHistoryData> history,
        string contentKey) =>
        history.Any(item =>
            string.Equals(item.Lane, ZaloProactiveLane.SocialPresence, StringComparison.Ordinal) &&
            string.Equals(item.ContentKey, contentKey, StringComparison.Ordinal));

    private static bool IsAmbientKind(string kind) =>
        string.Equals(kind, nameof(ZaloEngagementMoveKind.QuietWake), StringComparison.Ordinal) ||
        string.Equals(kind, nameof(ZaloEngagementMoveKind.HotTake), StringComparison.Ordinal);

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left >= right ? left : right;
    }

    private static int StableIndex(string value, int modulo)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return (int)(BitConverter.ToUInt32(bytes, 0) % (uint)Math.Max(1, modulo));
    }

    private static string StableFingerprint(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}

internal sealed record ZaloSocialPresenceTargetSession(
    string GroupId,
    string ConnectionId,
    string AccountId,
    string? Name,
    DateTimeOffset? StartTime,
    SessionStatus? Status);

public sealed class ZaloSocialPresenceService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    IConfiguration configuration,
    ILogger<ZaloSocialPresenceService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly TimeSpan SendLeaseDuration = TimeSpan.FromMinutes(2);
    private readonly ZaloProactiveMessageStore proactiveStore = new(db);

    public static ZaloSocialPresenceService Create(IServiceProvider services) => new(
        services.GetRequiredService<VolleyDraftDbContext>(),
        services.GetRequiredService<ZaloBridgeClient>(),
        services.GetRequiredService<IConfiguration>(),
        services.GetRequiredService<ILogger<ZaloSocialPresenceService>>());

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var settings = ZaloSocialPresenceSettings.FromConfiguration(configuration);
        var dailySettings = ZaloDailySocialSettings.FromConfiguration(configuration);
        if (!settings.Enabled) return;

        await proactiveStore.EnsureAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var sessionRows = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.BotEnabled &&
                              session.ZaloGroupId != null &&
                              session.ZaloConnectionId != null &&
                              session.ZaloConnection != null &&
                              session.ZaloConnection.Status == ZaloConnectionStatus.Connected)
            .Select(session => new ZaloSocialPresenceTargetSession(
                session.ZaloGroupId!,
                session.ZaloConnectionId!,
                session.ZaloConnection!.AccountZaloId,
                session.Name,
                session.StartTime,
                session.Status))
            .ToListAsync(cancellationToken);

        // A configured Zalo group is a proactive target even when no current MatchSession
        // exists. MatchSessions below remain useful only as optional pre/post-game context.
        var configuredTargets = await new ZaloProactiveTargetResolver(db)
            .GetTargetsAsync(cancellationToken);
        sessionRows.AddRange(configuredTargets
            .Where(target => !sessionRows.Any(row =>
                string.Equals(row.ConnectionId, target.ConnectionId, StringComparison.Ordinal) &&
                string.Equals(row.GroupId, target.GroupId, StringComparison.Ordinal)))
            .Select(target => new ZaloSocialPresenceTargetSession(
                target.GroupId,
                target.ConnectionId,
                target.AccountId,
                null,
                null,
                null)));

        foreach (var group in sessionRows
                     .GroupBy(item => new { item.GroupId, item.ConnectionId, item.AccountId })
                     .Take(100))
        {
            var messages = await db.ZaloGroupMessages
                .AsNoTracking()
                .Where(item => item.ZaloConnectionId == group.Key.ConnectionId &&
                               item.GroupId == group.Key.GroupId)
                .OrderByDescending(item => item.SentAt)
                .Take(500)
                .ToListAsync(cancellationToken);
            if (messages.Count == 0) continue;

            var proactiveHistory = await proactiveStore.GetHistoryAsync(
                group.Key.ConnectionId,
                group.Key.GroupId,
                500,
                cancellationToken);

            var localNow = now.ToOffset(VietnamOffset);
            var lastUser = messages
                .Where(item => !item.IsFromBot)
                .Select(item => (DateTimeOffset?)item.SentAt)
                .FirstOrDefault();
            var lastBot = messages
                .Where(item => item.IsFromBot)
                .Select(item => (DateTimeOffset?)item.SentAt)
                .FirstOrDefault();
            var presenceToday = proactiveHistory.Count(item =>
                string.Equals(item.Lane, ZaloProactiveLane.SocialPresence, StringComparison.Ordinal) &&
                item.SentAt.ToOffset(VietnamOffset).Date == localNow.Date);
            var legacyBotToday = messages.Count(item =>
                item.IsFromBot && item.SentAt.ToOffset(VietnamOffset).Date == localNow.Date);
            var recentTwoMinutes = messages.Count(item => now - item.SentAt <= TimeSpan.FromMinutes(2));
            var legacyTrashSentToday = messages.Any(item =>
                item.IsFromBot &&
                item.SentAt.ToOffset(VietnamOffset).Date == localNow.Date &&
                ZaloGroupEngagementDirector.IsAmbientTrashMessage(item.Content));
            var effectiveLastBot = Max(
                lastBot,
                proactiveHistory.OrderByDescending(item => item.SentAt).FirstOrDefault()?.SentAt);

            var greetingHistory = await LoadGreetingHistoryAsync(
                group.Key.ConnectionId,
                group.Key.GroupId,
                proactiveHistory,
                dailySettings,
                now,
                cancellationToken);

            var greeting = ZaloDailyGreetingRecoveryPolicy.Plan(
                new ZaloDailyGreetingSnapshot(
                    group.Key.GroupId,
                    now,
                    effectiveLastBot,
                    recentTwoMinutes,
                    greetingHistory),
                dailySettings,
                settings.MinBotIntervalMinutes);
            if (greeting is not null)
            {
                if (!settings.SendEnabled)
                {
                    logger.LogInformation(
                        "Daily greeting shadow Group={GroupId} Kind={Kind} Mood={Mood} Message={Message}",
                        group.Key.GroupId,
                        greeting.Kind,
                        greeting.Mood,
                        greeting.Message);
                    continue;
                }

                var greetingIdempotencyKey =
                    $"social-greeting:{group.Key.ConnectionId}:{group.Key.GroupId}:{greeting.ServiceDate:yyyyMMdd}:{greeting.Kind}";
                await TrySendProactiveAsync(
                    group.Key.AccountId,
                    group.Key.ConnectionId,
                    group.Key.GroupId,
                    ZaloProactiveLane.DailyGreeting,
                    greeting.Kind.ToString(),
                    $"greeting:{greeting.ServiceDate:yyyy-MM-dd}:{greeting.Kind}",
                    greeting.Message,
                    greetingIdempotencyKey,
                    now,
                    settings.MinBotIntervalMinutes,
                    cancellationToken);
                continue;
            }

            // Morning and bedtime (including bounded recovery) should feel warm,
            // not like the street-trash persona.
            if (ZaloDailyGreetingRecoveryPolicy.IsGreetingZone(now)) continue;

            var upcoming = group
                .Where(item => item.StartTime is not null &&
                               item.StartTime >= now &&
                               item.Status is not (SessionStatus.Cancelled or SessionStatus.Finished))
                .OrderBy(item => item.StartTime)
                .FirstOrDefault();
            var finished = group
                .Where(item => item.StartTime is not null &&
                               item.StartTime <= now &&
                               item.Status == SessionStatus.Finished)
                .OrderByDescending(item => item.StartTime)
                .FirstOrDefault();

            var snapshot = new ZaloSocialPresenceSnapshot(
                group.Key.GroupId,
                now,
                lastUser,
                effectiveLastBot,
                Math.Max(presenceToday, Math.Min(legacyBotToday, settings.MaxProactivePerDay)),
                recentTwoMinutes,
                upcoming?.Name,
                upcoming?.StartTime,
                finished?.Name,
                finished?.StartTime,
                proactiveHistory,
                legacyTrashSentToday);
            var move = ZaloGroupEngagementDirector.Plan(snapshot, settings);
            if (move is null) continue;

            if (!settings.SendEnabled)
            {
                logger.LogInformation(
                    "Social presence shadow Group={GroupId} Move={Move} Message={Message}",
                    group.Key.GroupId,
                    move.Kind,
                    move.Message);
                continue;
            }

            var occurrence = move.Kind is ZaloEngagementMoveKind.QuietWake or ZaloEngagementMoveKind.HotTake
                ? "ambient-trash"
                : move.ContentKey;
            var socialIdempotencyKey =
                $"social-presence:{group.Key.ConnectionId}:{group.Key.GroupId}:{localNow:yyyyMMdd}:{occurrence}";
            var sent = await TrySendProactiveAsync(
                group.Key.AccountId,
                group.Key.ConnectionId,
                group.Key.GroupId,
                ZaloProactiveLane.SocialPresence,
                move.Kind.ToString(),
                move.ContentKey,
                move.Message,
                socialIdempotencyKey,
                now,
                settings.MinBotIntervalMinutes,
                cancellationToken);
            if (sent)
            {
                logger.LogInformation(
                    "Social presence sent Group={GroupId} Move={Move} ContentKey={ContentKey}",
                    group.Key.GroupId,
                    move.Kind,
                    move.ContentKey);
            }
        }
    }

    private async Task<IReadOnlyList<ZaloSocialHistoryMessage>> LoadGreetingHistoryAsync(
        string connectionId,
        string groupId,
        IReadOnlyList<ZaloProactiveMessageHistoryData> proactiveHistory,
        ZaloDailySocialSettings dailySettings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!dailySettings.Enabled || !ZaloDailyGreetingRecoveryPolicy.IsGreetingZone(now))
            return [];

        var echoed = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == connectionId &&
                           item.GroupId == groupId &&
                           item.IsFromBot &&
                           item.SentAt >= now.AddDays(-dailySettings.GreetingRepeatDays))
            .OrderByDescending(item => item.SentAt)
            .Take(500)
            .Select(item => new ZaloSocialHistoryMessage(item.Content, item.SentAt))
            .ToArrayAsync(cancellationToken);
        var durable = proactiveHistory
            .Where(item =>
                string.Equals(item.Lane, ZaloProactiveLane.DailyGreeting, StringComparison.Ordinal) &&
                item.SentAt >= now.AddDays(-dailySettings.GreetingRepeatDays))
            .Select(item => new ZaloSocialHistoryMessage(item.MessageText, item.SentAt));

        return echoed
            .Concat(durable)
            .GroupBy(item => $"{item.SentAt:O}\n{item.Content}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(item => item.SentAt)
            .ToArray();
    }

    private async Task<bool> TrySendProactiveAsync(
        string accountId,
        string connectionId,
        string groupId,
        string lane,
        string kind,
        string contentKey,
        string message,
        string idempotencyKey,
        DateTimeOffset now,
        int cooldownMinutes,
        CancellationToken cancellationToken)
    {
        if (!await proactiveStore.TryAcquireLeaseAsync(
                connectionId,
                groupId,
                idempotencyKey,
                now,
                SendLeaseDuration,
                cancellationToken))
            return false;

        var providerAccepted = false;
        try
        {
            var response = await bridge.SendGroupMessageAsync(
                accountId,
                groupId,
                message,
                [],
                imageUrl: null,
                idempotencyKey: idempotencyKey);
            if (!response.Sent)
            {
                await proactiveStore.ReleaseLeaseAsync(
                    connectionId,
                    groupId,
                    idempotencyKey,
                    cancellationToken);
                return false;
            }

            providerAccepted = true;
            await PersistAcceptedSendBestEffortAsync(
                connectionId,
                groupId,
                lane,
                kind,
                contentKey,
                message,
                response.MessageId,
                idempotencyKey,
                now,
                cooldownMinutes,
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!providerAccepted)
                await TryReleaseLeaseAsync(connectionId, groupId, idempotencyKey, cancellationToken);

            logger.LogWarning(
                exception,
                "Proactive send failed Group={GroupId} Lane={Lane} Kind={Kind}",
                groupId,
                lane,
                kind);
            return false;
        }
    }

    private async Task PersistAcceptedSendBestEffortAsync(
        string connectionId,
        string groupId,
        string lane,
        string kind,
        string contentKey,
        string message,
        string? providerMessageId,
        string idempotencyKey,
        DateTimeOffset sentAt,
        int cooldownMinutes,
        CancellationToken cancellationToken)
    {
        try
        {
            await proactiveStore.CommitCooldownAsync(
                connectionId,
                groupId,
                idempotencyKey,
                sentAt.AddMinutes(cooldownMinutes),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Proactive cooldown persistence failed after accepted send Group={GroupId} Key={Key}",
                groupId,
                idempotencyKey);
        }

        try
        {
            await proactiveStore.RecordAsync(
                new ZaloProactiveMessageHistoryData(
                    Guid.NewGuid().ToString("n"),
                    connectionId,
                    groupId,
                    sentAt.ToOffset(VietnamOffset).ToString("yyyy-MM-dd"),
                    lane,
                    kind,
                    contentKey,
                    null,
                    null,
                    message,
                    sentAt,
                    NormalizeProviderMessageId(providerMessageId),
                    idempotencyKey),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Proactive history persistence failed after accepted send Group={GroupId} Key={Key}",
                groupId,
                idempotencyKey);
        }
    }

    private async Task TryReleaseLeaseAsync(
        string connectionId,
        string groupId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await proactiveStore.ReleaseLeaseAsync(connectionId, groupId, idempotencyKey, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not release proactive send lease Group={GroupId} Key={Key}",
                groupId,
                idempotencyKey);
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left >= right ? left : right;
    }

    private static string? NormalizeProviderMessageId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.EndsWith("_0", StringComparison.Ordinal))
            text = text[..^2];
        return text.Length == 0 ? null : text;
    }
}
