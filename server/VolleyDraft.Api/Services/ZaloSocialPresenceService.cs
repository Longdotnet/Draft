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
        var lastProactive = history
            .OrderByDescending(item => item.SentAt)
            .FirstOrDefault();
        var lastBotOrProactive = Max(snapshot.LastBotMessageAt, lastProactive?.SentAt);
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
                var key = EventContentKey("pregame", snapshot.UpcomingSessionName, upcoming);
                if (!HasContentKey(history, key))
                {
                    return new(
                        ZaloEngagementMoveKind.PregameBanter,
                        BuildPregame(settings.TrashTalkLevel),
                        key);
                }
            }
        }

        if (snapshot.RecentFinishedSessionAt is { } finished &&
            snapshot.Now - finished <= TimeSpan.FromHours(4))
        {
            var key = EventContentKey("postgame", snapshot.RecentFinishedSessionName, finished);
            if (!HasContentKey(history, key))
            {
                return new(
                    ZaloEngagementMoveKind.PostgameDebrief,
                    BuildPostgame(settings.TrashTalkLevel),
                    key);
            }
        }

        // Ambient trash/debate is intentionally a once-per-local-day lane. Event-specific
        // pre/post-game banter above has its own occurrence key and does not consume this.
        if (snapshot.LegacyAmbientTrashSentToday || history.Any(item =>
                string.Equals(item.Lane, ZaloProactiveLane.SocialPresence, StringComparison.Ordinal) &&
                IsAmbientKind(item.Kind) &&
                item.SentAt.ToOffset(VietnamOffset).Date == localNow.Date))
            return null;

        var selector = StableIndex($"{snapshot.GroupId}:{localNow:yyyy-MM-dd}:ambient-kind", 2);
        if (selector == 0)
        {
            var phrase = SelectAmbientPhrase(
                snapshot.GroupId,
                localNow.Date,
                ZaloEngagementMoveKind.QuietWake,
                settings.TrashTalkLevel,
                history);
            return new(ZaloEngagementMoveKind.QuietWake, phrase.Message, phrase.ContentKey);
        }

        var hotTake = SelectAmbientPhrase(
            snapshot.GroupId,
            localNow.Date,
            ZaloEngagementMoveKind.HotTake,
            settings.TrashTalkLevel,
            history);
        return new(ZaloEngagementMoveKind.HotTake, hotTake.Message, hotTake.ContentKey);
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

        var ranked = pool
            .Select((message, index) =>
            {
                var contentKey = $"ambient:{family}:{(level >= 3 ? "street" : "mild")}:{index}";
                var matching = history
                    .Where(item =>
                        string.Equals(item.Lane, ZaloProactiveLane.SocialPresence, StringComparison.Ordinal) &&
                        string.Equals(item.ContentKey, contentKey, StringComparison.Ordinal))
                    .ToList();
                return new
                {
                    Message = message,
                    ContentKey = contentKey,
                    Usage = matching.Count,
                    LastSentAt = matching.Count == 0
                        ? DateTimeOffset.MinValue
                        : matching.Max(item => item.SentAt)
                };
            })
            .OrderBy(item => item.Usage)
            .ThenBy(item => item.LastSentAt)
            .ThenBy(item => StableIndex(
                $"{groupId}:{localDate:yyyy-MM-dd}:{item.ContentKey}",
                10000))
            .ToList();

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
            .Select(session => new
            {
                GroupId = session.ZaloGroupId!,
                ConnectionId = session.ZaloConnectionId!,
                AccountId = session.ZaloConnection!.AccountZaloId,
                session.Name,
                session.StartTime,
                session.Status
            })
            .ToListAsync(cancellationToken);

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

            var vietnamNow = now.ToOffset(VietnamOffset);
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
                item.SentAt.ToOffset(VietnamOffset).Date == vietnamNow.Date);
            var legacyBotToday = messages.Count(item =>
                item.IsFromBot && item.SentAt.ToOffset(VietnamOffset).Date == vietnamNow.Date);
            var recentTwoMinutes = messages.Count(item => now - item.SentAt <= TimeSpan.FromMinutes(2));
            var legacyTrashSentToday = messages.Any(item =>
                item.IsFromBot &&
                item.SentAt.ToOffset(VietnamOffset).Date == vietnamNow.Date &&
                ZaloGroupEngagementDirector.IsAmbientTrashMessage(item.Content));
            var effectiveLastBot = Max(
                lastBot,
                proactiveHistory.OrderByDescending(item => item.SentAt).FirstOrDefault()?.SentAt);

            var greetingHistory = Array.Empty<ZaloSocialHistoryMessage>();
            if (dailySettings.Enabled && ZaloDailyGreetingEngine.IsSoftGreetingZone(now))
            {
                var echoedGreetingHistory = await db.ZaloGroupMessages
                    .AsNoTracking()
                    .Where(item => item.ZaloConnectionId == group.Key.ConnectionId &&
                                   item.GroupId == group.Key.GroupId &&
                                   item.IsFromBot &&
                                   item.SentAt >= now.AddDays(-dailySettings.GreetingRepeatDays))
                    .OrderByDescending(item => item.SentAt)
                    .Take(500)
                    .Select(item => new ZaloSocialHistoryMessage(item.Content, item.SentAt))
                    .ToArrayAsync(cancellationToken);
                var durableGreetingHistory = proactiveHistory
                    .Where(item =>
                        string.Equals(item.Lane, ZaloProactiveLane.DailyGreeting, StringComparison.Ordinal) &&
                        item.SentAt >= now.AddDays(-dailySettings.GreetingRepeatDays))
                    .Select(item => new ZaloSocialHistoryMessage(item.MessageText, item.SentAt));

                greetingHistory = echoedGreetingHistory
                    .Concat(durableGreetingHistory)
                    .GroupBy(item => $"{item.SentAt:O}\n{item.Text}", StringComparer.Ordinal)
                    .Select(grouping => grouping.First())
                    .OrderByDescending(item => item.SentAt)
                    .ToArray();
            }

            var greeting = ZaloDailyGreetingEngine.Plan(
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

                var idempotencyKey =
                    $"social-greeting:{group.Key.ConnectionId}:{group.Key.GroupId}:{greeting.ServiceDate:yyyyMMdd}:{greeting.Kind}";
                if (!await proactiveStore.TryAcquireLeaseAsync(
                        group.Key.ConnectionId,
                        group.Key.GroupId,
                        idempotencyKey,
                        now,
                        SendLeaseDuration,
                        cancellationToken))
                    continue;

                var accepted = false;
                try
                {
                    var response = await bridge.SendGroupMessageAsync(
                        group.Key.AccountId,
                        group.Key.GroupId,
                        greeting.Message,
                        [],
                        imageUrl: null,
                        idempotencyKey: idempotencyKey);
                    if (!response.Sent)
                    {
                        await proactiveStore.ReleaseLeaseAsync(
                            group.Key.ConnectionId,
                            group.Key.GroupId,
                            idempotencyKey,
                            cancellationToken);
                        continue;
                    }

                    accepted = true;
                    await RememberAcceptedProactiveAsync(
                        group.Key.ConnectionId,
                        group.Key.GroupId,
                        ZaloProactiveLane.DailyGreeting,
                        greeting.Kind.ToString(),
                        $"greeting:{greeting.ServiceDate:yyyy-MM-dd}:{greeting.Kind}",
                        null,
                        null,
                        greeting.Message,
                        response.MessageId,
                        idempotencyKey,
                        now,
                        settings.MinBotIntervalMinutes,
                        cancellationToken);
                    logger.LogInformation(
                        "Daily greeting sent as text only Group={GroupId} Kind={Kind} Mood={Mood}",
                        group.Key.GroupId,
                        greeting.Kind,
                        greeting.Mood);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (!accepted)
                    {
                        await TryReleaseLeaseAsync(
                            group.Key.ConnectionId,
                            group.Key.GroupId,
                            idempotencyKey,
                            cancellationToken);
                    }
                    logger.LogWarning(exception, "Daily greeting send failed Group={GroupId}", group.Key.GroupId);
                }
                continue;
            }

            // Morning and bedtime should feel warm, not like the street-trash persona.
            // If today is not a greeting day we stay quiet in these soft zones instead
            // of sending a trashy QuietWake/HotTake. Direct mentions are handled by the
            // normal realtime bot pipeline and remain available at any hour.
            if (ZaloDailyGreetingEngine.IsSoftGreetingZone(now)) continue;

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

            var local = now.ToOffset(VietnamOffset);
            var occurrence = move.Kind is ZaloEngagementMoveKind.QuietWake or ZaloEngagementMoveKind.HotTake
                ? "ambient-trash"
                : move.ContentKey;
            var idempotencyKey =
                $"social-presence:{group.Key.ConnectionId}:{group.Key.GroupId}:{local:yyyyMMdd}:{occurrence}";
            if (!await proactiveStore.TryAcquireLeaseAsync(
                    group.Key.ConnectionId,
                    group.Key.GroupId,
                    idempotencyKey,
                    now,
                    SendLeaseDuration,
                    cancellationToken))
                continue;

            var socialAccepted = false;
            try
            {
                var response = await bridge.SendGroupMessageAsync(
                    group.Key.AccountId,
                    group.Key.GroupId,
                    move.Message,
                    [],
                    idempotencyKey: idempotencyKey);
                if (!response.Sent)
                {
                    await proactiveStore.ReleaseLeaseAsync(
                        group.Key.ConnectionId,
                        group.Key.GroupId,
                        idempotencyKey,
                        cancellationToken);
                    continue;
                }

                socialAccepted = true;
                await RememberAcceptedProactiveAsync(
                    group.Key.ConnectionId,
                    group.Key.GroupId,
                    ZaloProactiveLane.SocialPresence,
                    move.Kind.ToString(),
                    move.ContentKey,
                    null,
                    null,
                    move.Message,
                    response.MessageId,
                    idempotencyKey,
                    now,
                    settings.MinBotIntervalMinutes,
                    cancellationToken);
                logger.LogInformation(
                    "Social presence sent Group={GroupId} Move={Move} ContentKey={ContentKey}",
                    group.Key.GroupId,
                    move.Kind,
                    move.ContentKey);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (!socialAccepted)
                {
                    await TryReleaseLeaseAsync(
                        group.Key.ConnectionId,
                        group.Key.GroupId,
                        idempotencyKey,
                        cancellationToken);
                }
                logger.LogWarning(exception, "Social presence send failed Group={GroupId}", group.Key.GroupId);
            }
        }
    }

    private async Task RememberAcceptedProactiveAsync(
        string connectionId,
        string groupId,
        string lane,
        string kind,
        string contentKey,
        string? subjectUserId,
        string? subjectName,
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
                    subjectUserId,
                    subjectName,
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
