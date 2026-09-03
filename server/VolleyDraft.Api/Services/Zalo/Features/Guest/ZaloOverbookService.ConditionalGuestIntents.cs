using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    public async Task<int> ProcessConditionalGuestIntentsDueAsync(
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("ZaloBot:DraftAutopilot:ConditionalGuestIntentsEnabled", true))
            return 0;

        var now = DateTimeOffset.UtcNow;
        var store = new ZaloConditionalGuestIntentStore(db);
        var due = await store.LoadDueAsync(now, 50, cancellationToken);
        if (due.Count == 0) return 0;

        var handled = 0;
        foreach (var intent in due)
        {
            var session = await db.MatchSessions
                .AsNoTracking()
                .Include(item => item.ZaloConnection)
                .SingleOrDefaultAsync(item =>
                    item.Id == intent.SessionId &&
                    item.BotEnabled &&
                    item.ZaloGroupId == intent.GroupId,
                    cancellationToken);
            if (session is null || session.StartTime is null ||
                session.Status is SessionStatus.Cancelled or SessionStatus.Drafting or SessionStatus.Finished ||
                session.StartTime <= now)
            {
                await store.SetStatusAsync(intent.Id, ZaloConditionalGuestIntentStatus.Expired, null, null, cancellationToken);
                handled += 1;
                continue;
            }

            if (!ZaloRecruitmentGuestGatePolicy.IsAddWindowOpen(session.StartTime, now, configuration))
                continue;

            var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
            if (!sync.Success)
            {
                await store.SetRetryErrorAsync(intent.Id, sync.Error ?? "conditional_guest_poll_sync_failed", cancellationToken);
                continue;
            }

            await new ZaloGuestIdentityReconciler(db).ReconcileAsync(session.Id, cancellationToken);

            var readiness = await new ZaloDraftReadinessService(db).BuildAsync(session.Id, now, cancellationToken);
            if (readiness is null)
            {
                await store.SetRetryErrorAsync(intent.Id, "conditional_guest_readiness_unavailable", cancellationToken);
                continue;
            }

            var missing = Math.Max(0, readiness.Capacity - readiness.EffectiveSlotCount);
            if (missing > 0)
            {
                // Existing waitlist always owns newly free room first. The worker runs
                // the waitlist lane before this one; this extra guard handles a rare
                // poll change between those two reads without silently leapfrogging it.
                var hasWaiting = await db.ZaloGuestReservations.AsNoTracking().AnyAsync(item =>
                    item.SessionId == session.Id && item.Status == ZaloGuestReservationStatus.Waitlisted,
                    cancellationToken);
                if (hasWaiting) continue;
            }

            if (missing < intent.MinimumMissingSlots)
            {
                try
                {
                    // Send first, then close the intent. If transport/persistence fails,
                    // the Active row remains retryable and the stable idempotency key
                    // prevents a duplicate visible message on a provider retry.
                    await SendConditionalGuestResultAsync(
                        session,
                        intent,
                        $"Tới mốc {ZaloConditionalGuestIntentPolicy.FormatLocalTime(intent.RequestedTriggerAt)} tui sync {session.Name}: roster đang {readiness.EffectiveSlotCount}/{readiness.Capacity}, không còn thiếu theo condition nên tui không + guest nào nha.",
                        "condition-false",
                        cancellationToken);
                    await store.SetStatusAsync(
                        intent.Id,
                        ZaloConditionalGuestIntentStatus.SkippedConditionFalse,
                        null,
                        now,
                        cancellationToken);
                    handled += 1;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await store.SetRetryErrorAsync(intent.Id, exception.Message, cancellationToken);
                    logger.LogWarning(exception, "Conditional guest false-condition notification failed Intent={IntentId} Session={SessionId}", intent.Id, intent.SessionId);
                }
                continue;
            }

            try
            {
                var guests = ZaloConditionalGuestIntentPolicy.DeserializeGuests(intent.GuestsJson);
                var command = new ZaloRecruitmentGuestCommand(
                    ZaloRecruitmentGuestCommandKind.Add,
                    intent.Quantity,
                    guests.Count == intent.Quantity
                        ? guests
                        : Enumerable.Range(0, intent.Quantity).Select(_ => new ZaloRecruitmentGuestSpec()).ToArray());

                // SourceMessageId is stable across retries. AddAsync is therefore the
                // idempotent mutation boundary: a send failure can safely retry this
                // whole block without adding another reservation/player.
                var result = await new ZaloGuestReservationService(db).AddAsync(
                    session,
                    intent.SponsorZaloUserId,
                    intent.SponsorDisplayName,
                    $"conditional:{intent.Id}",
                    intent.RecruitmentMessageId,
                    command,
                    cancellationToken);

                await SyncSemanticGuestConversationStateAsync(
                    intent.GroupId,
                    intent.SponsorZaloUserId,
                    session.Id,
                    intent.SourceMessageId,
                    intent.RecruitmentMessageId,
                    cancellationToken);

                var admitted = result.Added.Count;
                var waiting = result.Waitlisted.Count;
                var placement = waiting == 0
                    ? $"{admitted} guest vào roster"
                    : admitted == 0
                        ? $"{waiting} guest vào waitlist"
                        : $"{admitted} vào roster, {waiting} vào waitlist";
                var missingProfile = result.Added.Concat(result.Waitlisted).Count(item => item.Gender is null);
                var profileNote = missingProfile > 0
                    ? $" Còn {missingProfile} guest thiếu giới tính, bổ sung trước draft giúp tui."
                    : string.Empty;

                await SendConditionalGuestResultAsync(
                    session,
                    intent,
                    $"Condition đã tới hạn và roster thật còn thiếu ({readiness.EffectiveSlotCount}/{readiness.Capacity}) nên tui chạy +{intent.Quantity}: {placement}. Roster sau mutation {result.AfterEffectiveSlots}/{result.Capacity}.{profileNote}",
                    "executed",
                    cancellationToken);

                // Mark terminal only after the result message has been persisted/sent.
                // If anything above fails, the Active row retries through the same
                // mutation and outbound idempotency keys.
                await store.SetStatusAsync(intent.Id, ZaloConditionalGuestIntentStatus.Executed, null, now, cancellationToken);
                handled += 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await store.SetRetryErrorAsync(intent.Id, exception.Message, cancellationToken);
                logger.LogWarning(exception, "Conditional guest intent execution failed Intent={IntentId} Session={SessionId}", intent.Id, intent.SessionId);
            }
        }

        return handled;
    }

    private async Task SendConditionalGuestResultAsync(
        MatchSession session,
        ZaloConditionalGuestIntentSnapshot intent,
        string body,
        string outcome,
        CancellationToken cancellationToken)
    {
        var connection = session.ZaloConnection;
        if (connection is null || connection.Status != ZaloConnectionStatus.Connected || string.IsNullOrWhiteSpace(session.ZaloGroupId))
            throw new InvalidOperationException("Zalo connection is unavailable for conditional guest result notification.");

        var label = $"@{intent.SponsorDisplayName}";
        var message = $"{label} {body}";
        IReadOnlyList<BridgeOutgoingMention> mentions = string.IsNullOrWhiteSpace(intent.SponsorZaloUserId)
            ? []
            : [new BridgeOutgoingMention(intent.SponsorZaloUserId, 0, label.Length)];
        var key = $"conditional-guest:{intent.Id}:{outcome}";
        var send = await bridge.SendGroupMessageAsync(
            connection.AccountZaloId,
            session.ZaloGroupId!,
            message,
            mentions,
            idempotencyKey: key);
        var providerId = NormalizeProviderMessageId(send.MessageId);
        var persistedId = providerId ?? $"local:{key}";
        await EnsureV2OutboundMessageAsync(
            connection.Id,
            session.ZaloGroupId!,
            persistedId,
            connection.AccountZaloId,
            connection.DisplayName,
            message,
            cancellationToken);
        if (providerId is not null)
            await new ZaloMessageGraphStore(db).RememberOutboundAsync(connection.Id, session.ZaloGroupId!, providerId, intent.SourceMessageId, cancellationToken);
    }
}
