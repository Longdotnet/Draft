using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private const string RosterDropSoftOutcome = "keep_recruiting_roster_drop_soft";
    private const string RosterDropSelectedIntentPrefix = "RosterDrop:";

    /// <summary>
    /// Event lane for KeepRecruiting. Unlike the broadcast lane, this always refreshes
    /// the linked poll every worker cycle even while @all is in cooldown. Cooldown only
    /// changes how a confirmed drop is announced; it never makes observation blind.
    /// </summary>
    public async Task<int> ProcessRecruitmentRosterChangesDueAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = DraftAutopilotSettings.FromConfiguration(configuration);
        if (!settings.Enabled || !settings.ProactiveEnabled ||
            !configuration.GetValue("ZaloBot:DraftAutopilot:RosterChangeWatchEnabled", true))
            return 0;

        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Where(item =>
                item.BotEnabled &&
                item.ZaloConnection != null &&
                item.ZaloConnectionId != null &&
                item.ZaloGroupId != null &&
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
            .GroupBy(item => $"{item.ZaloConnectionId}:{item.ZaloGroupId}", StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.StartTime).First())
            .OrderBy(item => item.StartTime)
            .Take(30)
            .ToList();
        if (candidates.Count == 0) return 0;

        var observationStore = new ZaloRecruitmentRosterObservationStore(db);
        var debounce = ZaloRosterChangeCoordinatorPolicy.GetDebounce(configuration);
        var recentBroadcastWindow = ZaloRosterChangeCoordinatorPolicy.GetRecentBroadcastWindow(configuration);
        var sent = 0;

        foreach (var session in candidates)
        {
            var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
            if (!sync.Success)
            {
                logger.LogWarning(
                    "Roster-change watch could not refresh linked poll Session={SessionId} Reason={Reason}",
                    session.Id,
                    sync.Error);
                continue;
            }

            await new ZaloGuestIdentityReconciler(db).ReconcileAsync(session.Id, cancellationToken);

            // A newly empty poll slot belongs to the existing guest waitlist first.
            // Promotion happens before observation, so 18→17→18 through a waitlisted
            // guest is never announced as a recruitment drop.
            var promotions = await new ZaloGuestReservationService(db)
                .PromoteWaitingAsync(session.Id, cancellationToken);
            if (promotions.Count > 0)
            {
                logger.LogInformation(
                    "Roster-change watch promoted {PromotionCount} waiting guest(s) before evaluating drop Session={SessionId}",
                    promotions.Count,
                    session.Id);
            }

            var readiness = await new ZaloDraftReadinessService(db)
                .BuildAsync(session.Id, now, cancellationToken);
            if (readiness is null) continue;

            var previous = await observationStore.GetAsync(session.Id, cancellationToken);
            var transition = ZaloRosterChangeCoordinatorPolicy.Observe(
                previous,
                session.Id,
                readiness.EffectiveSlotCount,
                readiness.PresentPlayerCount,
                readiness.Fingerprint,
                now,
                debounce);
            await observationStore.SaveAsync(transition.State, cancellationToken);

            if (transition.Kind == ZaloRosterObservationTransitionKind.DropPending)
            {
                logger.LogDebug(
                    "Roster drop pending debounce Session={SessionId} From={From} To={To}",
                    session.Id,
                    transition.DropFrom,
                    transition.DropTo);
                continue;
            }

            // If the roster recovered after a confirmed-but-unsent incident, discard
            // the stale notification instead of announcing a drop that no longer exists.
            var state = transition.State;
            if (transition.Kind is ZaloRosterObservationTransitionKind.Increased or ZaloRosterObservationTransitionKind.DropBounced)
                continue;
            if (!state.HasUnnotifiedDrop) continue;

            var from = state.LastDropFromCount!.Value;
            var to = state.LastDropToCount!.Value;
            if (readiness.EffectiveSlotCount != to || to >= readiness.Capacity)
            {
                await observationStore.SaveAsync(state with
                {
                    LastDropNotifiedAt = now,
                    UpdatedAt = now
                }, cancellationToken);
                continue;
            }

            var activeSlotRisks = await CountActiveSlotRisksAsync(session, cancellationToken);
            if (activeSlotRisks > 0)
            {
                // Explicit pass/open-slot has its own grounded interaction lane. Record
                // the poll delta but do not double-alert or name the voter who left.
                await observationStore.SaveAsync(state with
                {
                    LastDropNotifiedAt = now,
                    UpdatedAt = now
                }, cancellationToken);
                logger.LogInformation(
                    "Roster drop notification suppressed because slot-risk lane owns incident Session={SessionId} From={From} To={To} Risks={Risks}",
                    session.Id,
                    from,
                    to,
                    activeSlotRisks);
                continue;
            }

            if (sent >= settings.MaxSendsPerCycle) continue;

            var recentBroadcast = await HasRecentKeepRecruitingBroadcastAsync(
                session,
                now - recentBroadcastWindow,
                cancellationToken);
            var sentThisIncident = recentBroadcast
                ? await TrySendRosterDropSoftUpdateAsync(session, readiness, from, to, now, cancellationToken)
                : await TrySendRosterDropRecruitmentBroadcastAsync(session, readiness, from, to, now, cancellationToken);

            if (!sentThisIncident) continue;
            sent += 1;
            await observationStore.SaveAsync(state with
            {
                LastDropNotifiedAt = now,
                UpdatedAt = now
            }, cancellationToken);
        }

        return sent;
    }

    private async Task<bool> HasRecentKeepRecruitingBroadcastAsync(
        MatchSession session,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var intent = ZaloKeepRecruitingBroadcastPolicy.SelectedIntent(session.Id);
        return await db.ZaloGroupMessages.AsNoTracking().AnyAsync(item =>
            item.ZaloConnectionId == session.ZaloConnectionId &&
            item.GroupId == session.ZaloGroupId &&
            item.IsFromBot &&
            item.ReplyOutcome == ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome &&
            item.SelectedIntent == intent &&
            item.SentAt >= cutoff,
            cancellationToken);
    }

    private async Task<bool> TrySendRosterDropSoftUpdateAsync(
        MatchSession session,
        ZaloDraftReadinessSnapshot readiness,
        int from,
        int to,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = session.ZaloConnection;
        if (connection is null || connection.Status != ZaloConnectionStatus.Connected ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return false;
        var body = ZaloRosterChangeCoordinatorPolicy.BuildSoftUpdate(readiness, from, to, 0);
        var idempotencyKey = $"roster-drop-soft:{session.Id}:{from}:{to}:{now.ToUnixTimeSeconds() / 120}";
        return await SendRosterCoordinatorMessageAsync(
            session,
            body,
            [],
            idempotencyKey,
            $"{RosterDropSelectedIntentPrefix}{session.Id}",
            RosterDropSoftOutcome,
            now,
            cancellationToken);
    }

    private async Task<bool> TrySendRosterDropRecruitmentBroadcastAsync(
        MatchSession session,
        ZaloDraftReadinessSnapshot readiness,
        int from,
        int to,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var guestSignupOpen = ZaloRecruitmentGuestGatePolicy.IsAddWindowOpen(session.StartTime, now, configuration);
        var recruitment = ZaloKeepRecruitingBroadcastPolicy.BuildMessage(readiness, 0, guestSignupOpen);
        if (string.IsNullOrWhiteSpace(recruitment)) return false;
        var tail = recruitment.StartsWith("@all ", StringComparison.Ordinal)
            ? recruitment[5..]
            : recruitment;
        var fullBreak = ZaloRosterChangeCoordinatorPolicy.IsFullRosterBreak(from, to, readiness.Capacity);
        var reason = fullBreak
            ? "Kèo vừa từ đủ người thành hụt slot"
            : "Roster vừa tụt thêm";
        var message = $"@all {reason}: {from}/{readiness.Capacity} → {to}/{readiness.Capacity} 😭 {tail}";
        var idempotencyKey = $"draft-keep-recruiting-drop:{session.Id}:{from}:{to}:{now.ToUnixTimeSeconds() / 120}";
        return await SendRosterCoordinatorMessageAsync(
            session,
            message,
            [new BridgeOutgoingMention("-1", 0, 4)],
            idempotencyKey,
            ZaloKeepRecruitingBroadcastPolicy.SelectedIntent(session.Id),
            ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome,
            now,
            cancellationToken);
    }

    private async Task<bool> SendRosterCoordinatorMessageAsync(
        MatchSession session,
        string message,
        IReadOnlyList<BridgeOutgoingMention> mentions,
        string idempotencyKey,
        string selectedIntent,
        string replyOutcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = session.ZaloConnection;
        if (connection is null || connection.Status != ZaloConnectionStatus.Connected ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return false;
        try
        {
            var send = await bridge.SendGroupMessageAsync(
                connection.AccountZaloId,
                session.ZaloGroupId!,
                message,
                mentions,
                idempotencyKey: idempotencyKey);
            var providerReplyId = NormalizeProviderMessageId(send.MessageId);
            var persistedReplyId = providerReplyId ?? $"local:{idempotencyKey}";
            await EnsureV2OutboundMessageAsync(
                connection.Id,
                session.ZaloGroupId!,
                persistedReplyId,
                connection.AccountZaloId,
                connection.DisplayName,
                message,
                cancellationToken);
            var stored = await db.ZaloGroupMessages.SingleOrDefaultAsync(item =>
                item.ZaloConnectionId == connection.Id &&
                item.GroupId == session.ZaloGroupId &&
                item.MessageId == persistedReplyId,
                cancellationToken);
            if (stored is not null)
            {
                stored.SelectedIntent = selectedIntent;
                stored.ReplyOutcome = replyOutcome;
                stored.SentAt = now;
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
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Roster-change coordinator send failed Session={SessionId} Outcome={Outcome}",
                session.Id,
                replyOutcome);
            return false;
        }
    }
}
