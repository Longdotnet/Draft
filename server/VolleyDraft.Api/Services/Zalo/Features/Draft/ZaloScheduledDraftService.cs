using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloScheduledDraftCycleResult(int Drafted, int Failed, int Skipped);

internal enum ZaloScheduledDraftCommandKind
{
    Enable,
    Disable,
    SkipSession,
    DeferSession
}

internal sealed record ZaloScheduledDraftCommand(
    ZaloScheduledDraftCommandKind Kind,
    int? LocalMinuteOfDay = null);

public sealed class ZaloScheduledDraftService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    ZaloIntegrationService zaloIntegration,
    ZaloDraftReadinessService readiness,
    ILoggerFactory loggerFactory,
    ILogger<ZaloScheduledDraftService> logger)
{
    private static readonly TimeSpan FallbackVietnamOffset = TimeSpan.FromHours(7);
    private const string LateReminderRescheduledReason = "late_reminder_safe_window";
    private const string ReminderDeliveryAttemptedReason = "reminder_delivery_attempted";
    private const string ScheduledOutcomeUnconfirmedReason = "scheduled_execution_outcome_unconfirmed";

    public async Task<(bool Handled, string? Reply)> TryApplyCommandAsync(
        string connectionId,
        string groupId,
        string senderZaloUserId,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseCommand(content, out var command))
            return (false, null);

        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions.AsNoTracking()
            .Where(session =>
                session.ZaloConnectionId == connectionId &&
                session.ZaloGroupId == groupId &&
                session.BotEnabled &&
                session.Status != SessionStatus.Cancelled &&
                session.StartTime != null &&
                session.StartTime > now)
            .OrderBy(session => session.StartTime)
            .Take(5)
            .ToListAsync(cancellationToken);
        var session = sessions.FirstOrDefault();
        if (session is null)
            return (true, "Nhóm chưa có buổi chơi sắp tới để xác minh quyền hoặc áp dụng lịch tự draft.");

        var authorization = await zaloIntegration.GetGroupRoleAuthorizationAsync(
            session.AdminUserId,
            session.Id,
            senderZaloUserId);
        if (!authorization.IsSuccess || authorization.Value is null)
            return (true, "Tui chưa xác minh được quyền trưởng/phó nên chưa đổi lịch tự draft.");
        if (!authorization.Value.CanOperateBot)
            return (true, "Chỉ trưởng nhóm hoặc phó nhóm mới đổi lịch tự draft.");

        var policy = await db.ZaloScheduledDraftPolicies.SingleOrDefaultAsync(
            item => item.ZaloConnectionId == connectionId && item.GroupId == groupId,
            cancellationToken);
        if (policy is null)
        {
            policy = new ZaloScheduledDraftPolicy
            {
                ZaloConnectionId = connectionId,
                GroupId = groupId,
                Enabled = false,
                LocalDraftMinuteOfDay = 17 * 60 + 30,
                ReminderMinutes = 30,
                TimeZoneId = "Asia/Ho_Chi_Minh",
                Version = 1,
                UpdatedAt = now
            };
            db.ZaloScheduledDraftPolicies.Add(policy);
        }

        switch (command.Kind)
        {
            case ZaloScheduledDraftCommandKind.Enable:
                policy.Enabled = true;
                policy.LocalDraftMinuteOfDay = command.LocalMinuteOfDay ?? 17 * 60 + 30;
                policy.EnabledByZaloUserId = senderZaloUserId;
                policy.EnabledAt = now;
                policy.Version += 1;
                policy.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return (true,
                    $"Đã bật tự draft lúc {FormatLocalMinute(policy.LocalDraftMinuteOfDay)}. Tui sẽ báo trước {policy.ReminderMinutes} phút; chỉ tự chạy khi roster đủ và readiness hợp lệ.");

            case ZaloScheduledDraftCommandKind.Disable:
                policy.Enabled = false;
                policy.Version += 1;
                policy.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return (true, "Đã tắt tự draft cho nhóm. Draft thủ công vẫn dùng bình thường.");

            case ZaloScheduledDraftCommandKind.SkipSession:
            {
                if (!policy.Enabled)
                    return (true, "Nhóm chưa bật tự draft nên chưa có lịch tự draft để bỏ qua.");
                var activeRun = await db.ZaloScheduledDraftRuns.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.SessionId == session.Id, cancellationToken);
                if (activeRun?.LeaseUntil > now)
                    return (true, "Buổi này đang thực hiện tự draft; không thể bỏ qua khi quá trình đã bắt đầu.");
                await using var commandTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var decision = await GetOrCreateDecisionAsync(session.Id, policy.Version, senderZaloUserId, now, cancellationToken);
                decision.Skip = true;
                decision.DeferredUntil = null;
                decision.PolicyVersion = policy.Version;
                decision.ChangedAt = now;
                decision.ChangedByZaloUserId = senderZaloUserId;
                if (activeRun is not null &&
                    activeRun.State is not (ZaloScheduledDraftRunState.Drafted or ZaloScheduledDraftRunState.AlreadyDrafted))
                {
                    var updated = await db.ZaloScheduledDraftRuns
                        .Where(item => item.Id == activeRun.Id &&
                                       (item.LeaseUntil == null || item.LeaseUntil <= now) &&
                                       item.State != ZaloScheduledDraftRunState.Drafted &&
                                       item.State != ZaloScheduledDraftRunState.AlreadyDrafted)
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(item => item.State, ZaloScheduledDraftRunState.Skipped)
                            .SetProperty(item => item.LastError, "session_skipped_by_authorized_operator")
                            .SetProperty(item => item.UpdatedAt, now), cancellationToken);
                    if (updated == 0)
                        return (true, "Buổi này vừa bắt đầu draft; không thể xác nhận bỏ qua.");
                }
                await db.SaveChangesAsync(cancellationToken);
                await commandTransaction.CommitAsync(cancellationToken);
                return (true, $"Ok, buổi {session.Name} sẽ không tự draft.");
            }

            case ZaloScheduledDraftCommandKind.DeferSession:
            {
                if (!policy.Enabled)
                    return (true, "Nhóm chưa bật tự draft nên chưa có lịch tự draft để hoãn.");
                var minute = command.LocalMinuteOfDay ?? policy.LocalDraftMinuteOfDay;
                var deferred = ResolveLocalTimeForSession(session.StartTime!.Value, minute, policy.TimeZoneId);
                var activeRun = await db.ZaloScheduledDraftRuns.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.SessionId == session.Id, cancellationToken);
                if (activeRun?.LeaseUntil > now)
                    return (true, "Buổi này đang thực hiện tự draft; không thể hoãn khi quá trình đã bắt đầu.");
                if (deferred <= now)
                    return (true, "Giờ hoãn phải nằm sau thời điểm hiện tại.");
                if (deferred >= session.StartTime)
                    return (true, "Giờ hoãn phải trước giờ bắt đầu buổi chơi.");
                var currentDue = activeRun?.DraftDueAt ??
                    ResolveLocalTimeForSession(session.StartTime.Value, policy.LocalDraftMinuteOfDay, policy.TimeZoneId);
                if (deferred <= currentDue)
                    return (true, "Giờ hoãn phải muộn hơn lịch tự draft hiện tại.");

                await using var commandTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var decision = await GetOrCreateDecisionAsync(session.Id, policy.Version, senderZaloUserId, now, cancellationToken);
                decision.Skip = false;
                decision.DeferredUntil = deferred;
                decision.PolicyVersion = policy.Version;
                decision.ChangedAt = now;
                decision.ChangedByZaloUserId = senderZaloUserId;
                if (activeRun is not null &&
                    activeRun.State is not (ZaloScheduledDraftRunState.Drafted or ZaloScheduledDraftRunState.AlreadyDrafted))
                {
                    var updated = await db.ZaloScheduledDraftRuns
                        .Where(item => item.Id == activeRun.Id &&
                                       (item.LeaseUntil == null || item.LeaseUntil <= now) &&
                                       item.State != ZaloScheduledDraftRunState.Drafted &&
                                       item.State != ZaloScheduledDraftRunState.AlreadyDrafted)
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(item => item.DraftDueAt, deferred)
                            .SetProperty(item => item.ReminderDueAt, deferred.AddMinutes(-policy.ReminderMinutes))
                            .SetProperty(item => item.ReminderSentAt, activeRun.ReminderSentAt)
                            .SetProperty(item => item.State,
                                activeRun.ReminderSentAt == null
                                    ? ZaloScheduledDraftRunState.Pending
                                    : ZaloScheduledDraftRunState.ReminderSent)
                            .SetProperty(item => item.LastError, (string?)null)
                            .SetProperty(item => item.UpdatedAt, now), cancellationToken);
                    if (updated == 0)
                        return (true, "Buổi này vừa bắt đầu draft; không thể xác nhận hoãn.");
                }
                await db.SaveChangesAsync(cancellationToken);
                await commandTransaction.CommitAsync(cancellationToken);
                return (true, $"Ok, buổi {session.Name} sẽ hoãn tự draft đến {FormatLocalMinute(minute)}.");
            }
            default:
                return (false, null);
        }
    }

    public async Task<ZaloScheduledDraftCycleResult> RunDueAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var drafted = 0;
        var failed = 0;
        var skipped = 0;

        var pendingResultRuns = await db.ZaloScheduledDraftRuns
            .Include(run => run.Session)
            .ThenInclude(session => session.ZaloConnection)
            .Where(run =>
                (run.State == ZaloScheduledDraftRunState.Drafted ||
                 (run.State == ZaloScheduledDraftRunState.AlreadyDrafted &&
                  run.LastError == ScheduledOutcomeUnconfirmedReason)) &&
                run.ResultMessageSentAt == null)
            .ToListAsync(cancellationToken);
        foreach (var pendingResultRun in pendingResultRuns)
        {
            if (!await TrySendDraftedResultAsync(
                    pendingResultRun.Session,
                    pendingResultRun,
                    cancellationToken))
                failed += 1;
        }

        // An interrupted process can leave its durable attempt marker on a pending
        // run after the draft engine committed teams and before completion was saved.
        // Reconcile those sessions independently of the active-session query, which
        // intentionally excludes Finished sessions.
        var interruptedRuns = await db.ZaloScheduledDraftRuns
            .Include(run => run.Session)
            .ThenInclude(session => session.ZaloConnection)
            .Where(run =>
                (run.State == ZaloScheduledDraftRunState.Pending ||
                 run.State == ZaloScheduledDraftRunState.ReminderSent) &&
                run.RosterFingerprint != null &&
                run.ReminderSentAt != null &&
                (run.LeaseUntil == null || run.LeaseUntil <= now) &&
                run.Session.Status == SessionStatus.Finished)
            .ToListAsync(cancellationToken);
        foreach (var interruptedRun in interruptedRuns)
        {
            try
            {
                var snapshot = await readiness.BuildAsync(interruptedRun.SessionId, now, cancellationToken);
                if (snapshot?.State != ZaloDraftReadinessState.AlreadyDrafted)
                    continue;
                // Another actor may have finished this session after our attempt
                // marker. Report the observed outcome without claiming authorship.
                interruptedRun.State = ZaloScheduledDraftRunState.AlreadyDrafted;
                interruptedRun.DraftedAt = now;
                interruptedRun.LastError = ScheduledOutcomeUnconfirmedReason;
                interruptedRun.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                if (!await TrySendDraftedResultAsync(
                        interruptedRun.Session, interruptedRun, cancellationToken))
                    failed += 1;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed += 1;
                logger.LogError(exception,
                    "Scheduled draft interrupted-outcome reconciliation failed Session={SessionId}",
                    interruptedRun.SessionId);
            }
        }

        var policies = await db.ZaloScheduledDraftPolicies.AsNoTracking()
            .Where(policy => policy.Enabled)
            .ToListAsync(cancellationToken);

        foreach (var policy in policies)
        {
            try
            {
                var sessions = await db.MatchSessions
                    .Include(session => session.ZaloConnection)
                    .Where(session =>
                        session.ZaloConnectionId == policy.ZaloConnectionId &&
                        session.ZaloGroupId == policy.GroupId &&
                        session.BotEnabled &&
                        (session.Status == SessionStatus.Setup || session.Status == SessionStatus.CaptainSelection) &&
                        session.StartTime != null &&
                        session.StartTime > now)
                    .OrderBy(session => session.StartTime)
                    .Take(2)
                    .ToListAsync(cancellationToken);

                foreach (var session in sessions)
                {
                    var nominalDraftDue = ResolveLocalTimeForSession(
                        session.StartTime!.Value,
                        policy.LocalDraftMinuteOfDay,
                        policy.TimeZoneId);
                    if (nominalDraftDue >= session.StartTime)
                        continue;
                    var decision = await db.ZaloScheduledDraftDecisions.AsNoTracking()
                        .SingleOrDefaultAsync(item => item.SessionId == session.Id, cancellationToken);
                    var scheduledDraftDue = decision?.DeferredUntil ?? nominalDraftDue;
                    var scheduledReminderDue = scheduledDraftDue.AddMinutes(-policy.ReminderMinutes);
                    if (scheduledDraftDue >= session.StartTime || now < scheduledReminderDue)
                        continue;

                    var run = await GetOrCreateRunAsync(
                        session.Id,
                        policy.Version,
                        scheduledReminderDue,
                        scheduledDraftDue,
                        now,
                        cancellationToken);
                    if (run.PolicyVersion != policy.Version)
                        continue;
                    if (decision?.Skip == true)
                    {
                        if (run.State is not (ZaloScheduledDraftRunState.Drafted or ZaloScheduledDraftRunState.AlreadyDrafted))
                        {
                            run.State = ZaloScheduledDraftRunState.Skipped;
                            run.LastError = "session_skipped_by_authorized_operator";
                            run.UpdatedAt = now;
                            await db.SaveChangesAsync(cancellationToken);
                        }
                        skipped += 1;
                        continue;
                    }

                    if (run.State is ZaloScheduledDraftRunState.Drafted or
                        ZaloScheduledDraftRunState.AlreadyDrafted or
                        ZaloScheduledDraftRunState.Blocked or
                        ZaloScheduledDraftRunState.Skipped)
                        continue;

                    if (run.ReminderSentAt is null && now >= run.ReminderDueAt)
                    {
                        if (run.LastError is LateReminderRescheduledReason or ReminderDeliveryAttemptedReason &&
                            now > run.ReminderDueAt.AddMinutes(2))
                        {
                            // An earlier send might have reached Zalo even if its acknowledgement
                            // was lost. Retrying after the safe lead window would advertise a
                            // deadline that is now too close. Require organizer intervention.
                            run.State = ZaloScheduledDraftRunState.Skipped;
                            run.LastError = "reminder_delivery_unconfirmed_no_safe_window";
                            run.UpdatedAt = now;
                            await db.SaveChangesAsync(cancellationToken);
                            skipped += 1;
                            continue;
                        }
                        var effectiveDraftDue = run.DraftDueAt;
                        // Persist the first late adjustment so retries after a bridge failure
                        // reuse the exact same reminder payload/idempotency key.
                        if (ShouldShiftLateReminder(run, scheduledDraftDue, now))
                        {
                            effectiveDraftDue = now.AddMinutes(policy.ReminderMinutes);
                            if (effectiveDraftDue >= session.StartTime)
                            {
                                run.State = ZaloScheduledDraftRunState.Skipped;
                                run.LastError = "late_reminder_no_safe_30_minute_window";
                                run.UpdatedAt = now;
                                await db.SaveChangesAsync(cancellationToken);
                                skipped += 1;
                                continue;
                            }
                            run.DraftDueAt = effectiveDraftDue;
                            run.ReminderDueAt = now;
                            run.LastError = LateReminderRescheduledReason;
                            run.UpdatedAt = now;
                            await db.SaveChangesAsync(cancellationToken);
                        }

                        var freshPolicy = await db.ZaloScheduledDraftPolicies.AsNoTracking()
                            .AnyAsync(item => item.Id == policy.Id &&
                                              item.Enabled &&
                                              item.Version == run.PolicyVersion, cancellationToken);
                        if (!freshPolicy)
                            continue;
                        if (run.LastError is null)
                        {
                            run.LastError = ReminderDeliveryAttemptedReason;
                            run.UpdatedAt = now;
                            await db.SaveChangesAsync(cancellationToken);
                        }
                        var localDue = ToLocal(effectiveDraftDue, policy.TimeZoneId);
                        var reminderText =
                            $"Dự kiến từ {localDue:HH:mm}, và luôn ít nhất {policy.ReminderMinutes} phút sau khi tin này được gửi thành công, tui sẽ tự draft cho buổi {session.Name} nếu trưởng/phó chưa cho chạy trước đó và danh sách đủ điều kiện. " +
                            "Muốn đổi giờ hoặc bỏ qua, trưởng/phó nhắn “hoãn draft đến …” hoặc “hôm nay không tự draft” nha.";
                        var send = await bridge.SendGroupMessageAsync(
                            session.ZaloConnection!.AccountZaloId,
                            policy.GroupId,
                            reminderText,
                            [],
                            idempotencyKey: ReminderIdempotencyKey(run));
                        if (!send.Sent)
                        {
                            failed += 1;
                            continue;
                        }

                        var sentAt = DateTimeOffset.UtcNow;
                        // The bridge can acknowledge a 17:00 warning at 17:01. Persist
                        // its actual delivery-based floor, otherwise the nominal 17:30
                        // execution would violate the promised 30-minute notice.
                        var confirmedDraftDue = ResolveConfirmedDraftDue(
                            effectiveDraftDue, sentAt, policy.ReminderMinutes);
                        var stamped = await db.ZaloScheduledDraftRuns
                            .Where(item => item.Id == run.Id &&
                                           item.PolicyVersion == run.PolicyVersion &&
                                           item.State == ZaloScheduledDraftRunState.Pending &&
                                           item.ReminderSentAt == null &&
                                           item.DraftDueAt == effectiveDraftDue)
                            .ExecuteUpdateAsync(updates => updates
                                .SetProperty(item => item.ReminderSentAt, sentAt)
                                .SetProperty(item => item.DraftDueAt, confirmedDraftDue)
                                .SetProperty(item => item.State, ZaloScheduledDraftRunState.ReminderSent)
                                .SetProperty(item => item.LastError, (string?)null)
                                .SetProperty(item => item.UpdatedAt, sentAt), cancellationToken);
                        db.Entry(run).State = EntityState.Detached;
                        if (stamped != 1)
                            continue; // The organizer changed this run while the bridge sent.
                        run = await db.ZaloScheduledDraftRuns.SingleAsync(
                            item => item.Id == run.Id, cancellationToken);
                    }

                    if (run.State == ZaloScheduledDraftRunState.Drafted &&
                        run.ResultMessageSentAt is null)
                    {
                        var resent = await TrySendDraftedResultAsync(session, run, cancellationToken);
                        if (!resent) failed += 1;
                        continue;
                    }

                    if (run.ReminderSentAt is null ||
                        DateTimeOffset.UtcNow < ResolveConfirmedDraftDue(
                            run.DraftDueAt, run.ReminderSentAt.Value, policy.ReminderMinutes))
                        continue;
                    if (run.State is ZaloScheduledDraftRunState.Drafted or
                        ZaloScheduledDraftRunState.AlreadyDrafted or
                        ZaloScheduledDraftRunState.Blocked or
                        ZaloScheduledDraftRunState.Skipped)
                        continue;

                    var outcome = await TryExecuteDraftAsync(session.Id, policy, run, cancellationToken);
                    drafted += outcome == ZaloScheduledDraftRunState.Drafted ? 1 : 0;
                    skipped += outcome is ZaloScheduledDraftRunState.AlreadyDrafted or ZaloScheduledDraftRunState.Blocked ? 1 : 0;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed += 1;
                logger.LogError(
                    exception,
                    "Scheduled draft cycle failed Connection={ConnectionId} Group={GroupId}",
                    policy.ZaloConnectionId,
                    policy.GroupId);
            }
        }

        return new ZaloScheduledDraftCycleResult(drafted, failed, skipped);
    }

    private async Task<ZaloScheduledDraftRunState> TryExecuteDraftAsync(
        string sessionId,
        ZaloScheduledDraftPolicy policy,
        ZaloScheduledDraftRun run,
        CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        if (!await TryClaimExecutionAsync(
                db, run.Id, policy.Version, policy.ReminderMinutes, leaseToken, now, cancellationToken))
            return run.State;

        // ExecuteUpdate bypasses this context's tracked entity snapshots.
        db.Entry(run).State = EntityState.Detached;
        run = await db.ZaloScheduledDraftRuns.SingleAsync(item => item.Id == run.Id, cancellationToken);
        try
        {
            return await ExecuteClaimedDraftAsync(sessionId, policy, run, cancellationToken);
        }
        finally
        {
            // AutoRunDraftAsync clears its DbContext's change tracker while picking.
            // A scoped ExecuteUpdate also releases a detached run reliably.
            await db.ZaloScheduledDraftRuns
                .Where(item => item.Id == run.Id && item.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(item => item.LeaseToken, (string?)null)
                    .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null), CancellationToken.None);
        }
    }

    internal static bool ShouldShiftLateReminder(
        ZaloScheduledDraftRun run,
        DateTimeOffset configuredDraftDue,
        DateTimeOffset now) =>
        run.ReminderSentAt is null &&
        run.DraftDueAt == configuredDraftDue &&
        run.LastError is null &&
        now > run.ReminderDueAt.AddMinutes(2);

    internal static string ReminderIdempotencyKey(ZaloScheduledDraftRun run) =>
        $"scheduled-draft-reminder:{run.SessionId}:{run.PolicyVersion}:{run.DraftDueAt.UtcTicks}";

    internal static DateTimeOffset ResolveConfirmedDraftDue(
        DateTimeOffset plannedDraftDue,
        DateTimeOffset confirmedReminderSentAt,
        int reminderMinutes)
    {
        var earliestSafeDraft = confirmedReminderSentAt.AddMinutes(reminderMinutes);
        return plannedDraftDue >= earliestSafeDraft ? plannedDraftDue : earliestSafeDraft;
    }

    internal static async Task<bool> TryClaimExecutionAsync(
        VolleyDraftDbContext db,
        string runId,
        int policyVersion,
        int reminderMinutes,
        string token,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return await db.ZaloScheduledDraftRuns
            .Where(run => run.Id == runId &&
                          run.PolicyVersion == policyVersion &&
                          run.ReminderSentAt != null &&
                          run.ReminderSentAt <= now.AddMinutes(-reminderMinutes) &&
                          run.DraftDueAt <= now &&
                          (run.State == ZaloScheduledDraftRunState.Pending ||
                           run.State == ZaloScheduledDraftRunState.ReminderSent) &&
                          (run.LeaseUntil == null || run.LeaseUntil <= now))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(run => run.LeaseToken, token)
                .SetProperty(run => run.LeaseUntil, now.AddMinutes(15))
                .SetProperty(run => run.UpdatedAt, now), cancellationToken) == 1;
    }

    private async Task<ZaloScheduledDraftRunState> ExecuteClaimedDraftAsync(
        string sessionId,
        ZaloScheduledDraftPolicy policy,
        ZaloScheduledDraftRun run,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var freshPolicy = await db.ZaloScheduledDraftPolicies.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == policy.Id, cancellationToken);
        if (freshPolicy is null || !freshPolicy.Enabled || freshPolicy.Version != run.PolicyVersion)
            return await BlockAsync(run, "policy_disabled_or_changed", cancellationToken);

        var session = await db.MatchSessions
            .Include(item => item.ZaloConnection)
            .SingleAsync(item => item.Id == sessionId, cancellationToken);
        if (session.StartTime is null || session.StartTime <= now || session.Status == SessionStatus.Cancelled)
            return await BlockAsync(run, "session_started_or_cancelled", cancellationToken);

        var currentReadiness = await readiness.BuildAsync(sessionId, now, cancellationToken);
        if (currentReadiness is null)
            return await BlockAsync(run, "readiness_unavailable", cancellationToken);
        if (currentReadiness.State == ZaloDraftReadinessState.AlreadyDrafted)
        {
            run.State = ZaloScheduledDraftRunState.AlreadyDrafted;
            run.DraftedAt = now;
            run.LastError = null;
            run.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return run.State;
        }

        if (currentReadiness.HasLinkedPoll)
        {
            var synced = await zaloIntegration.SyncLatestPollAsync(session.AdminUserId, session.Id);
            if (!synced.IsSuccess)
                return await BlockAsync(run, $"poll_sync_failed:{synced.Error}", cancellationToken);
        }

        var afterSync = await readiness.BuildAsync(sessionId, DateTimeOffset.UtcNow, cancellationToken);
        if (afterSync is null ||
            afterSync.State != ZaloDraftReadinessState.Ready ||
            !afterSync.CanEscalate ||
            afterSync.EffectiveSlotCount != afterSync.Capacity ||
            string.IsNullOrWhiteSpace(afterSync.Fingerprint))
        {
            var reason = afterSync?.ReasonCode ?? "readiness_unavailable_after_sync";
            await SendBlockedAsync(session, run, reason, cancellationToken);
            return await BlockAsync(run, reason, cancellationToken);
        }

        var fingerprint = afterSync.Fingerprint;
        var verify = await readiness.BuildAsync(sessionId, DateTimeOffset.UtcNow, cancellationToken);
        if (verify is null ||
            verify.State != ZaloDraftReadinessState.Ready ||
            !string.Equals(verify.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            await SendBlockedAsync(session, run, "roster_changed_before_draft", cancellationToken);
            return await BlockAsync(run, "roster_changed_before_draft", cancellationToken);
        }

        // Organizer decisions can arrive while the poll is synchronizing. Re-read
        // immediately before entering the authoritative session draft service.
        var finalPolicy = await db.ZaloScheduledDraftPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == policy.Id, cancellationToken);
        var finalDecision = await db.ZaloScheduledDraftDecisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.SessionId == sessionId, cancellationToken);
        if (finalPolicy is null || !finalPolicy.Enabled || finalPolicy.Version != run.PolicyVersion ||
            finalDecision?.Skip == true)
            return await BlockAsync(run, "policy_or_organizer_changed_before_draft", cancellationToken);
        if (finalDecision?.DeferredUntil is { } deferred && deferred > DateTimeOffset.UtcNow)
        {
            // A concurrently committed organizer defer must retain the run. The
            // scheduler will revisit it at the later, explicitly authorized time.
            run.DraftDueAt = deferred;
            run.ReminderDueAt = deferred.AddMinutes(-freshPolicy.ReminderMinutes);
            run.ReminderSentAt = null;
            run.State = ZaloScheduledDraftRunState.Pending;
            run.LastError = null;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return run.State;
        }

        var history = new ZaloBotActionHistoryService(
            db,
            loggerFactory.CreateLogger<ZaloBotActionHistoryService>());
        var before = await history.CaptureAsync(session.Id, cancellationToken);
        run.RosterFingerprint = fingerprint;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var drafted = await new SessionDraftService(db).AutoRunDraftAsync(
            session.AdminUserId,
            session.Id,
            false);
        // SessionDraftService clears the entire shared tracker between draft picks.
        // Reload before storing the outcome, including the ambiguous/failure paths.
        run = await db.ZaloScheduledDraftRuns.SingleAsync(item => item.Id == run.Id, cancellationToken);
        session = await db.MatchSessions.Include(item => item.ZaloConnection)
            .SingleAsync(item => item.Id == sessionId, cancellationToken);
        if (!drafted.IsSuccess || drafted.Value is null)
        {
            var finalReadiness = await readiness.BuildAsync(sessionId, DateTimeOffset.UtcNow, cancellationToken);
            if (finalReadiness?.State == ZaloDraftReadinessState.AlreadyDrafted)
            {
                // Draft may have finished just as the engine returned a conflict.
                // Keep the notice neutral because ownership cannot be proven.
                run.State = ZaloScheduledDraftRunState.AlreadyDrafted;
                run.DraftedAt = DateTimeOffset.UtcNow;
                run.LastError = ScheduledOutcomeUnconfirmedReason;
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await TrySendDraftedResultAsync(session, run, cancellationToken);
                return run.State;
            }
            return await BlockAsync(run, $"draft_failed:{drafted.Error}", cancellationToken);
        }

        // Persist completion before the result notification. A failed send can be retried
        // later without ever executing the draft mutation again.
        run.State = ZaloScheduledDraftRunState.Drafted;
        run.DraftedAt = DateTimeOffset.UtcNow;
        run.LastError = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await history.RecordAsync(
            session.Id,
            null,
            "Scheduled draft policy",
            "AutoDraft",
            $"Tự draft theo chính sách v{policy.Version}; roster {fingerprint}.",
            before,
            cancellationToken);

        await TrySendDraftedResultAsync(session, run, cancellationToken);
        return run.State;
    }

    private async Task<bool> TrySendDraftedResultAsync(
        MatchSession session,
        ZaloScheduledDraftRun run,
        CancellationToken cancellationToken)
    {
        if (run.State != ZaloScheduledDraftRunState.Drafted &&
            !IsUnconfirmedScheduledOutcome(run) ||
            run.ResultMessageSentAt is not null ||
            session.ZaloConnection is null)
            return run.ResultMessageSentAt is not null;

        try
        {
            var unconfirmed = IsUnconfirmedScheduledOutcome(run);
            var send = await bridge.SendGroupMessageAsync(
                session.ZaloConnection.AccountZaloId,
                session.ZaloGroupId!,
                unconfirmed
                    ? $"Buổi {session.Name} hiện đã có đội hình. Lượt tự draft theo lịch bị gián đoạn nên tui chưa xác định được thao tác nào hoàn tất việc chia đội. Trưởng/phó kiểm tra lại đội hình nha."
                    : $"Đã tự draft xong buổi {session.Name} theo lịch đã được trưởng/phó bật trước đó.",
                [],
                idempotencyKey: unconfirmed
                    ? $"scheduled-draft-result-unconfirmed:{session.Id}:{run.PolicyVersion}"
                    : $"scheduled-draft-result:{session.Id}:{run.PolicyVersion}");
            if (!send.Sent) return false;

            run.ResultMessageSentAt = DateTimeOffset.UtcNow;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not send scheduled draft result Session={SessionId}; draft will not be repeated",
                session.Id);
            return false;
        }
    }

    private async Task SendBlockedAsync(
        MatchSession session,
        ZaloScheduledDraftRun run,
        string reason,
        CancellationToken cancellationToken)
    {
        if (session.ZaloConnection is null || string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return;
        try
        {
            await bridge.SendGroupMessageAsync(
                session.ZaloConnection.AccountZaloId,
                session.ZaloGroupId,
                $"Tui không tự draft buổi {session.Name}: dữ liệu chưa đủ an toàn ({reason}). Trưởng/phó kiểm tra roster/hồ sơ rồi draft thủ công giúp tui nha.",
                [],
                idempotencyKey: $"scheduled-draft-blocked:{session.Id}:{run.PolicyVersion}:{reason}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not send scheduled draft blocked notice Session={SessionId}", session.Id);
        }
    }

    private async Task<ZaloScheduledDraftRunState> BlockAsync(
        ZaloScheduledDraftRun run,
        string reason,
        CancellationToken cancellationToken)
    {
        run.State = ZaloScheduledDraftRunState.Blocked;
        run.LastError = reason.Length <= 1000 ? reason : reason[..1000];
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return run.State;
    }

    private async Task<ZaloScheduledDraftDecision> GetOrCreateDecisionAsync(
        string sessionId,
        int policyVersion,
        string senderId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var decision = await db.ZaloScheduledDraftDecisions.SingleOrDefaultAsync(
            item => item.SessionId == sessionId, cancellationToken);
        if (decision is not null) return decision;
        decision = new ZaloScheduledDraftDecision
        {
            SessionId = sessionId,
            ChangedByZaloUserId = senderId,
            ChangedAt = now,
            PolicyVersion = policyVersion
        };
        db.ZaloScheduledDraftDecisions.Add(decision);
        return decision;
    }

    private async Task<ZaloScheduledDraftRun> GetOrCreateRunAsync(
        string sessionId,
        int policyVersion,
        DateTimeOffset reminderDue,
        DateTimeOffset draftDue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var run = await db.ZaloScheduledDraftRuns.SingleOrDefaultAsync(
            item => item.SessionId == sessionId, cancellationToken);
        if (run is not null)
        {
            // One durable row exists per session. A later policy version must never
            // inherit the old version's warning, due time, or terminal failure.
            // Compare-and-swap prevents policy edits from resetting an execution lease
            // that another scheduler acquired after this row was read.
            if (run.PolicyVersion != policyVersion &&
                (run.LeaseUntil is null || run.LeaseUntil <= now))
            {
                var oldVersion = run.PolicyVersion;
                if (ResetForPolicy(run, policyVersion, reminderDue, draftDue, now))
                {
                    await db.ZaloScheduledDraftRuns
                        .Where(item => item.Id == run.Id &&
                                       item.PolicyVersion == oldVersion &&
                                       (item.LeaseUntil == null || item.LeaseUntil <= now) &&
                                       item.State != ZaloScheduledDraftRunState.Drafted &&
                                       item.State != ZaloScheduledDraftRunState.AlreadyDrafted)
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(item => item.PolicyVersion, policyVersion)
                            .SetProperty(item => item.ReminderDueAt, reminderDue)
                            .SetProperty(item => item.DraftDueAt, draftDue)
                            .SetProperty(item => item.ReminderSentAt, (DateTimeOffset?)null)
                            .SetProperty(item => item.DraftedAt, (DateTimeOffset?)null)
                            .SetProperty(item => item.ResultMessageSentAt, (DateTimeOffset?)null)
                            .SetProperty(item => item.State, ZaloScheduledDraftRunState.Pending)
                            .SetProperty(item => item.RosterFingerprint, (string?)null)
                            .SetProperty(item => item.LastError, (string?)null)
                            .SetProperty(item => item.UpdatedAt, now), cancellationToken);
                    db.Entry(run).State = EntityState.Detached;
                    return await db.ZaloScheduledDraftRuns.SingleAsync(
                        item => item.SessionId == sessionId, cancellationToken);
                }
            }
            else if (run.PolicyVersion == policyVersion &&
                     run.ReminderSentAt is null &&
                     run.State == ZaloScheduledDraftRunState.Pending &&
                     (run.LeaseUntil is null || run.LeaseUntil <= now) &&
                     draftDue > run.DraftDueAt)
            {
                // A defer can commit while another scheduler is creating this row.
                // Reconcile the newer, later organizer deadline before any warning.
                await db.ZaloScheduledDraftRuns
                    .Where(item => item.Id == run.Id &&
                                   item.PolicyVersion == policyVersion &&
                                   item.State == ZaloScheduledDraftRunState.Pending &&
                                   item.ReminderSentAt == null &&
                                   item.DraftDueAt == run.DraftDueAt &&
                                   (item.LeaseUntil == null || item.LeaseUntil <= now))
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(item => item.ReminderDueAt, reminderDue)
                        .SetProperty(item => item.DraftDueAt, draftDue)
                        .SetProperty(item => item.LastError, (string?)null)
                        .SetProperty(item => item.UpdatedAt, now), cancellationToken);
                db.Entry(run).State = EntityState.Detached;
                return await db.ZaloScheduledDraftRuns.SingleAsync(
                    item => item.SessionId == sessionId, cancellationToken);
            }
            return run;
        }

        run = new ZaloScheduledDraftRun
        {
            SessionId = sessionId,
            PolicyVersion = policyVersion,
            ReminderDueAt = reminderDue,
            DraftDueAt = draftDue,
            State = ZaloScheduledDraftRunState.Pending,
            UpdatedAt = now
        };
        db.ZaloScheduledDraftRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return run;
        }
        catch (DbUpdateException)
        {
            db.Entry(run).State = EntityState.Detached;
            return await db.ZaloScheduledDraftRuns.SingleAsync(
                item => item.SessionId == sessionId, cancellationToken);
        }
    }

    internal static bool IsUnconfirmedScheduledOutcome(ZaloScheduledDraftRun run) =>
        run.State == ZaloScheduledDraftRunState.AlreadyDrafted &&
        run.LastError == ScheduledOutcomeUnconfirmedReason &&
        !string.IsNullOrWhiteSpace(run.RosterFingerprint);

    internal static bool ResetForPolicy(
        ZaloScheduledDraftRun run,
        int policyVersion,
        DateTimeOffset reminderDue,
        DateTimeOffset draftDue,
        DateTimeOffset now)
    {
        if (run.PolicyVersion == policyVersion ||
            run.State is ZaloScheduledDraftRunState.Drafted or ZaloScheduledDraftRunState.AlreadyDrafted)
            return false;

        run.PolicyVersion = policyVersion;
        run.ReminderDueAt = reminderDue;
        run.DraftDueAt = draftDue;
        run.ReminderSentAt = null;
        run.DraftedAt = null;
        run.ResultMessageSentAt = null;
        run.State = ZaloScheduledDraftRunState.Pending;
        run.RosterFingerprint = null;
        run.LastError = null;
        run.UpdatedAt = now;
        return true;
    }

    internal static bool TryParseCommand(string content, out ZaloScheduledDraftCommand command)
    {
        command = new ZaloScheduledDraftCommand(ZaloScheduledDraftCommandKind.Enable);
        var q = ZaloBotIntelligence.Normalize(content);
        if (Regex.IsMatch(q, @"\b(?:hom nay|buoi nay)\s+(?:khong|ko)\s+tu\s+draft\b", RegexOptions.CultureInvariant))
        {
            command = new ZaloScheduledDraftCommand(ZaloScheduledDraftCommandKind.SkipSession);
            return true;
        }

        var defer = Regex.Match(
            q,
            @"\b(?:hoan|doi)\s+(?:tu\s+)?draft\s+den\s+(?<hour>\d{1,2})(?:h(?<minute>\d{1,2})?|:(?<minute>\d{1,2}))?\b",
            RegexOptions.CultureInvariant);
        if (defer.Success && TryMinuteOfDay(defer, out var deferMinute))
        {
            command = new ZaloScheduledDraftCommand(ZaloScheduledDraftCommandKind.DeferSession, deferMinute);
            return true;
        }

        if (Regex.IsMatch(q, @"\b(?:tat|dung|disable)\s+(?:che do\s+)?tu\s+draft\b", RegexOptions.CultureInvariant))
        {
            command = new ZaloScheduledDraftCommand(ZaloScheduledDraftCommandKind.Disable);
            return true;
        }

        var enable = Regex.Match(
            q,
            @"\b(?:bat|enable)\s+(?:che do\s+)?tu\s+draft(?:\s+(?:luc|vao)?\s*(?<hour>\d{1,2})(?:h(?<minute>\d{1,2})?|:(?<minute>\d{1,2}))?\b)?",
            RegexOptions.CultureInvariant);
        if (enable.Success)
        {
            int? minute = null;
            if (enable.Groups["hour"].Success)
            {
                if (!TryMinuteOfDay(enable, out var parsed))
                    return false;
                minute = parsed;
            }
            command = new ZaloScheduledDraftCommand(ZaloScheduledDraftCommandKind.Enable, minute);
            return true;
        }
        return false;
    }

    private static bool TryMinuteOfDay(Match match, out int minuteOfDay)
    {
        minuteOfDay = 0;
        if (!int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
            hour is < 0 or > 23)
            return false;
        var minute = 0;
        if (match.Groups["minute"].Success &&
            (!int.TryParse(match.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minute) ||
             minute is < 0 or > 59))
            return false;
        minuteOfDay = hour * 60 + minute;
        return true;
    }

    private static DateTimeOffset ResolveLocalTimeForSession(
        DateTimeOffset sessionStart,
        int minuteOfDay,
        string timeZoneId)
    {
        var localStart = ToLocal(sessionStart, timeZoneId);
        var local = new DateTime(
            localStart.Year,
            localStart.Month,
            localStart.Day,
            minuteOfDay / 60,
            minuteOfDay % 60,
            0,
            DateTimeKind.Unspecified);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        }
        catch (TimeZoneNotFoundException)
        {
            return new DateTimeOffset(local, FallbackVietnamOffset).ToUniversalTime();
        }
        catch (InvalidTimeZoneException)
        {
            return new DateTimeOffset(local, FallbackVietnamOffset).ToUniversalTime();
        }
    }

    private static DateTimeOffset ToLocal(DateTimeOffset value, string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
        }
        catch (TimeZoneNotFoundException)
        {
            return value.ToOffset(FallbackVietnamOffset);
        }
        catch (InvalidTimeZoneException)
        {
            return value.ToOffset(FallbackVietnamOffset);
        }
    }

    private static string FormatLocalMinute(int minuteOfDay) =>
        $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}";
}
