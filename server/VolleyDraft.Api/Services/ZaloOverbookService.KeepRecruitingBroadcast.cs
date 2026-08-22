using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal enum ZaloKeepRecruitingBroadcastResult
{
    Sent,
    Cooldown,
    Disabled,
    WindowClosed,
    ConnectionUnavailable,
    NotNeeded,
    Failed
}

internal static class ZaloKeepRecruitingBroadcastPolicy
{
    internal const string ReplyOutcome = "draft_keep_recruiting_broadcast";
    internal const string MessageIdPrefix = "draft-keep-recruiting:";

    internal static TimeSpan GetCooldown(IConfiguration configuration) =>
        TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("ZaloBot:DraftAutopilot:KeepRecruitingBroadcastCooldownMinutes", 60),
            10,
            180));

    internal static string MessagePrefix(string sessionId) =>
        $"{MessageIdPrefix}{sessionId}:";

    internal static string BuildIdempotencyKey(
        string sessionId,
        DateTimeOffset now,
        TimeSpan cooldown)
    {
        var bucketSeconds = Math.Max(60L, (long)cooldown.TotalSeconds);
        var bucket = Math.Max(0L, now.ToUnixTimeSeconds()) / bucketSeconds;
        return $"{MessagePrefix(sessionId)}{bucket}";
    }

    internal static string? BuildMessage(ZaloDraftReadinessSnapshot readiness)
    {
        if (readiness.EffectiveSlotCount >= readiness.Capacity)
            return null;

        var missing = Math.Max(1, readiness.Capacity - readiness.EffectiveSlotCount);
        var roster = readiness.PresentPlayerCount == readiness.EffectiveSlotCount
            ? $"{readiness.EffectiveSlotCount}/{readiness.Capacity}"
            : $"{readiness.PresentPlayerCount} người / {readiness.EffectiveSlotCount} effective slot (mốc {readiness.Capacity})";
        var slotLabel = missing == 1 ? "1 slot" : $"{missing} slot";

        return $"@all Kèo {readiness.SessionName} đang {roster}, còn thiếu {slotLabel} 👋 Ai chưa vote hoặc giờ sắp xếp chơi được thì vào poll chốt giúp nha. Trưởng/phó đang chọn tiếp tục kiếm thêm; đủ người bot tự ngưng réo.";
    }
}

public sealed partial class ZaloOverbookService
{
    public async Task<int> ProcessKeepRecruitingBroadcastsDueAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = DraftAutopilotSettings.FromConfiguration(configuration);
        if (!settings.Enabled ||
            !settings.ProactiveEnabled ||
            !configuration.GetValue("ZaloBot:DraftAutopilot:KeepRecruitingBroadcastEnabled", true))
            return 0;

        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Where(item =>
                item.BotEnabled &&
                item.ZaloConnectionId != null &&
                item.ZaloGroupId != null &&
                item.ZaloConnection != null &&
                item.StartTime != null &&
                item.StartTime > now.AddMinutes(settings.StopNudgingMinutesBeforeStart) &&
                item.StartTime <= now.AddHours(36) &&
                (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection))
            .OrderBy(item => item.StartTime)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0) return 0;

        var decisionStore = new ZaloDraftPreparationDecisionStore(db);
        var keepRecruiting = new List<MatchSession>();
        foreach (var session in sessions)
        {
            var decision = await decisionStore.GetAsync(session.Id, cancellationToken);
            if (decision?.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting)
                keepRecruiting.Add(session);
        }

        var candidates = keepRecruiting
            .GroupBy(
                item => $"{item.ZaloConnectionId}:{item.ZaloGroupId}",
                StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.StartTime).First())
            .OrderBy(item => item.StartTime)
            .Take(30)
            .ToList();
        if (candidates.Count == 0) return 0;

        var reminderStore = new ZaloDraftPreparationReminderStore(db);
        var sent = 0;
        foreach (var session in candidates)
        {
            if (sent >= settings.MaxSendsPerCycle) break;

            var cooldown = ZaloKeepRecruitingBroadcastPolicy.GetCooldown(configuration);
            var prefix = ZaloKeepRecruitingBroadcastPolicy.MessagePrefix(session.Id);
            var recentBroadcast = await db.ZaloGroupMessages
                .AsNoTracking()
                .AnyAsync(message =>
                    message.ZaloConnectionId == session.ZaloConnectionId &&
                    message.GroupId == session.ZaloGroupId &&
                    message.IsFromBot &&
                    message.ReplyOutcome == ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome &&
                    message.MessageId.StartsWith(prefix) &&
                    message.SentAt >= now - cooldown,
                    cancellationToken);

            var bucket = ZaloDraftPreparationReminderPolicy.GetDueBucket(
                session.StartTime!.Value,
                now,
                settings.StopNudgingMinutesBeforeStart);
            var previousReminder = bucket is null
                ? null
                : await reminderStore.GetAsync(session.Id, cancellationToken);
            var dueLeaderBucket = bucket is not null &&
                                  !string.Equals(previousReminder?.LastBucketKey, bucket.Key, StringComparison.Ordinal);

            if (recentBroadcast && !dueLeaderBucket)
                continue;

            var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
            if (!sync.Success)
            {
                logger.LogWarning(
                    "Keep-recruiting broadcast postponed because linked poll sync failed Session={SessionId} Reason={Reason}",
                    session.Id,
                    sync.Error);
                continue;
            }

            var readiness = await new ZaloDraftReadinessService(db)
                .BuildAsync(session.Id, now, cancellationToken);
            if (readiness is null) continue;

            if (readiness.EffectiveSlotCount >= readiness.Capacity)
            {
                await decisionStore.ClearAsync(session.Id, cancellationToken);
                continue;
            }

            var activeSlotRisks = await CountActiveSlotRisksAsync(session, cancellationToken);
            ZaloKeepRecruitingBroadcastResult result;
            if (recentBroadcast)
            {
                result = ZaloKeepRecruitingBroadcastResult.Cooldown;
            }
            else
            {
                result = await TrySendKeepRecruitingBroadcastAsync(
                    session,
                    readiness,
                    now,
                    cancellationToken);
            }

            if (result == ZaloKeepRecruitingBroadcastResult.Sent)
                sent += 1;

            // This lane owns the group-wide recruitment nudge while KeepRecruiting is
            // active. Mark the same V2 bucket handled so the next call in the worker
            // does not also tag one or two leaders with a duplicate recruitment note.
            if (dueLeaderBucket &&
                result is ZaloKeepRecruitingBroadcastResult.Sent or ZaloKeepRecruitingBroadcastResult.Cooldown)
            {
                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket!.Key,
                    readiness.EffectiveSlotCount,
                    activeSlotRisks,
                    readiness.Fingerprint,
                    result == ZaloKeepRecruitingBroadcastResult.Sent ? now : null,
                    cancellationToken);
            }
        }

        return sent;
    }

    private async Task<ZaloKeepRecruitingBroadcastResult> TrySendKeepRecruitingBroadcastAsync(
        MatchSession session,
        ZaloDraftReadinessSnapshot readiness,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("ZaloBot:DraftAutopilot:KeepRecruitingBroadcastEnabled", true))
            return ZaloKeepRecruitingBroadcastResult.Disabled;

        var settings = DraftAutopilotSettings.FromConfiguration(configuration);
        if (session.StartTime is { } start &&
            start <= now.AddMinutes(settings.StopNudgingMinutesBeforeStart))
            return ZaloKeepRecruitingBroadcastResult.WindowClosed;

        var message = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(readiness);
        if (message is null)
            return ZaloKeepRecruitingBroadcastResult.NotNeeded;

        var connection = session.ZaloConnection;
        if (connection is null ||
            connection.Status != ZaloConnectionStatus.Connected ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return ZaloKeepRecruitingBroadcastResult.ConnectionUnavailable;

        var cooldown = ZaloKeepRecruitingBroadcastPolicy.GetCooldown(configuration);
        var prefix = ZaloKeepRecruitingBroadcastPolicy.MessagePrefix(session.Id);
        var recent = await db.ZaloGroupMessages
            .AsNoTracking()
            .AnyAsync(item =>
                item.ZaloConnectionId == connection.Id &&
                item.GroupId == session.ZaloGroupId &&
                item.IsFromBot &&
                item.ReplyOutcome == ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome &&
                item.MessageId.StartsWith(prefix) &&
                item.SentAt >= now - cooldown,
                cancellationToken);
        if (recent)
            return ZaloKeepRecruitingBroadcastResult.Cooldown;

        var idempotencyKey = ZaloKeepRecruitingBroadcastPolicy.BuildIdempotencyKey(
            session.Id,
            now,
            cooldown);
        try
        {
            await bridge.SendGroupMessageAsync(
                connection.AccountZaloId,
                session.ZaloGroupId!,
                message,
                [new BridgeOutgoingMention("-1", 0, 4)],
                idempotencyKey: idempotencyKey);

            if (!await db.ZaloGroupMessages
                    .AsNoTracking()
                    .AnyAsync(item =>
                        item.ZaloConnectionId == connection.Id &&
                        item.MessageId == idempotencyKey,
                        cancellationToken))
            {
                var stored = new ZaloGroupMessage
                {
                    ZaloConnectionId = connection.Id,
                    GroupId = session.ZaloGroupId!,
                    MessageId = idempotencyKey,
                    SenderId = connection.AccountZaloId,
                    SenderName = connection.DisplayName,
                    Content = message,
                    IsFromBot = true,
                    SentAt = now,
                    ReceivedAt = now,
                    FirstObservedAt = now,
                    LastObservedAt = now,
                    ReplyOutcome = ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome
                };
                db.ZaloGroupMessages.Add(stored);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    db.Entry(stored).State = EntityState.Detached;
                }
            }

            return ZaloKeepRecruitingBroadcastResult.Sent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Keep-recruiting @all broadcast failed Session={SessionId} Group={GroupId}",
                session.Id,
                session.ZaloGroupId);
            return ZaloKeepRecruitingBroadcastResult.Failed;
        }
    }
}
