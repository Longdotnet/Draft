using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    private const string RosterDropSoftOutcome = "keep_recruiting_roster_drop_soft";
    private const string RosterRecoveredReadyOutcome = "keep_recruiting_roster_recovered_ready";
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
        var observationStore = new ZaloRecruitmentRosterObservationStore(db);
        var reminderStore = new ZaloDraftPreparationReminderStore(db);
        var watched = new List<MatchSession>();
        foreach (var session in sessions)
        {
            var decision = await decisionStore.GetAsync(session.Id, cancellationToken);
            var previousWatch = await observationStore.GetAsync(session.Id, cancellationToken);
            if (decision?.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting)
            {
                watched.Add(session);
                continue;
            }

            // PlayCurrentRoster/StopMatch are explicit newer organizer directions and
            // must terminate any old recruitment watch. A null decision may be the V2
            // reminder lane auto-clearing KeepRecruiting after a clean full roster; in
            // that case the durable observation keeps watching for a later 18→17 break.
            if (decision?.Kind is ZaloDraftPreparationDecisionKind.PlayCurrentRoster or
                                  ZaloDraftPreparationDecisionKind.StopMatch)
            {
                if (previousWatch is not null)
                    await observationStore.DeleteAsync(session.Id, cancellationToken);
                continue;
            }
            if (decision is null && previousWatch is not null)
                watched.Add(session);
        }

        var candidates = watched
            .GroupBy(item => $"{item.ZaloConnectionId}:{item.ZaloGroupId}", StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.StartTime).First())
            .OrderBy(item => item.StartTime)
            .Take(30)
            .ToList();
        if (candidates.Count == 0) return 0;

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

            // A confirmed shortage may have already produced an @all recruitment message
            // or been intentionally handed to the pass-slot lane. When the authoritative
            // state recovers all the way to clean draft readiness, close that user-visible
            // incident immediately instead of leaving the group's last instruction stale.
            // Do not persist the recovered state until delivery succeeds; a transient bridge
            // failure then retries the closure next heavy cycle instead of losing it forever.
            if (previous is not null &&
                ZaloRosterChangeCoordinatorPolicy.ShouldAnnounceRecoveredReady(
                    previous,
                    transition,
                    readiness))
            {
                if (sent >= settings.MaxSendsPerCycle)
                    continue;

                var from = transition.DropFrom ?? previous.StableEffectiveSlotCount;
                var to = transition.DropTo ?? readiness.EffectiveSlotCount;
                var incidentAt = previous.LastDropAt!.Value;
                if (!await TrySendRosterRecoveredReadyAsync(
                        session,
                        readiness,
                        from,
                        to,
                        incidentAt,
                        now,
                        cancellationToken))
                    continue;

                await observationStore.SaveAsync(transition.State, cancellationToken);
                sent += 1;

                // This closure already tells the group the next safe action (`draft đi`).
                // Mark the current draft-prep bucket with the exact recovered fingerprint so
                // the V2 lane later in the same worker cycle does not repeat the ready message.
                var bucket = ZaloDraftPreparationReminderPolicy.GetDueBucket(
                    session.StartTime!.Value,
                    now,
                    settings.StopNudgingMinutesBeforeStart);
                if (bucket is not null)
                {
                    var observationFingerprint = ZaloDraftPreparationReminderObservation.BuildFingerprint(
                        readiness,
                        readiness.ActivePassSlotRiskCount);
                    await reminderStore.MarkHandledAsync(
                        session.Id,
                        bucket.Key,
                        readiness.EffectiveSlotCount,
                        readiness.ActivePassSlotRiskCount,
                        observationFingerprint,
                        now,
                        cancellationToken);
                }
                continue;
            }

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

            var state = transition.State;
            if (transition.Kind is ZaloRosterObservationTransitionKind.Increased or
                                   ZaloRosterObservationTransitionKind.Recovered or
                                   ZaloRosterObservationTransitionKind.DropBounced)
                continue;
            if (!state.HasUnnotifiedDrop) continue;

            var fromDrop = state.LastDropFromCount!.Value;
            var toDrop = state.LastDropToCount!.Value;
            if (readiness.EffectiveSlotCount != toDrop || toDrop >= readiness.Capacity)
            {
                await observationStore.SaveAsync(state with
                {
                    LastDropNotifiedAt = now,
                    UpdatedAt = now
                }, cancellationToken);
                continue;
            }

            // Readiness is the canonical coherent snapshot for pass-slot authority too.
            // Re-querying the handoff ledger here could race the roster snapshot and cause
            // one cycle to mix old slot counts with new pass/claim state.
            var activeSlotRisks = readiness.ActivePassSlotRiskCount;
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
                    fromDrop,
                    toDrop,
                    activeSlotRisks);
                continue;
            }

            if (sent >= settings.MaxSendsPerCycle) continue;

            var incidentAt = state.LastDropAt!.Value;
            var recentBroadcast = await HasRecentKeepRecruitingBroadcastAsync(
                session,
                incidentAt,
                recentBroadcastWindow,
                cancellationToken);
            var sentThisIncident = recentBroadcast
                ? await TrySendRosterDropSoftUpdateAsync(session, readiness, fromDrop, toDrop, incidentAt, now, cancellationToken)
                : await TrySendRosterDropRecruitmentBroadcastAsync(session, readiness, fromDrop, toDrop, incidentAt, now, cancellationToken);

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
        DateTimeOffset incidentAt,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var intent = ZaloKeepRecruitingBroadcastPolicy.SelectedIntent(session.Id);
        var cutoff = incidentAt - window;
        return await db.ZaloGroupMessages.AsNoTracking().AnyAsync(item =>
            item.ZaloConnectionId == session.ZaloConnectionId &&
            item.GroupId == session.ZaloGroupId &&
            item.IsFromBot &&
            item.ReplyOutcome == ZaloKeepRecruitingBroadcastPolicy.ReplyOutcome &&
            item.SelectedIntent == intent &&
            item.SentAt >= cutoff &&
            item.SentAt <= incidentAt,
            cancellationToken);
    }

    private async Task<bool> TrySendRosterRecoveredReadyAsync(
        MatchSession session,
        ZaloDraftReadinessSnapshot readiness,
        int from,
        int to,
        DateTimeOffset incidentAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var body = ZaloRosterChangeCoordinatorPolicy.BuildRecoveredReadyUpdate(readiness, from, to);
        var idempotencyKey = ZaloRosterChangeCoordinatorPolicy.BuildIncidentIdempotencyKey(
            "roster-recovered-ready",
            session.Id,
            from,
            to,
            incidentAt);
        return await SendRosterCoordinatorMessageAsync(
            session,
            body,
            [],
            idempotencyKey,
            $"{RosterDropSelectedIntentPrefix}{session.Id}",
            RosterRecoveredReadyOutcome,
            now,
            cancellationToken);
    }

    private async Task<bool> TrySendRosterDropSoftUpdateAsync(
        MatchSession session,
        ZaloDraftReadinessSnapshot readiness,
        int from,
        int to,
        DateTimeOffset incidentAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = session.ZaloConnection;
        if (connection is null || connection.Status != ZaloConnectionStatus.Connected ||
            string.IsNullOrWhiteSpace(session.ZaloGroupId))
            return false;
        var body = ZaloRosterChangeCoordinatorPolicy.BuildSoftUpdate(readiness, from, to, 0);
        var idempotencyKey = ZaloRosterChangeCoordinatorPolicy.BuildIncidentIdempotencyKey(
            "roster-drop-soft",
            session.Id,
            from,
            to,
            incidentAt);
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
        DateTimeOffset incidentAt,
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
            ? "Kèo vừa từ đủ người thành hụt chỗ"
            : "Danh sách vừa tụt thêm";
        var message = $"@all {reason}: {from}/{readiness.Capacity} → {to}/{readiness.Capacity} 😭 {tail}";
        var idempotencyKey = ZaloRosterChangeCoordinatorPolicy.BuildIncidentIdempotencyKey(
            "draft-keep-recruiting-drop",
            session.Id,
            from,
            to,
            incidentAt);
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