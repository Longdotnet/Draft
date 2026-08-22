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
    internal const string SelectedIntentPrefix = "KeepRecruiting:";

    internal static TimeSpan GetCooldown(IConfiguration configuration) =>
        TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("ZaloBot:DraftAutopilot:KeepRecruitingBroadcastCooldownMinutes", 60),
            10,
            180));

    internal static string MessagePrefix(string sessionId) =>
        $"{MessageIdPrefix}{sessionId}:";

    internal static string SelectedIntent(string sessionId) => $"{SelectedIntentPrefix}{sessionId}";

    internal static string? TryReadSessionId(string? selectedIntent)
    {
        if (string.IsNullOrWhiteSpace(selectedIntent) ||
            !selectedIntent.StartsWith(SelectedIntentPrefix, StringComparison.Ordinal))
            return null;
        var value = selectedIntent[SelectedIntentPrefix.Length..].Trim();
        return value.Length == 0 ? null : value;
    }

    internal static string BuildIdempotencyKey(
        string sessionId,
        DateTimeOffset now,
        TimeSpan cooldown)
    {
        var bucketSeconds = Math.Max(60L, (long)cooldown.TotalSeconds);
        var bucket = Math.Max(0L, now.ToUnixTimeSeconds()) / bucketSeconds;
        return $"{MessagePrefix(sessionId)}{bucket}";
    }

    internal static string? BuildMessage(
        ZaloDraftReadinessSnapshot readiness,
        int activeSlotRiskCount = 0,
        bool guestSignupOpen = true)
    {
        if (readiness.EffectiveSlotCount >= readiness.Capacity && activeSlotRiskCount <= 0)
            return null;

        var roster = readiness.PresentPlayerCount == readiness.EffectiveSlotCount
            ? $"{readiness.EffectiveSlotCount}/{readiness.Capacity}"
            : $"{readiness.PresentPlayerCount} người / {readiness.EffectiveSlotCount} effective slot (mốc {readiness.Capacity})";
        var guestHint = guestSignupOpen
            ? " Có kéo bạn ngoài group thì reply thẳng tin này `+1` hoặc `+2`; bạn đó không cần ở trong group Zalo."
            : string.Empty;

        if (activeSlotRiskCount > 0 && readiness.EffectiveSlotCount >= readiness.Capacity)
        {
            var riskLabel = activeSlotRiskCount == 1 ? "1 slot" : $"{activeSlotRiskCount} slot";
            return $"@all Kèo {readiness.SessionName} poll đang {roster} nhưng có {riskLabel} báo pass/huỷ đang cần người thay 👋 Ai chưa vote hoặc giờ sắp xếp vào được thì vào poll chốt giúp nha.{guestHint} Trưởng/phó đang chọn tiếp tục kiếm thêm; slot sạch lại bot tự ngưng réo.";
        }

        var missing = Math.Max(1, readiness.Capacity - readiness.EffectiveSlotCount);
        var slotLabel = missing == 1 ? "1 slot" : $"{missing} slot";
        var riskNote = activeSlotRiskCount > 0
            ? $"; đồng thời còn {activeSlotRiskCount} slot pass/huỷ chưa xử lý xong"
            : string.Empty;
        return $"@all Kèo {readiness.SessionName} đang {roster}, còn thiếu {slotLabel}{riskNote} 👋 Ai chưa vote hoặc giờ sắp xếp chơi được thì vào poll chốt giúp nha.{guestHint} Trưởng/phó đang chọn tiếp tục kiếm thêm; đủ người và slot sạch thì bot tự ngưng réo.";
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
            var selectedIntent = ZaloKeepRecruitingBroadcastPolicy.SelectedIntent(session.Id);
            var recentBroadcast = await db.ZaloGroupMessages
                .AsNoTracking()
                .AnyAsync(message =>
                    message.ZaloConnectionId == session.ZaloConnectionId &&
                    message.GroupId == session.ZaloGroupId &&
                    message.IsFromBot &&
                    message.ReplyOutcome == ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome &&
                    message.SelectedIntent == selectedIntent &&
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

            // A named guest may have joined the group since the last cycle. Collapse
            // that manual placeholder onto the unique poll-backed player before the
            // recruitment capacity check so one human never consumes two slots.
            await new ZaloGuestIdentityReconciler(db)
                .ReconcileAsync(session.Id, cancellationToken);

            var readiness = await new ZaloDraftReadinessService(db)
                .BuildAsync(session.Id, now, cancellationToken);
            if (readiness is null) continue;
            var activeSlotRisks = await CountActiveSlotRisksAsync(session, cancellationToken);

            // Keep the organizer's direction durable when the roster becomes full.
            // A full clean roster suppresses messages, but if somebody later passes a
            // slot the same KeepRecruiting decision can resume without forcing the
            // organizer to repeat themselves.
            if (readiness.EffectiveSlotCount >= readiness.Capacity && activeSlotRisks == 0)
                continue;

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
                    activeSlotRisks,
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
        int activeSlotRiskCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("ZaloBot:DraftAutopilot:KeepRecruitingBroadcastEnabled", true))
            return ZaloKeepRecruitingBroadcastResult.Disabled;

        var settings = DraftAutopilotSettings.FromConfiguration(configuration);
        if (session.StartTime is { } start &&
            start <= now.AddMinutes(settings.StopNudgingMinutesBeforeStart))
            return ZaloKeepRecruitingBroadcastResult.WindowClosed;

        var guestSignupOpen = ZaloRecruitmentGuestGatePolicy.IsAddWindowOpen(
            session.StartTime,
            now,
            configuration);
        var message = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(
            readiness,
            activeSlotRiskCount,
            guestSignupOpen);
        if (message is null)
            return ZaloKeepRecruitingBroadcastResult.NotNeeded;

        var connection = session.ZaloConnection;
        if (connection is null ||
            connection.Status != ZaloConnectionStatus.Connected ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return ZaloKeepRecruitingBroadcastResult.ConnectionUnavailable;

        var cooldown = ZaloKeepRecruitingBroadcastPolicy.GetCooldown(configuration);
        var selectedIntent = ZaloKeepRecruitingBroadcastPolicy.SelectedIntent(session.Id);
        var recent = await db.ZaloGroupMessages
            .AsNoTracking()
            .AnyAsync(item =>
                item.ZaloConnectionId == connection.Id &&
                item.GroupId == session.ZaloGroupId &&
                item.IsFromBot &&
                item.ReplyOutcome == ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome &&
                item.SelectedIntent == selectedIntent &&
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
            var send = await bridge.SendGroupMessageAsync(
                connection.AccountZaloId,
                session.ZaloGroupId!,
                message,
                [new BridgeOutgoingMention("-1", 0, 4)],
                idempotencyKey: idempotencyKey);
            var providerReplyId = NormalizeProviderMessageId(send.MessageId);
            var persistedReplyId = providerReplyId ?? $"local:{idempotencyKey}";

            if (!await db.ZaloGroupMessages
                    .AsNoTracking()
                    .AnyAsync(item =>
                        item.ZaloConnectionId == connection.Id &&
                        item.MessageId == persistedReplyId,
                        cancellationToken))
            {
                var stored = new ZaloGroupMessage
                {
                    ZaloConnectionId = connection.Id,
                    GroupId = session.ZaloGroupId!,
                    MessageId = persistedReplyId,
                    SenderId = connection.AccountZaloId,
                    SenderName = connection.DisplayName,
                    Content = message,
                    IsFromBot = true,
                    SentAt = now,
                    ReceivedAt = now,
                    FirstObservedAt = now,
                    LastObservedAt = now,
                    SelectedIntent = selectedIntent,
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

            if (providerReplyId is not null)
            {
                await new ZaloMessageGraphStore(db).RememberOutboundAsync(
                    connection.Id,
                    session.ZaloGroupId!,
                    providerReplyId,
                    null,
                    cancellationToken);
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
