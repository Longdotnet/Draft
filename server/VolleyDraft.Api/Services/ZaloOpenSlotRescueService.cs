using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloOpenSlotRescueRunResult(
    int CandidateCount,
    int NudgedCount,
    int ClaimReleasedCount,
    int ClosedCount,
    int SkippedCount,
    int FailedCount);

/// <summary>
/// Scheduler-driven rescue for pass-slot coordination. It only changes coordination
/// state. Roster/poll/team truth remains owned by the existing domain workflows.
/// Applying is treated as a crash-recovery state: never time it out blindly; reconcile
/// it against canonical roster/team data first.
/// </summary>
public sealed class ZaloOpenSlotRescueService(
    VolleyDraftDbContext db,
    ZaloBridgeClient bridge,
    IConfiguration configuration,
    ILogger<ZaloOpenSlotRescueService> logger)
{
    public Task<ZaloOpenSlotRescueRunResult> RunDueAsync(CancellationToken cancellationToken = default) =>
        RunDueAsync(DateTimeOffset.UtcNow, cancellationToken);

    internal async Task<ZaloOpenSlotRescueRunResult> RunDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("ZaloBot:Ambient:MemberAssist:Rescue:Enabled", true))
            return new(0, 0, 0, 0, 1, 0);

        var maxNudges = Math.Clamp(
            configuration.GetValue("ZaloBot:Ambient:MemberAssist:Rescue:MaxNudges", 3),
            1,
            5);
        var groupCooldown = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("ZaloBot:Ambient:MemberAssist:Rescue:GroupCooldownMinutes", 10),
            2,
            60));
        var retryDelay = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("ZaloBot:Ambient:MemberAssist:Rescue:RetryMinutes", 10),
            5,
            60));
        var applyingStale = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("ZaloBot:Ambient:MemberAssist:Rescue:ApplyingStaleMinutes", 5),
            2,
            30));

        var store = new ZaloOpenSlotOfferStore(db);
        var safetyStore = new ZaloOpenSlotMarketplaceSafetyStore(db);
        var due = await store.ListDueRescueAsync(now, 100, cancellationToken);
        var staleApplying = await safetyStore.ListStaleApplyingAsync(now - applyingStale, 100, cancellationToken);
        var candidates = due
            .Concat(staleApplying)
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .OrderBy(item => item.UpdatedAt)
            .Take(100)
            .ToList();

        var nudged = 0;
        var released = 0;
        var closed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var candidate in candidates)
        {
            var leaseToken = Guid.NewGuid().ToString("n");
            if (!await store.TryAcquireReminderLeaseAsync(
                    candidate,
                    leaseToken,
                    now,
                    TimeSpan.FromMinutes(5),
                    cancellationToken))
            {
                skipped += 1;
                continue;
            }

            try
            {
                var session = await LoadSessionAsync(candidate, cancellationToken);

                // Once a claimant has confirmed and the offer entered Applying, the
                // reservation deadline is irrelevant. A stale Applying row means an API
                // process may have died between marketplace CAS and domain completion.
                // Reconcile canonical truth instead of expiring/cancelling blindly.
                if (candidate.Status == ZaloOpenSlotOfferStatus.Applying)
                {
                    if (candidate.UpdatedAt > now - applyingStale)
                    {
                        await store.ReleaseReminderLeaseAsync(candidate.Id, leaseToken, null, cancellationToken);
                        skipped += 1;
                        continue;
                    }

                    if (session is null)
                    {
                        await store.CloseFromReminderAsync(
                            candidate.Id,
                            leaseToken,
                            ZaloOpenSlotOfferStatus.Expired,
                            "SessionMissingDuringApplyRecovery",
                            now,
                            cancellationToken);
                        closed += 1;
                        continue;
                    }

                    if (await IsCanonicalTransferCompleteAsync(session, candidate, cancellationToken))
                    {
                        await store.CloseFromReminderAsync(
                            candidate.Id,
                            leaseToken,
                            ZaloOpenSlotOfferStatus.Completed,
                            "RecoveredCompletedTransfer",
                            now,
                            cancellationToken);
                        closed += 1;
                        continue;
                    }

                    var ownerStillPresent = ZaloOpenSlotOfferService.ResolveOwner(session, candidate) is not null;
                    if (ownerStillPresent &&
                        session.BotEnabled &&
                        session.Status != SessionStatus.Cancelled &&
                        candidate.ExpiresAt > now &&
                        (session.StartTime is null || session.StartTime > now))
                    {
                        // Domain transaction did not win. This is controlled internal
                        // compensation, not a user cancellation during Applying.
                        var claimantId = candidate.ClaimantZaloUserId ?? string.Empty;
                        if (claimantId.Length > 0 &&
                            await store.ReleaseClaimAsync(candidate.Id, claimantId, cancellationToken))
                        {
                            released += 1;
                            if (session.ZaloConnection is not null &&
                                session.ZaloConnection.Status == ZaloConnectionStatus.Connected &&
                                !await HasRecentBotMessageAsync(candidate.ConnectionId, candidate.GroupId, now - groupCooldown, cancellationToken))
                            {
                                var owner = ZaloOpenSlotOfferService.FriendlyName(candidate.OwnerDisplayName);
                                var claimant = ZaloOpenSlotOfferService.FriendlyName(candidate.ClaimantDisplayName);
                                await SendAndRememberAsync(
                                    session,
                                    candidate,
                                    $"Lượt chốt slot {owner} ở {candidate.SessionName} cho {claimant} bị gián đoạn nên tui đã đối chiếu roster: chưa chuyển. Tui mở lại slot an toàn nha 😅",
                                    $"apply-recovered-open:{candidate.Version}",
                                    now,
                                    cancellationToken);
                            }
                            continue;
                        }
                    }

                    if (!session.BotEnabled || session.Status == SessionStatus.Cancelled)
                    {
                        await store.CloseFromReminderAsync(
                            candidate.Id,
                            leaseToken,
                            ZaloOpenSlotOfferStatus.Cancelled,
                            session.Status == SessionStatus.Cancelled ? "SessionCancelledDuringApplyRecovery" : "BotDisabledDuringApplyRecovery",
                            now,
                            cancellationToken);
                        closed += 1;
                        continue;
                    }

                    if (candidate.ExpiresAt <= now || session.StartTime is { } applyStart && applyStart <= now)
                    {
                        await store.CloseFromReminderAsync(
                            candidate.Id,
                            leaseToken,
                            ZaloOpenSlotOfferStatus.Expired,
                            "SessionWindowEndedDuringApplyRecovery",
                            now,
                            cancellationToken);
                        closed += 1;
                        continue;
                    }

                    // Owner disappeared but the exact claimant is not canonically in the
                    // transferred slot. That is ambiguous corruption; do not fabricate a
                    // completion or reopen a slot another flow may own.
                    await store.ReleaseReminderLeaseAsync(candidate.Id, leaseToken, null, cancellationToken);
                    logger.LogWarning(
                        "Stale Applying open-slot offer needs manual attention Offer={OfferId} Session={SessionId} Owner={OwnerId} Claimant={ClaimantId}",
                        candidate.Id,
                        candidate.SessionId,
                        candidate.OwnerZaloUserId,
                        candidate.ClaimantZaloUserId);
                    skipped += 1;
                    continue;
                }

                if (session is null)
                {
                    await store.CloseFromReminderAsync(
                        candidate.Id, leaseToken, ZaloOpenSlotOfferStatus.Expired,
                        "SessionOrConnectionMissing", now, cancellationToken);
                    closed += 1;
                    continue;
                }

                if (!session.BotEnabled)
                {
                    await store.CloseFromReminderAsync(
                        candidate.Id, leaseToken, ZaloOpenSlotOfferStatus.Cancelled,
                        "BotDisabled", now, cancellationToken);
                    closed += 1;
                    continue;
                }

                if (session.Status == SessionStatus.Cancelled)
                {
                    await store.CloseFromReminderAsync(
                        candidate.Id, leaseToken, ZaloOpenSlotOfferStatus.Cancelled,
                        "SessionCancelled", now, cancellationToken);
                    closed += 1;
                    continue;
                }

                if (candidate.ExpiresAt <= now || session.StartTime is { } started && started <= now)
                {
                    await store.CloseFromReminderAsync(
                        candidate.Id, leaseToken, ZaloOpenSlotOfferStatus.Expired,
                        "SessionWindowEnded", now, cancellationToken);
                    closed += 1;
                    continue;
                }

                if (ZaloOpenSlotOfferService.ResolveOwner(session, candidate) is null)
                {
                    await store.CloseFromReminderAsync(
                        candidate.Id, leaseToken, ZaloOpenSlotOfferStatus.Completed,
                        "OwnerNoLongerPresent", now, cancellationToken);
                    closed += 1;
                    continue;
                }

                if (session.ZaloConnection is null || session.ZaloConnection.Status != ZaloConnectionStatus.Connected)
                {
                    await store.ReleaseReminderLeaseAsync(candidate.Id, leaseToken, now.Add(retryDelay), cancellationToken);
                    skipped += 1;
                    continue;
                }

                if (candidate.Status == ZaloOpenSlotOfferStatus.ClaimPending &&
                    candidate.ClaimExpiresAt is { } claimExpiresAt && claimExpiresAt <= now)
                {
                    var nextNudgeAt = NextAfterClaimTimeout(now, session.StartTime, candidate.ExpiresAt);
                    if (!await store.ReleaseTimedOutClaimAsync(
                            candidate.Id, leaseToken, now, nextNudgeAt, cancellationToken))
                    {
                        skipped += 1;
                        continue;
                    }
                    released += 1;

                    if (!await HasRecentBotMessageAsync(candidate.ConnectionId, candidate.GroupId, now - groupCooldown, cancellationToken))
                    {
                        var claimant = ZaloOpenSlotOfferService.FriendlyName(candidate.ClaimantDisplayName);
                        var owner = ZaloOpenSlotOfferService.FriendlyName(candidate.OwnerDisplayName);
                        var text = $"{claimant} chưa chốt kịp nên tui mở lại slot {owner} ở {candidate.SessionName} nha 😅 Ai hốt thì nói ‘tui nhận {candidate.SessionName}’.";
                        await SendAndRememberAsync(session, candidate, text, $"claim-timeout:{candidate.Version}", now, cancellationToken);
                    }
                    continue;
                }

                if (candidate.Status != ZaloOpenSlotOfferStatus.Open ||
                    candidate.NextNudgeAt is null || candidate.NextNudgeAt > now ||
                    candidate.NudgeCount >= maxNudges)
                {
                    await store.ReleaseReminderLeaseAsync(candidate.Id, leaseToken, null, cancellationToken);
                    skipped += 1;
                    continue;
                }

                if (await HasRecentBotMessageAsync(candidate.ConnectionId, candidate.GroupId, now - groupCooldown, cancellationToken))
                {
                    await store.ReleaseReminderLeaseAsync(candidate.Id, leaseToken, now.Add(groupCooldown), cancellationToken);
                    skipped += 1;
                    continue;
                }

                var nudgeNumber = candidate.NudgeCount + 1;
                var message = BuildNudge(candidate, session, nudgeNumber);
                try
                {
                    await SendAndRememberAsync(session, candidate, message, $"nudge:{nudgeNumber}", now, cancellationToken);
                }
                catch (Exception sendException) when (!cancellationToken.IsCancellationRequested)
                {
                    failed += 1;
                    await store.ReleaseReminderLeaseAsync(candidate.Id, leaseToken, now.Add(retryDelay), cancellationToken);
                    logger.LogWarning(
                        sendException,
                        "Open-slot rescue send failed Offer={OfferId} Group={GroupId} Nudge={Nudge}",
                        candidate.Id,
                        candidate.GroupId,
                        nudgeNumber);
                    continue;
                }

                var next = CalculateNextNudge(
                    now,
                    session.StartTime,
                    candidate.ExpiresAt,
                    nudgeNumber,
                    maxNudges);
                await store.MarkNudgedAsync(candidate.Id, leaseToken, now, next, cancellationToken);
                nudged += 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed += 1;
                await store.ReleaseReminderLeaseAsync(candidate.Id, leaseToken, now.Add(retryDelay), cancellationToken);
                logger.LogWarning(
                    exception,
                    "Open-slot rescue candidate failed Offer={OfferId} Group={GroupId}",
                    candidate.Id,
                    candidate.GroupId);
            }
        }

        return new(candidates.Count, nudged, released, closed, skipped, failed);
    }

    private async Task<MatchSession?> LoadSessionAsync(
        ZaloOpenSlotOfferSnapshot offer,
        CancellationToken cancellationToken)
    {
        var query = db.MatchSessions
            .AsNoTracking()
            .Include(session => session.ZaloConnection)
            .Include(session => session.Players)
                .ThenInclude(player => player.PlayerProfile)
            .Where(session => session.Id == offer.SessionId && session.ZaloGroupId == offer.GroupId);
        if (!string.IsNullOrWhiteSpace(offer.ConnectionId))
            query = query.Where(session => session.ZaloConnectionId == offer.ConnectionId);
        var matches = await query.Take(2).ToListAsync(cancellationToken);
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<bool> IsCanonicalTransferCompleteAsync(
        MatchSession session,
        ZaloOpenSlotOfferSnapshot offer,
        CancellationToken cancellationToken)
    {
        if (ZaloOpenSlotOfferService.ResolveOwner(session, offer) is not null)
            return false;
        if (string.IsNullOrWhiteSpace(offer.ClaimantZaloUserId))
            return false;

        var claimantMatches = session.Players
            .Where(player =>
                player.IsPresent &&
                string.Equals(
                    NormalizeId(player.PlayerProfile?.ZaloUserId),
                    NormalizeId(offer.ClaimantZaloUserId),
                    StringComparison.Ordinal))
            .Take(2)
            .ToList();
        if (claimantMatches.Count != 1) return false;
        var claimant = claimantMatches[0];

        if (session.Status is SessionStatus.Setup or SessionStatus.CaptainSelection)
            return true;
        if (session.Status != SessionStatus.Finished)
            return false;

        return await db.DraftSlotPlayers
            .AsNoTracking()
            .AnyAsync(link =>
                link.SessionPlayerId == claimant.Id &&
                link.DraftSlot.SessionId == session.Id &&
                link.DraftSlot.AssignedTeamId != null,
                cancellationToken);
    }

    private async Task<bool> HasRecentBotMessageAsync(
        string connectionId,
        string groupId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return false;
        return await db.ZaloGroupMessages
            .AsNoTracking()
            .AnyAsync(message =>
                message.ZaloConnectionId == connectionId &&
                message.GroupId == groupId &&
                message.IsFromBot &&
                message.SentAt >= since,
                cancellationToken);
    }

    private async Task SendAndRememberAsync(
        MatchSession session,
        ZaloOpenSlotOfferSnapshot offer,
        string text,
        string suffix,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = session.ZaloConnection
            ?? throw new InvalidOperationException("Zalo connection is unavailable for open-slot rescue.");
        var idempotencyKey = $"open-slot-rescue:{offer.Id}:{suffix}";
        var send = await bridge.SendGroupMessageAsync(
            connection.AccountZaloId,
            offer.GroupId,
            text,
            [],
            idempotencyKey: idempotencyKey);
        if (!send.Sent)
            throw new InvalidOperationException("Zalo bridge did not confirm open-slot rescue send.");

        var persistedMessageId = string.IsNullOrWhiteSpace(send.MessageId)
            ? idempotencyKey
            : send.MessageId!;
        if (!await db.ZaloGroupMessages
                .AsNoTracking()
                .AnyAsync(message =>
                    message.ZaloConnectionId == connection.Id &&
                    message.MessageId == persistedMessageId,
                    cancellationToken))
        {
            db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                ZaloConnectionId = connection.Id,
                GroupId = offer.GroupId,
                MessageId = persistedMessageId,
                SenderId = connection.AccountZaloId,
                SenderName = connection.DisplayName,
                Content = text,
                IsFromBot = true,
                SentAt = now,
                ReceivedAt = now,
                FirstObservedAt = now,
                LastObservedAt = now,
                ReplyOutcome = "open_slot_rescue"
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string BuildNudge(
        ZaloOpenSlotOfferSnapshot offer,
        MatchSession session,
        int nudgeNumber)
    {
        var owner = ZaloOpenSlotOfferService.FriendlyName(offer.OwnerDisplayName);
        var timeNote = session.StartTime is { } start
            ? $" Kèo còn khoảng {FriendlyRemaining(start - DateTimeOffset.UtcNow)}."
            : string.Empty;
        return nudgeNumber switch
        {
            1 => $"Tin pass slot của {owner} ở {offer.SessionName} trôi rồi nè 😆 Slot vẫn đang mở, ai hốt nói ‘tui nhận {offer.SessionName}’.{timeNote}",
            2 => $"Slot {owner} ở {offer.SessionName} vẫn chưa có người hốt nha 👀 Ai vào được thì nói ‘tui nhận {offer.SessionName}’.{timeNote}",
            _ => $"Ré slot {offer.SessionName} lần cuối nè 🥲 slot của {owner} vẫn đang trống người nhận. Ai cứu kèo nói ‘tui nhận {offer.SessionName}’.{timeNote}"
        };
    }

    internal static DateTimeOffset? CalculateNextNudge(
        DateTimeOffset now,
        DateTimeOffset? sessionStart,
        DateTimeOffset expiresAt,
        int nudgeNumberJustSent,
        int maxNudges)
    {
        if (nudgeNumberJustSent >= maxNudges) return null;
        if (sessionStart is null)
        {
            var fallback = now.AddMinutes(60);
            return fallback < expiresAt ? fallback : null;
        }

        var target = nudgeNumberJustSent == 1
            ? sessionStart.Value.AddMinutes(-60)
            : sessionStart.Value.AddMinutes(-30);
        var minimumGap = nudgeNumberJustSent == 1 ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(15);
        if (target < now.Add(minimumGap)) target = now.Add(minimumGap);
        return target < sessionStart.Value && target < expiresAt ? target : null;
    }

    private static DateTimeOffset? NextAfterClaimTimeout(
        DateTimeOffset now,
        DateTimeOffset? sessionStart,
        DateTimeOffset expiresAt)
    {
        var next = now.AddMinutes(10);
        if (sessionStart is { } start && next >= start) return null;
        return next < expiresAt ? next : null;
    }

    private static string FriendlyRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "0p";
        if (remaining.TotalMinutes < 60) return $"{Math.Max(1, (int)Math.Round(remaining.TotalMinutes))}p";
        var hours = (int)Math.Floor(remaining.TotalHours);
        var minutes = remaining.Minutes;
        return minutes == 0 ? $"{hours}h" : $"{hours}h{minutes:00}";
    }

    private static string NormalizeId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.EndsWith("_0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}
