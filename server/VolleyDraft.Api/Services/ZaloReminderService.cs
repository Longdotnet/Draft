using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed class ZaloReminderService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ILogger<ZaloReminderService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task SendDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .Include(session => session.ZaloConnection)
            .Where(session => session.BotEnabled &&
                              session.ReminderEnabled &&
                              session.StartTime != null &&
                              session.StartTime > now &&
                              session.ZaloConnectionId != null &&
                              session.ZaloGroupId != null &&
                              session.ZaloConnection != null &&
                              session.ZaloConnection.Status == ZaloConnectionStatus.Connected &&
                              session.Status != SessionStatus.Cancelled)
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0) return;

        var sessionIds = sessions.Select(session => session.Id).ToList();
        var counts = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => sessionIds.Contains(player.SessionId) && player.IsPresent)
            .GroupBy(player => player.SessionId)
            .Select(group => new { SessionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.SessionId, item => item.Count, cancellationToken);

        var groups = sessions.GroupBy(session => new { session.ZaloConnection!.AccountZaloId, session.ZaloGroupId });
        foreach (var group in groups)
        {
            var target = group
                .Where(session => counts.GetValueOrDefault(session.Id) < session.TeamCount * session.TeamSize)
                .OrderBy(session => session.StartTime)
                .FirstOrDefault();
            if (target?.StartTime is null) continue;

            var reminderStartsAt = target.StartTime.Value.AddHours(-target.ReminderLeadHours);
            var nextAllowedAt = target.LastReminderAt?.AddHours(target.ReminderIntervalHours);
            if (now < reminderStartsAt || (nextAllowedAt is not null && now < nextAllowedAt)) continue;

            var capacity = target.TeamCount * target.TeamSize;
            var playerCount = counts.GetValueOrDefault(target.Id);
            var missing = capacity - playerCount;
            var mentionLabel = "@all";
            var location = string.IsNullOrWhiteSpace(target.Location) ? string.Empty : $" tại {target.Location}";
            var message = $"{mentionLabel} Nhắc lịch {target.Name}: còn thiếu {missing} slot ({playerCount}/{capacity}). Trận lúc {FormatVietnamTime(target.StartTime.Value)}{location}.";
            try
            {
                await bridge.SendGroupMessageAsync(
                    target.ZaloConnection!.AccountZaloId,
                    target.ZaloGroupId!,
                    message,
                    [new BridgeOutgoingMention("-1", 0, mentionLabel.Length)]);
                target.LastReminderAt = now;
                db.ZaloGroupMessages.Add(new ZaloGroupMessage
                {
                    ZaloConnectionId = target.ZaloConnectionId!,
                    GroupId = target.ZaloGroupId!,
                    MessageId = $"reminder:{Guid.NewGuid():n}",
                    SenderId = target.ZaloConnection.AccountZaloId,
                    SenderName = target.ZaloConnection.DisplayName,
                    Content = message,
                    IsFromBot = true,
                    SentAt = now,
                    ReceivedAt = now
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(exception, "Could not send reminder for session {SessionId}", target.Id);
            }
        }
    }

    private static string FormatVietnamTime(DateTimeOffset time)
    {
        var local = time.ToOffset(VietnamOffset);
        var day = local.DayOfWeek switch
        {
            DayOfWeek.Monday => "thứ Hai",
            DayOfWeek.Tuesday => "thứ Ba",
            DayOfWeek.Wednesday => "thứ Tư",
            DayOfWeek.Thursday => "thứ Năm",
            DayOfWeek.Friday => "thứ Sáu",
            DayOfWeek.Saturday => "thứ Bảy",
            _ => "Chủ nhật"
        };
        return $"{local:HH:mm} {day} {local:dd/MM}";
    }
}

public sealed class ZaloReminderWorker(IServiceScopeFactory scopeFactory, ILogger<ZaloReminderWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ZaloReminderService>()
                    .SendDueRemindersAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Zalo reminder cycle failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
