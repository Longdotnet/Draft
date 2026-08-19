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
        Enabled: configuration.GetValue("ZaloBot:Ambient:Presence:Enabled", false),
        SendEnabled: configuration.GetValue("ZaloBot:Ambient:Presence:SendEnabled", false),
        QuietMinutes: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:QuietMinutes", 90), 20, 720),
        MinBotIntervalMinutes: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:MinBotIntervalMinutes", 60), 15, 720),
        MaxProactivePerDay: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:MaxProactivePerDay", 4), 1, 10),
        StartHour: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:StartHour", 8), 0, 23),
        EndHour: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:Presence:EndHour", 23), 1, 24),
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
    DateTimeOffset? RecentFinishedSessionAt);

internal sealed record ZaloEngagementMove(ZaloEngagementMoveKind Kind, string Message);

internal static class ZaloGroupEngagementDirector
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static ZaloEngagementMove? Plan(
        ZaloSocialPresenceSnapshot snapshot,
        ZaloSocialPresenceSettings settings)
    {
        if (!settings.Enabled) return null;
        var localNow = snapshot.Now.ToOffset(VietnamOffset);
        if (!IsInsideWindow(localNow.Hour, settings.StartHour, settings.EndHour)) return null;
        if (snapshot.RecentTwoMinuteMessageCount >= 6) return null;
        if (snapshot.BotMessagesToday >= settings.MaxProactivePerDay) return null;
        if (snapshot.LastBotMessageAt is { } lastBot &&
            snapshot.Now - lastBot < TimeSpan.FromMinutes(settings.MinBotIntervalMinutes))
            return null;
        if (snapshot.LastUserMessageAt is { } lastUser &&
            snapshot.Now - lastUser < TimeSpan.FromMinutes(settings.QuietMinutes))
            return null;

        if (snapshot.UpcomingSessionAt is { } upcoming)
        {
            var until = upcoming - snapshot.Now;
            if (until >= TimeSpan.FromMinutes(30) && until <= TimeSpan.FromHours(8))
                return new(ZaloEngagementMoveKind.PregameBanter,
                    BuildPregame(snapshot.GroupId, settings.TrashTalkLevel));
        }

        if (snapshot.RecentFinishedSessionAt is { } finished &&
            snapshot.Now - finished <= TimeSpan.FromHours(4))
            return new(ZaloEngagementMoveKind.PostgameDebrief,
                BuildPostgame(snapshot.GroupId, settings.TrashTalkLevel));

        var selector = StableSelector(snapshot.GroupId, localNow.Date);
        return selector % 2 == 0
            ? new(ZaloEngagementMoveKind.QuietWake, BuildQuietWake(selector, settings.TrashTalkLevel))
            : new(ZaloEngagementMoveKind.HotTake, BuildHotTake(selector, settings.TrashTalkLevel));
    }

    private static bool IsInsideWindow(int hour, int startHour, int endHour) =>
        endHour == 24
            ? hour >= startHour
            : startHour <= endHour
                ? hour >= startHour && hour < endHour
                : hour >= startHour || hour < endHour;

    private static string BuildQuietWake(int selector, int level)
    {
        string[] mild =
        [
            "nay group im dữ, mọi người đang dưỡng tay hay dưỡng mồm vậy =))",
            "hello mấy cha, group còn thở không hay đợi tới sát giờ mới gáy =))",
            "im quá nha, ai mở combat nhẹ cho có không khí coi =))"
        ];
        string[] street =
        [
            "đm nay group im như chưa ai dám gáy vậy =)) sống hết không mấy cha",
            "vl nay ngoan dữ, chưa thấy ông nào lên Zalo gáy trước trận luôn =))",
            "đm im gì dữ vậy, ai quăng miếng content coi chứ NPC buồn ngủ rồi =))"
        ];
        var pool = level >= 3 ? street : mild;
        return pool[Math.Abs(selector) % pool.Length];
    }

    private static string BuildHotTake(int selector, int level)
    {
        string[] mild =
        [
            "hot take: team toàn người đập mạnh chưa chắc ngon bằng team biết chuyền với thủ nha, ai phản đối =))",
            "hỏi thiệt: setter ngon hay chủ công khỏe mới là thứ cứu team nhiều hơn? combat văn minh coi =))",
            "nay mở debate nhẹ: thắng do draft ngon hay do vô sân bớt gáy mới quan trọng hơn =))"
        ];
        string[] street =
        [
            "hot take: team mạnh trên giấy mà vô sân chuyền như cc thì cũng vậy thôi =)) ai phản đối vô combat",
            "đm hỏi thật, setter ngon với mấy ông đập khỏe mà đỡ bước 1 bay nóc thì chọn bên nào =))",
            "vl mở debate coi: draft ngon cứu được mấy ông vào sân mất não không =))"
        ];
        var pool = level >= 3 ? street : mild;
        return pool[Math.Abs(selector) % pool.Length];
    }

    private static string BuildPregame(string groupId, int level) =>
        level >= 3
            ? "đm tối nay ai gáy trước thì nhớ đánh cho đúng lời nha =)) đừng để Zalo nóng hơn trên sân"
            : "tối nay ai gáy trước thì nhớ đánh cho đúng lời nha =)) đừng để Zalo nóng hơn trên sân";

    private static string BuildPostgame(string groupId, int level) =>
        level >= 3
            ? "rồi khai đi mấy cha, nãy ai gáy dữ nhất mà vô sân im nhất vậy =)))"
            : "rồi khai đi, nãy ai gáy dữ nhất mà vô sân im nhất vậy =)))";

    private static int StableSelector(string groupId, DateTime date) =>
        StringComparer.Ordinal.GetHashCode($"{groupId}:{date:yyyyMMdd}");
}

public sealed class ZaloSocialPresenceService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    IConfiguration configuration,
    ILogger<ZaloSocialPresenceService> logger)
{
    public static ZaloSocialPresenceService Create(IServiceProvider services) => new(
        services.GetRequiredService<VolleyDraftDbContext>(),
        services.GetRequiredService<ZaloBridgeClient>(),
        services.GetRequiredService<IConfiguration>(),
        services.GetRequiredService<ILogger<ZaloSocialPresenceService>>());

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var settings = ZaloSocialPresenceSettings.FromConfiguration(configuration);
        if (!settings.Enabled) return;

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
                .Take(160)
                .ToListAsync(cancellationToken);
            if (messages.Count == 0) continue;

            var vietnamNow = now.ToOffset(TimeSpan.FromHours(7));
            var lastUser = messages
                .Where(item => !item.IsFromBot)
                .Select(item => (DateTimeOffset?)item.SentAt)
                .FirstOrDefault();
            var lastBot = messages
                .Where(item => item.IsFromBot)
                .Select(item => (DateTimeOffset?)item.SentAt)
                .FirstOrDefault();
            var botToday = messages.Count(item =>
                item.IsFromBot && item.SentAt.ToOffset(TimeSpan.FromHours(7)).Date == vietnamNow.Date);
            var recentTwoMinutes = messages.Count(item => now - item.SentAt <= TimeSpan.FromMinutes(2));

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
                lastBot,
                botToday,
                recentTwoMinutes,
                upcoming?.Name,
                upcoming?.StartTime,
                finished?.Name,
                finished?.StartTime);
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

            try
            {
                var local = now.ToOffset(TimeSpan.FromHours(7));
                await bridge.SendGroupMessageAsync(
                    group.Key.AccountId,
                    group.Key.GroupId,
                    move.Message,
                    [],
                    idempotencyKey: $"social-presence:{group.Key.GroupId}:{local:yyyyMMddHH}:{move.Kind}");
                logger.LogInformation(
                    "Social presence sent Group={GroupId} Move={Move}",
                    group.Key.GroupId,
                    move.Kind);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Social presence send failed Group={GroupId}", group.Key.GroupId);
            }
        }
    }
}
