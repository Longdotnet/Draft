using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal static class ZaloLeaderAwareDraftReminderPolicy
{
    internal static string? BuildMessage(
        MatchSession session,
        ZaloDraftReadinessSnapshot readiness,
        ZaloDraftPreparationDecisionSnapshot? decision,
        bool decisionWasStale,
        int? staleDecisionSlotCount,
        int? previousObservedSlotCount,
        int activeSlotRiskCount,
        bool urgent)
    {
        var count = readiness.EffectiveSlotCount;
        var capacity = readiness.Capacity;
        var name = readiness.SessionName;
        var teamCount = Math.Max(1, session.TeamCount);
        var rawLabel = readiness.PresentPlayerCount == count
            ? $"{count}/{capacity}"
            : $"{readiness.PresentPlayerCount} người / {count} effective slot (mốc {capacity})";

        if (activeSlotRiskCount > 0)
        {
            var risk = activeSlotRiskCount == 1 ? "1 slot" : $"{activeSlotRiskCount} slot";
            return $"Tui vừa sync {name}: {rawLabel}, nhưng đang có {risk} báo pass/huỷ chưa xử lý xong 😭 Chưa chốt draft nha; roster/poll sạch lại rồi tui tính tiếp theo quyết định của trưởng/phó.";
        }

        if (decision?.Kind == ZaloDraftPreparationDecisionKind.StopMatch)
            return null;

        if (decision?.Kind == ZaloDraftPreparationDecisionKind.PlayCurrentRoster &&
            decision.MatchesRoster(readiness))
        {
            if (readiness.MissingProfileCount > 0)
            {
                return $"Kèo {name} đã được trưởng/phó chốt vẫn chơi với {rawLabel}, nhưng còn {readiness.MissingProfileCount} hồ sơ thiếu dữ liệu: {string.Join(", ", readiness.MissingProfileNames.Take(6))}. Bổ sung nốt giúp tui rồi mới draft nha 😆";
            }

            if (ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(count, teamCount))
            {
                var perTeam = count / teamCount;
                return urgent
                    ? $"{name} vẫn giữ đúng roster đã chốt: {rawLabel} → {teamCount} team x{perTeam} ✅ Sát giờ rồi; nếu muốn chia team nói `draft đi`, tui sync poll lần cuối rồi chạy."
                    : $"{name} vẫn đúng roster trưởng/phó đã chốt: {rawLabel} → {teamCount} team x{perTeam} 👌 Không dí kiếm thêm nữa; khi muốn chia nói `draft đi`.";
            }

            return $"{name} vẫn giữ quyết định chơi với {rawLabel} 👌 Nhưng {count} effective slot chưa chia đều được {teamCount} team. Kèo vẫn chạy theo quyết định trưởng/phó, còn bot chỉ auto-draft sau khi shared/rotation hoặc roster làm effective slot chia hết cho {teamCount}.";
        }

        var stalePrefix = decisionWasStale
            ? $"Roster {name} vừa đổi so với lúc chốt{(staleDecisionSlotCount is null ? string.Empty : $" {staleDecisionSlotCount} slot")}, nên quyết định roster cũ hết hiệu lực nha. "
            : string.Empty;

        if (readiness.State == ZaloDraftReadinessState.RosterOverCapacity)
        {
            return $"{stalePrefix}Tui vừa sync {name}: đang {count}/{capacity}, dư {Math.Max(1, count - capacity)} slot 😭 Chưa chốt roster được; xử lý over-slot trước, poll sạch rồi tui quay lại hỏi/chốt tiếp.";
        }

        if (readiness.State == ZaloDraftReadinessState.Ready)
        {
            return urgent
                ? $"{stalePrefix}Tui vừa sync {name}: đủ {count}/{capacity} ✅ team vẫn chưa chia. Sát giờ rồi, nói `draft đi` là tui check poll lần cuối rồi chạy."
                : $"{stalePrefix}Tui vừa sync {name}: đủ {count}/{capacity} rồi nha ✅ Team chưa chia; nói `draft đi` là tui check poll lần cuối rồi chạy.";
        }

        if (decision?.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting && count < capacity)
        {
            var delta = BuildDelta(previousObservedSlotCount, count, capacity);
            var missing = Math.Max(0, capacity - count);
            return urgent
                ? $"{delta}Trưởng/phó đã chốt tiếp tục kiếm thêm; hiện còn thiếu {missing} slot. Tui canh poll tiếp nha 🚨 có thay đổi tui báo ngay."
                : $"{delta}Trưởng/phó đã chốt tiếp tục kiếm thêm; hiện còn thiếu {missing} slot. Tui canh delta tiếp, không hỏi lại cùng một quyết định mỗi lượt 😆";
        }

        if (readiness.State == ZaloDraftReadinessState.NoRoster)
        {
            return $"{stalePrefix}Tui vừa sync đúng poll {name}: đang 0/{capacity}. Trưởng/phó cho tui hướng xử lý kèo nha; nếu vẫn gom người thì nói `kiếm thêm`, tui sẽ canh poll tiếp.";
        }

        if (readiness.State != ZaloDraftReadinessState.RosterNotFull)
        {
            if (readiness.MissingProfileCount > 0)
                return $"{name} còn {readiness.MissingProfileCount} hồ sơ thiếu dữ liệu: {string.Join(", ", readiness.MissingProfileNames.Take(6))}. Bổ sung nốt giúp tui trước khi chốt draft.";
            return null;
        }

        var deltaPrefix = stalePrefix + BuildDelta(previousObservedSlotCount, count, capacity);
        var even = ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(count, teamCount);
        if (even)
        {
            var perTeam = count / teamCount;
            return urgent
                ? $"{deltaPrefix}{rawLabel} vẫn chia được {teamCount} team x{perTeam}. Giờ sát giờ rồi 🚨 trưởng/phó chốt giúp: `chốt {count}` / `{count} vẫn đánh`, hoặc `kiếm thêm`. Tui bám quyết định của ông, không tự phán theo số slot."
                : $"{deltaPrefix}{rawLabel} vẫn chia được {teamCount} team x{perTeam} nha. Trưởng/phó chốt giúp `chốt {count}` / `{count} vẫn đánh`, hoặc nói `kiếm thêm`; tui bám đúng quyết định đó.";
        }

        return urgent
            ? $"{deltaPrefix}{rawLabel}. Kèo vẫn có thể chơi nếu trưởng/phó muốn, nhưng {count} effective slot chưa chia đều {teamCount} team 🚨 Nếu giữ roster hiện tại nói `vẫn đánh`; nếu tiếp tục tuyển nói `kiếm thêm`. Muốn bot auto-draft thì cần shared/rotation hoặc roster về số chia hết cho {teamCount}."
            : $"{deltaPrefix}{rawLabel}. Trưởng/phó có thể nói `vẫn đánh` hoặc `kiếm thêm`; riêng auto-draft thì {count} effective slot chưa chia đều {teamCount} team, cần shared/rotation hoặc roster đổi trước.";
    }

    private static string BuildDelta(int? previous, int current, int capacity)
    {
        if (previous is null || previous == current)
            return $"Tui vừa sync: hiện {current}/{capacity}. ";
        return previous > current
            ? $"Tui vừa sync: roster tụt {previous}/{capacity} → {current}/{capacity} 😭 "
            : $"Tui vừa sync: roster lên {previous}/{capacity} → {current}/{capacity} 😎 ";
    }
}

public sealed partial class ZaloOverbookService
{
    public async Task<int> ProcessDraftPreparationRemindersDueV2Async(
        CancellationToken cancellationToken = default)
    {
        var settings = DraftAutopilotSettings.FromConfiguration(configuration);
        if (!settings.Enabled || !settings.ProactiveEnabled || !settings.EscalationEnabled) return 0;

        var now = DateTimeOffset.UtcNow;
        var sessions = await db.MatchSessions
            .AsNoTracking()
            .Include(item => item.ZaloConnection)
            .Where(item => item.BotEnabled &&
                           item.ZaloConnection != null &&
                           item.ZaloConnectionId != null &&
                           item.ZaloGroupId != null &&
                           item.StartTime != null &&
                           (item.Status == SessionStatus.Setup || item.Status == SessionStatus.CaptainSelection))
            .ToListAsync(cancellationToken);

        var candidates = sessions
            .Select(session => new
            {
                Session = session,
                Bucket = ZaloDraftPreparationReminderPolicy.GetDueBucket(
                    session.StartTime!.Value,
                    now,
                    settings.StopNudgingMinutesBeforeStart)
            })
            .Where(item => item.Bucket is not null)
            .GroupBy(
                item => $"{item.Session.ZaloConnectionId}:{item.Session.ZaloGroupId}",
                StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.Session.StartTime).First())
            .OrderBy(item => item.Session.StartTime)
            .Take(30)
            .ToList();

        var reminderStore = new ZaloDraftPreparationReminderStore(db);
        var decisionStore = new ZaloDraftPreparationDecisionStore(db);
        var tagStore = new ZaloDraftReminderTagPreferenceStore(db);
        var escalationStore = new ZaloDraftEscalationStore(db);
        var sent = 0;

        foreach (var candidate in candidates)
        {
            if (sent >= settings.MaxSendsPerCycle) break;
            var session = candidate.Session;
            var bucket = candidate.Bucket!;
            var previous = await reminderStore.GetAsync(session.Id, cancellationToken);
            if (string.Equals(previous?.LastBucketKey, bucket.Key, StringComparison.Ordinal)) continue;

            var sync = await RefreshLinkedPollForDraftReminderAsync(session, cancellationToken);
            if (!sync.Success)
            {
                logger.LogWarning(
                    "Leader-aware draft reminder postponed because linked poll sync failed Session={SessionId} Reason={Reason}",
                    session.Id,
                    sync.Error);
                continue;
            }

            var readiness = await new ZaloDraftReadinessService(db)
                .BuildAsync(session.Id, now, cancellationToken);
            if (readiness is null) continue;
            var activeSlotRisks = await CountActiveSlotRisksAsync(session, cancellationToken);

            var decision = await decisionStore.GetAsync(session.Id, cancellationToken);
            var decisionWasStale = false;
            int? staleDecisionSlotCount = null;
            if (decision?.Kind == ZaloDraftPreparationDecisionKind.PlayCurrentRoster &&
                !decision.MatchesRoster(readiness))
            {
                decisionWasStale = true;
                staleDecisionSlotCount = decision.EffectiveSlotCount;
                await decisionStore.ClearAsync(session.Id, cancellationToken);
                decision = null;
            }
            else if (decision?.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting &&
                     readiness.EffectiveSlotCount >= readiness.Capacity &&
                     activeSlotRisks == 0)
            {
                await decisionStore.ClearAsync(session.Id, cancellationToken);
                decision = null;
            }

            if (decision?.Kind == ZaloDraftPreparationDecisionKind.StopMatch)
            {
                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket.Key,
                    readiness.EffectiveSlotCount,
                    activeSlotRisks,
                    readiness.Fingerprint,
                    null,
                    cancellationToken);
                continue;
            }

            var existingRequest = await escalationStore.LoadForSessionAsync(
                session.ZaloConnectionId!,
                session.ZaloGroupId!,
                session.Id,
                cancellationToken);

            if (existingRequest is not null &&
                existingRequest.State == ZaloDraftEscalationState.Completed &&
                string.Equals(existingRequest.RosterFingerprint, readiness.Fingerprint, StringComparison.Ordinal))
            {
                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket.Key,
                    readiness.EffectiveSlotCount,
                    activeSlotRisks,
                    readiness.Fingerprint,
                    null,
                    cancellationToken);
                continue;
            }

            if (existingRequest is not null &&
                (existingRequest.State is ZaloDraftEscalationState.AwaitingRequesterConsent or
                                          ZaloDraftEscalationState.ProactiveSoft or
                                          ZaloDraftEscalationState.ApproverTagged or
                                          ZaloDraftEscalationState.Executing) &&
                (!readiness.CanEscalate ||
                 activeSlotRisks > 0 ||
                 !string.Equals(existingRequest.RosterFingerprint, readiness.Fingerprint, StringComparison.Ordinal)))
            {
                await SupersedeDraftReminderRequestAsync(
                    escalationStore,
                    existingRequest,
                    session,
                    cancellationToken);
                existingRequest = null;
            }

            var body = ZaloLeaderAwareDraftReminderPolicy.BuildMessage(
                session,
                readiness,
                decision,
                decisionWasStale,
                staleDecisionSlotCount,
                previous?.LastSlotCount,
                activeSlotRisks,
                bucket.Urgent);
            if (string.IsNullOrWhiteSpace(body))
            {
                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket.Key,
                    readiness.EffectiveSlotCount,
                    activeSlotRisks,
                    readiness.Fingerprint,
                    null,
                    cancellationToken);
                continue;
            }

            // Reuse the same authoritative lifecycle snapshot the admin control room
            // uses. This turns the reminder into an exception-first Match Brief without
            // inventing a second state machine or another proactive message lane.
            var lifecycle = await new MatchLifecycleCoordinator(db)
                .GetAsync(session.AdminUserId, session.Id, cancellationToken);
            if (lifecycle.IsSuccess && lifecycle.Value is not null)
                body = ZaloMatchBriefFormatter.Append(body, lifecycle.Value);

            var resolved = await ResolveDraftApproversAsync(session, settings, cancellationToken);
            if (!resolved.RoleLookupSucceeded)
            {
                logger.LogWarning(
                    "Leader-aware draft reminder could not refresh live organizer roles Session={SessionId} Reason={Reason}",
                    session.Id,
                    resolved.Error ?? "draft_role_lookup_failed");
                continue;
            }

            var savedPreferences = await tagStore.GetForGroupAsync(
                session.ZaloConnectionId!,
                session.ZaloGroupId!,
                cancellationToken);
            var preferenceById = savedPreferences
                .GroupBy(item => item.ZaloUserId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var eligible = resolved.Candidates
                .Where(item =>
                    preferenceById.TryGetValue(item.ZaloUserId, out var preference)
                        ? preference.Enabled
                        : item.IsCreator)
                .ToList();

            var desiredTags = bucket.Urgent || activeSlotRisks > 0 ? 2 : 1;
            desiredTags = Math.Min(desiredTags, settings.MaxApproverTags);
            if (eligible.Count == 0 || desiredTags <= 0)
            {
                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket.Key,
                    readiness.EffectiveSlotCount,
                    activeSlotRisks,
                    readiness.Fingerprint,
                    null,
                    cancellationToken);
                continue;
            }

            IReadOnlyList<DraftApproverCandidate> recipients;
            ZaloDraftEscalationSnapshot? approvalRequest = null;
            DateTimeOffset? approvalExpiry = null;

            if (readiness.CanEscalate && activeSlotRisks == 0)
            {
                approvalExpiry = GetRequestExpiry(
                    readiness.StartTime,
                    now,
                    settings,
                    settings.RequestTtlMinutes);
                approvalRequest = existingRequest;
                if (approvalRequest is null ||
                    approvalRequest.State is ZaloDraftEscalationState.Expired or
                                             ZaloDraftEscalationState.Superseded or
                                             ZaloDraftEscalationState.Cancelled)
                {
                    approvalRequest = await escalationStore.CreateOrReuseAsync(
                        session.ZaloConnectionId!,
                        session.ZaloGroupId!,
                        session.Id,
                        "PreparationReminderV2",
                        null,
                        null,
                        null,
                        readiness.Fingerprint,
                        ZaloDraftEscalationState.ProactiveSoft,
                        approvalExpiry.Value,
                        cancellationToken);
                }

                var reserved = new List<DraftApproverCandidate>();
                foreach (var approver in eligible)
                {
                    if (reserved.Count >= desiredTags) break;
                    if (!await SeedDraftConfirmationAsync(
                            session.ZaloConnectionId!,
                            session.ZaloGroupId!,
                            approver.ZaloUserId,
                            session.Id,
                            approvalExpiry.Value,
                            cancellationToken,
                            refuseToOverwriteDifferentPending: true))
                        continue;
                    reserved.Add(approver);
                }
                recipients = reserved;
            }
            else
            {
                recipients = eligible.Take(desiredTags).ToList();
            }

            if (recipients.Count == 0)
            {
                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket.Key,
                    readiness.EffectiveSlotCount,
                    activeSlotRisks,
                    readiness.Fingerprint,
                    null,
                    cancellationToken);
                continue;
            }

            var ids = recipients.Select(item => item.ZaloUserId).ToList();
            var names = recipients.ToDictionary(
                item => item.ZaloUserId,
                item => item.DisplayName,
                StringComparer.Ordinal);
            var outgoing = BuildMentionMessage(ids, names, body);

            try
            {
                var providerId = await SendDraftProactiveAsync(
                    session,
                    outgoing.Message,
                    outgoing.Mentions,
                    $"draft-prep-v2:{session.Id}:{bucket.Key}",
                    cancellationToken);

                if (approvalRequest is not null && approvalExpiry is not null)
                {
                    await escalationStore.SetPrimaryApproverAsync(
                        approvalRequest.Id,
                        recipients[0].ZaloUserId,
                        providerId,
                        now,
                        approvalExpiry.Value,
                        cancellationToken);
                    if (recipients.Count > 1)
                    {
                        await escalationStore.SetSecondaryApproverAsync(
                            approvalRequest.Id,
                            recipients[1].ZaloUserId,
                            providerId,
                            now,
                            approvalExpiry.Value,
                            cancellationToken);
                    }
                }

                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket.Key,
                    readiness.EffectiveSlotCount,
                    activeSlotRisks,
                    readiness.Fingerprint,
                    now,
                    cancellationToken);
                sent += 1;
            }
            catch
            {
                if (approvalRequest is not null)
                {
                    foreach (var recipient in recipients)
                    {
                        await RemoveDraftPendingAsync(
                            session.ZaloConnectionId!,
                            session.ZaloGroupId!,
                            recipient.ZaloUserId,
                            session.Id,
                            cancellationToken);
                    }
                }
                throw;
            }
        }

        return sent;
    }
}