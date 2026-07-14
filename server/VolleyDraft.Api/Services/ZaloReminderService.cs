using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ReminderRunResult(
    int GroupCount,
    int SentCount,
    int FailedCount,
    int SkippedCount);

internal sealed record AudienceMessage(
    string Message,
    IReadOnlyList<BridgeOutgoingMention> Mentions);

public sealed class ZaloReminderService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloIntegrationService zaloIntegration,
    SessionWaitlistService waitlists,
    AiAssistantService ai,
    IConfiguration configuration,
    ILogger<ZaloReminderService> logger)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task<ReminderRunResult> SendDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await waitlists.ProcessAllAsync(cancellationToken);
        await RefreshPollRostersAsync(now, cancellationToken);
        db.ChangeTracker.Clear();
        var naturalScheduleResult = await SendDueNaturalSchedulesAsync(now, cancellationToken);
        db.ChangeTracker.Clear();

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
        if (sessions.Count == 0) return naturalScheduleResult;

        var sessionIds = sessions.Select(session => session.Id).ToList();
        var counts = await GetEffectiveSlotCountsAsync(sessionIds, cancellationToken);
        var urgentWindow = TimeSpan.FromHours(Math.Clamp(
            configuration.GetValue("Scheduler:UrgentWindowHours", 1),
            1,
            24));
        var urgentDelay = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("Scheduler:UrgentDelayMinutes", 0),
            0,
            30));

        foreach (var session in sessions)
        {
            var intervalMinutes = EffectiveIntervalMinutes(session);
            var reminderStartsAt = session.StartTime!.Value.AddHours(-session.ReminderLeadHours);
            session.NextReminderAt ??= session.LastReminderAt?.AddMinutes(intervalMinutes) ?? reminderStartsAt;

            var capacity = session.TeamCount * session.TeamSize;
            var playerCount = counts.GetValueOrDefault(session.Id);
            if (session.ReminderLastKnownPlayerCount is not null &&
                session.ReminderLastKnownPlayerCount >= capacity &&
                playerCount < capacity &&
                session.StartTime.Value - now <= urgentWindow)
            {
                var urgentAt = now.Add(urgentDelay);
                if (session.NextReminderAt is null || session.NextReminderAt > urgentAt)
                    session.NextReminderAt = urgentAt;
            }

            session.ReminderLastKnownPlayerCount = playerCount;
        }
        await db.SaveChangesAsync(cancellationToken);

        var groups = sessions
            .GroupBy(session => new { session.ZaloConnection!.AccountZaloId, session.ZaloGroupId })
            .ToList();
        var sentCount = 0;
        var failedCount = 0;
        var skippedCount = 0;

        foreach (var group in groups)
        {
            var target = group
                .Where(session => counts.GetValueOrDefault(session.Id) < session.TeamCount * session.TeamSize &&
                                  session.NextReminderAt is not null &&
                                  session.NextReminderAt <= now)
                .OrderBy(session => session.StartTime)
                .FirstOrDefault();
            if (target?.StartTime is null || target.NextReminderAt is null)
            {
                skippedCount += 1;
                continue;
            }

            var leaseToken = Guid.NewGuid().ToString("n");
            var dueAt = target.NextReminderAt.Value;
            var claimed = await db.MatchSessions
                .Where(session => session.Id == target.Id &&
                                  session.ReminderEnabled &&
                                  session.NextReminderAt != null &&
                                  session.NextReminderAt <= now &&
                                  (session.ReminderLeaseUntil == null || session.ReminderLeaseUntil < now))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(session => session.ReminderLeaseToken, leaseToken)
                    .SetProperty(session => session.ReminderLeaseUntil, now.AddMinutes(5)), cancellationToken);
            if (claimed == 0)
            {
                skippedCount += 1;
                continue;
            }

            var capacity = target.TeamCount * target.TeamSize;
            var playerCount = counts.GetValueOrDefault(target.Id);
            var missing = capacity - playerCount;
            var mentionLabel = "@all";
            var location = string.IsNullOrWhiteSpace(target.Location) ? string.Empty : $" tại {target.Location}";
            var factualReminder = $"Nhắc lịch {target.Name}: còn thiếu {missing} slot ({playerCount}/{capacity}). Trận lúc {FormatVietnamTime(target.StartTime.Value)}{location}.";
            var reminderBody = factualReminder;
            if (ai.IsConfigured && configuration.GetValue("ZaloBot:AiStyleEnabled", true))
            {
                var rewritten = await ai.RewriteFactualAnswerAsync(
                    new ZaloAiRewriteContext(
                        "Viết lời nhắc tự động cho cả nhóm khi trận vẫn còn thiếu người.",
                        "Cả nhóm",
                        ZaloBotIntent.ScheduleReminder,
                        factualReminder),
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(rewritten)) reminderBody = rewritten.Trim();
            }
            if (reminderBody.StartsWith(mentionLabel, StringComparison.OrdinalIgnoreCase))
                reminderBody = reminderBody[mentionLabel.Length..].TrimStart();
            var message = $"{mentionLabel} {reminderBody}";
            var idempotencyKey = $"reminder:{target.Id}:{dueAt.ToUnixTimeSeconds()}";

            try
            {
                await bridge.SendGroupMessageAsync(
                    target.ZaloConnection!.AccountZaloId,
                    target.ZaloGroupId!,
                    message,
                    [new BridgeOutgoingMention("-1", 0, mentionLabel.Length)],
                    idempotencyKey: idempotencyKey);

                var intervalMinutes = EffectiveIntervalMinutes(target);
                await db.MatchSessions
                    .Where(session => session.Id == target.Id && session.ReminderLeaseToken == leaseToken)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(session => session.LastReminderAt, now)
                        .SetProperty(session => session.NextReminderAt,
                            target.ReminderRepeats ? now.AddMinutes(intervalMinutes) : (DateTimeOffset?)null)
                        .SetProperty(session => session.ReminderEnabled, target.ReminderRepeats)
                        .SetProperty(session => session.ReminderLeaseToken, (string?)null)
                        .SetProperty(session => session.ReminderLeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(session => session.ReminderFailureCount, 0)
                        .SetProperty(session => session.LastReminderError, (string?)null), cancellationToken);

                if (!await db.ZaloGroupMessages.AsNoTracking()
                        .AnyAsync(item => item.ZaloConnectionId == target.ZaloConnectionId &&
                                          item.MessageId == idempotencyKey, cancellationToken))
                {
                    db.ZaloGroupMessages.Add(new ZaloGroupMessage
                    {
                        ZaloConnectionId = target.ZaloConnectionId!,
                        GroupId = target.ZaloGroupId!,
                        MessageId = idempotencyKey,
                        SenderId = target.ZaloConnection.AccountZaloId,
                        SenderName = target.ZaloConnection.DisplayName,
                        Content = message,
                        IsFromBot = true,
                        SentAt = now,
                        ReceivedAt = now
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }
                sentCount += 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failedCount += 1;
                var retryMinutes = Math.Clamp(configuration.GetValue("Scheduler:RetryMinutes", 10), 5, 60);
                var error = Truncate(exception.Message, 1000);
                await db.MatchSessions
                    .Where(session => session.Id == target.Id && session.ReminderLeaseToken == leaseToken)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(session => session.NextReminderAt, now.AddMinutes(retryMinutes))
                        .SetProperty(session => session.ReminderLeaseToken, (string?)null)
                        .SetProperty(session => session.ReminderLeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(session => session.ReminderFailureCount, session => session.ReminderFailureCount + 1)
                        .SetProperty(session => session.LastReminderError, error), cancellationToken);
                logger.LogWarning(exception, "Could not send reminder for session {SessionId}", target.Id);
            }
        }

        return new ReminderRunResult(
            naturalScheduleResult.GroupCount + groups.Count,
            naturalScheduleResult.SentCount + sentCount,
            naturalScheduleResult.FailedCount + failedCount,
            naturalScheduleResult.SkippedCount + skippedCount);
    }

    private async Task<ReminderRunResult> SendDueNaturalSchedulesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await db.ZaloReminderSchedules
            .Where(schedule => schedule.Enabled &&
                               schedule.Session.StartTime != null &&
                               schedule.Session.StartTime <= now)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(schedule => schedule.Enabled, false)
                .SetProperty(schedule => schedule.LeaseToken, (string?)null)
                .SetProperty(schedule => schedule.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(schedule => schedule.UpdatedAt, now), cancellationToken);

        var schedules = await db.ZaloReminderSchedules
            .Include(schedule => schedule.Session)
            .ThenInclude(session => session.ZaloConnection)
            .Where(schedule => schedule.Enabled &&
                               schedule.NextRunAt <= now &&
                               (schedule.LeaseUntil == null || schedule.LeaseUntil < now) &&
                               schedule.Session.BotEnabled &&
                               schedule.Session.Status != SessionStatus.Cancelled &&
                               schedule.Session.ZaloConnectionId != null &&
                               schedule.Session.ZaloGroupId != null &&
                               schedule.Session.ZaloConnection != null &&
                               schedule.Session.ZaloConnection.Status == ZaloConnectionStatus.Connected)
            .OrderBy(schedule => schedule.NextRunAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (schedules.Count == 0) return new ReminderRunResult(0, 0, 0, 0);

        var counts = await GetEffectiveSlotCountsAsync(
            schedules.Select(schedule => schedule.SessionId).Distinct(StringComparer.Ordinal).ToList(),
            cancellationToken);
        var sent = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var schedule in schedules)
        {
            var leaseToken = Guid.NewGuid().ToString("n");
            var dueAt = schedule.NextRunAt;
            var claimed = await db.ZaloReminderSchedules
                .Where(item => item.Id == schedule.Id &&
                               item.Enabled &&
                               item.NextRunAt <= now &&
                               (item.LeaseUntil == null || item.LeaseUntil < now))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(item => item.LeaseToken, leaseToken)
                    .SetProperty(item => item.LeaseUntil, now.AddMinutes(5)), cancellationToken);
            if (claimed == 0)
            {
                skipped += 1;
                continue;
            }

            var session = schedule.Session;
            var capacity = session.TeamCount * session.TeamSize;
            var slotCount = counts.GetValueOrDefault(session.Id);
            if (schedule.OnlyIfMissingSlots && slotCount >= capacity)
            {
                if (schedule.StopWhenFull || ZaloNaturalCommandParser.RequestsStopWhenFull(schedule.Message))
                    await DisableNaturalScheduleAsync(schedule.Id, leaseToken, now, cancellationToken);
                else
                    await CompleteNaturalScheduleAsync(schedule, leaseToken, now, sentSuccessfully: false, cancellationToken);
                skipped += 1;
                continue;
            }

            var storedMessage = ZaloNaturalCommandParser.SanitizeReminderMessage(
                schedule.Message,
                schedule.Message ?? string.Empty);
            var missingSlots = Math.Max(0, capacity - slotCount);
            var factualBody = string.IsNullOrWhiteSpace(storedMessage)
                ? $"Nhắc vote {session.Name}: hiện còn thiếu {missingSlots} slot ({slotCount}/{capacity}). Trận lúc {(session.StartTime is null ? "chưa chốt giờ" : FormatVietnamTime(session.StartTime.Value))}{(string.IsNullOrWhiteSpace(session.Location) ? string.Empty : $" tại {session.Location}")}. Mọi người vào vote giúp nhé!"
                : schedule.OnlyIfMissingSlots
                    ? $"{session.Name} hiện còn thiếu {missingSlots} slot ({slotCount}/{capacity}). {storedMessage}"
                    : $"{session.Name}: {storedMessage}";
            var body = factualBody;
            if (ai.IsConfigured && configuration.GetValue("ZaloBot:AiStyleEnabled", true))
            {
                var rewritten = await ai.RewriteFactualAnswerAsync(
                    new ZaloAiRewriteContext(
                        "Viết lời nhắc tự nhiên cho đúng đối tượng nhận của buổi bóng chuyền.",
                        "Nhóm bóng chuyền",
                        ZaloBotIntent.ScheduleReminder,
                        factualBody),
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(rewritten)) body = rewritten.Trim();
            }
            var audience = await BuildAudienceMessageAsync(schedule, body, cancellationToken);
            var idempotencyKey = $"natural-reminder:{schedule.Id}:{dueAt.ToUnixTimeSeconds()}";
            try
            {
                await bridge.SendGroupMessageAsync(
                    session.ZaloConnection!.AccountZaloId,
                    session.ZaloGroupId!,
                    audience.Message,
                    audience.Mentions,
                    idempotencyKey: idempotencyKey);
                await CompleteNaturalScheduleAsync(schedule, leaseToken, now, sentSuccessfully: true, cancellationToken);
                if (!await db.ZaloGroupMessages.AsNoTracking().AnyAsync(item =>
                        item.ZaloConnectionId == session.ZaloConnectionId && item.MessageId == idempotencyKey,
                        cancellationToken))
                {
                    db.ZaloGroupMessages.Add(new ZaloGroupMessage
                    {
                        ZaloConnectionId = session.ZaloConnectionId!,
                        GroupId = session.ZaloGroupId!,
                        MessageId = idempotencyKey,
                        SenderId = session.ZaloConnection.AccountZaloId,
                        SenderName = session.ZaloConnection.DisplayName,
                        Content = audience.Message,
                        IsFromBot = true,
                        SentAt = now,
                        ReceivedAt = now
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }
                sent += 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed += 1;
                var retryMinutes = Math.Clamp(configuration.GetValue("Scheduler:RetryMinutes", 10), 5, 60);
                await db.ZaloReminderSchedules
                    .Where(item => item.Id == schedule.Id && item.LeaseToken == leaseToken)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(item => item.NextRunAt, now.AddMinutes(retryMinutes))
                        .SetProperty(item => item.LeaseToken, (string?)null)
                        .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(item => item.FailureCount, item => item.FailureCount + 1)
                        .SetProperty(item => item.LastError, Truncate(exception.Message, 1000))
                        .SetProperty(item => item.UpdatedAt, now), cancellationToken);
                logger.LogWarning(exception, "Could not send natural reminder {ScheduleId} for session {SessionId}", schedule.Id, session.Id);
            }
        }

        return new ReminderRunResult(
            schedules.Select(item => item.Session.ZaloGroupId).Distinct(StringComparer.Ordinal).Count(),
            sent,
            failed,
            skipped);
    }

    private async Task CompleteNaturalScheduleAsync(
        ZaloReminderSchedule schedule,
        string leaseToken,
        DateTimeOffset now,
        bool sentSuccessfully,
        CancellationToken cancellationToken)
    {
        var repeats = schedule.Repeats && schedule.IntervalMinutes is >= 5 and <= 10_080;
        var nextRunAt = repeats
            ? now.AddMinutes(schedule.IntervalMinutes!.Value)
            : schedule.NextRunAt;
        var continues = repeats &&
                        (schedule.Session.StartTime is null || nextRunAt < schedule.Session.StartTime.Value);
        await db.ZaloReminderSchedules
            .Where(item => item.Id == schedule.Id && item.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.LastRunAt, sentSuccessfully ? now : schedule.LastRunAt)
                .SetProperty(item => item.NextRunAt, nextRunAt)
                .SetProperty(item => item.Enabled, continues)
                .SetProperty(item => item.LeaseToken, (string?)null)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.FailureCount, 0)
                .SetProperty(item => item.LastError, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    private async Task DisableNaturalScheduleAsync(
        string scheduleId,
        string leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await db.ZaloReminderSchedules
            .Where(item => item.Id == scheduleId && item.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.Enabled, false)
                .SetProperty(item => item.LeaseToken, (string?)null)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.FailureCount, 0)
                .SetProperty(item => item.LastError, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    private async Task<AudienceMessage> BuildAudienceMessageAsync(
        ZaloReminderSchedule schedule,
        string body,
        CancellationToken cancellationToken)
    {
        if (schedule.Audience == ZaloReminderAudience.All)
            return new AudienceMessage($"@all {body}", [new BridgeOutgoingMention("-1", 0, 4)]);

        var players = await db.SessionPlayers
            .AsNoTracking()
            .Include(player => player.PlayerProfile)
            .Where(player => player.SessionId == schedule.SessionId &&
                             player.IsPresent &&
                             player.PlayerProfile != null &&
                             player.PlayerProfile.ZaloUserId != "")
            .OrderBy(player => player.DisplayName)
            .ToListAsync(cancellationToken);
        if (players.Count == 0)
            return new AudienceMessage($"@all {body}", [new BridgeOutgoingMention("-1", 0, 4)]);

        var builder = new System.Text.StringBuilder();
        var mentions = new List<BridgeOutgoingMention>();
        foreach (var player in players.DistinctBy(item => item.PlayerProfile!.ZaloUserId, StringComparer.Ordinal))
        {
            if (builder.Length > 0) builder.Append(' ');
            var label = $"@{player.DisplayName.TrimStart('@')}";
            var position = builder.Length;
            builder.Append(label);
            mentions.Add(new BridgeOutgoingMention(player.PlayerProfile!.ZaloUserId, position, label.Length));
        }
        builder.Append(' ').Append(body);
        return new AudienceMessage(builder.ToString(), mentions);
    }

    private async Task RefreshPollRostersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var refreshMinutes = Math.Clamp(configuration.GetValue("Scheduler:PollRefreshMinutes", 20), 10, 180);
        var cutoff = now.AddMinutes(-refreshMinutes);
        var candidates = await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.BotEnabled &&
                              (session.ReminderEnabled ||
                               db.ZaloReminderSchedules.Any(schedule =>
                                   schedule.SessionId == session.Id &&
                                   schedule.Enabled &&
                                   schedule.OnlyIfMissingSlots)) &&
                              session.StartTime != null &&
                              session.StartTime > now &&
                              session.ZaloConnectionId != null &&
                              session.ZaloGroupId != null &&
                              session.Status != SessionStatus.Cancelled &&
                              db.PollImports.Any(import => import.SessionId == session.Id))
            .Select(session => new
            {
                session.Id,
                session.AdminUserId,
                LastImportedAt = db.PollImports
                    .Where(import => import.SessionId == session.Id)
                    .Max(import => (DateTimeOffset?)import.ImportedAt)
            })
            .Where(item => item.LastImportedAt == null || item.LastImportedAt <= cutoff)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            try
            {
                var result = await zaloIntegration.SyncLatestPollAsync(candidate.AdminUserId, candidate.Id);
                if (!result.IsSuccess)
                    logger.LogDebug("Poll refresh skipped for session {SessionId}: {Reason}", candidate.Id, result.Error);
                else
                    await waitlists.ProcessVacanciesAsync(candidate.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not refresh poll roster for reminder session {SessionId}", candidate.Id);
            }
        }
    }

    private async Task<Dictionary<string, int>> GetEffectiveSlotCountsAsync(
        IReadOnlyList<string> sessionIds,
        CancellationToken cancellationToken)
    {
        var regularCounts = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => sessionIds.Contains(player.SessionId) &&
                             player.IsPresent &&
                             !player.IsInsideSharedSlot)
            .GroupBy(player => player.SessionId)
            .Select(group => new { SessionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.SessionId, item => item.Count, cancellationToken);
        var sharedCounts = await db.DraftSlots
            .AsNoTracking()
            .Where(slot => sessionIds.Contains(slot.SessionId) &&
                           slot.Type == DraftSlotType.Shared &&
                           slot.Players.Any(link => link.SessionPlayer.IsPresent))
            .GroupBy(slot => slot.SessionId)
            .Select(group => new { SessionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.SessionId, item => item.Count, cancellationToken);

        return sessionIds.Distinct(StringComparer.Ordinal)
            .ToDictionary(
                id => id,
                id => regularCounts.GetValueOrDefault(id) + sharedCounts.GetValueOrDefault(id),
                StringComparer.Ordinal);
    }

    private static int EffectiveIntervalMinutes(MatchSession session) =>
        session.ReminderIntervalMinutes > 0
            ? Math.Clamp(session.ReminderIntervalMinutes, 5, 10_080)
            : Math.Clamp(session.ReminderIntervalHours * 60, 60, 10_080);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

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
                var result = await scope.ServiceProvider.GetRequiredService<ZaloReminderService>()
                    .SendDueRemindersAsync(stoppingToken);
                if (result.SentCount > 0 || result.FailedCount > 0)
                {
                    logger.LogInformation(
                        "Zalo reminder cycle completed Groups={Groups} Sent={Sent} Failed={Failed}",
                        result.GroupCount,
                        result.SentCount,
                        result.FailedCount);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Zalo reminder cycle failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
