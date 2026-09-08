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
        bool urgent) =>
        BuildMessageCore(
            session,
            readiness,
            decision,
            decisionWasStale,
            staleDecisionSlotCount,
            previousObservedSlotCount,
            readiness.ActivePassSlotRiskCount,
            urgent);

    // Compatibility overload for focused policy tests and older in-process callers.
    // Production reminder execution uses the overload above so pass/share authority
    // comes from the same readiness snapshot as roster/profile/draft state.
    internal static string? BuildMessage(
        MatchSession session,
        ZaloDraftReadinessSnapshot readiness,
        ZaloDraftPreparationDecisionSnapshot? decision,
        bool decisionWasStale,
        int? staleDecisionSlotCount,
        int? previousObservedSlotCount,
        int activeSlotRiskCount,
        bool urgent) =>
        BuildMessageCore(
            session,
            readiness,
            decision,
            decisionWasStale,
            staleDecisionSlotCount,
            previousObservedSlotCount,
            activeSlotRiskCount,
            urgent);

    private static string? BuildMessageCore(
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
        var peopleLabel = readiness.PresentPlayerCount == count
            ? $"{count}/{capacity} chỗ"
            : $"{readiness.PresentPlayerCount} người, tính ra {count}/{capacity} chỗ để chia đội";

        if (activeSlotRiskCount > 0)
        {
            var risk = activeSlotRiskCount == 1
                ? "1 chỗ đang nhường/chờ nhận"
                : $"{activeSlotRiskCount} chỗ đang nhường/chờ nhận";
            return $"Tui vừa kiểm tra {name}: {peopleLabel}, còn {risk} chưa xong nên chưa chia đội nha. " +
                   "Người nhường đổi ý dùng `huỷ pass`; người nhận đã vote đúng kèo dùng `xong`; người đang giữ lượt nhận muốn nhả dùng `huỷ nhận`. " +
                   "Xử lý xong tui sẽ đọc lại vote và danh sách thật rồi mới cho đi tiếp.";
        }

        if (decision?.Kind == ZaloDraftPreparationDecisionKind.StopMatch)
            return null;

        if (decision?.Kind == ZaloDraftPreparationDecisionKind.PlayCurrentRoster &&
            decision.MatchesRoster(readiness))
        {
            if (readiness.MissingProfileCount > 0)
            {
                return $"Kèo {name} đã được trưởng/phó chốt vẫn chơi với {peopleLabel}, nhưng còn {readiness.MissingProfileCount} người thiếu thông tin để chia đội: {string.Join(", ", readiness.MissingProfileNames.Take(6))}. Bổ sung nốt giúp tui rồi mới chia đội nha 😆";
            }

            if (ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(count, teamCount))
            {
                var perTeam = count / teamCount;
                return urgent
                    ? $"{name} vẫn giữ đúng danh sách đã chốt: {peopleLabel} → {teamCount} đội x{perTeam} ✅ Sát giờ rồi; nếu muốn chia đội nói `draft đi`, tui đọc lại vote lần cuối rồi chạy."
                    : $"{name} vẫn đúng danh sách trưởng/phó đã chốt: {peopleLabel} → {teamCount} đội x{perTeam} 👌 Không cần kiếm thêm nữa; khi muốn chia đội nói `draft đi`.";
            }

            return $"{name} vẫn giữ quyết định chơi với {peopleLabel} 👌 Nhưng {count} chỗ hiện tại chưa chia đều được {teamCount} đội. Kèo vẫn chơi theo quyết định trưởng/phó; nếu muốn bot tự chia đội thì cần xử lý các chỗ dùng chung/luân phiên hoặc để số chỗ chia hết cho {teamCount}.";
        }

        var stalePrefix = decisionWasStale
            ? $"Danh sách {name} vừa đổi so với lúc chốt{(staleDecisionSlotCount is null ? string.Empty : $" {staleDecisionSlotCount} chỗ")}, nên quyết định cũ không còn khớp nữa nha. "
            : string.Empty;

        if (readiness.State == ZaloDraftReadinessState.RosterOverCapacity)
        {
            return $"{stalePrefix}Tui vừa đọc lại vote {name}: đang {count}/{capacity} chỗ, dư {Math.Max(1, count - capacity)} chỗ 😭 Chưa thể chốt danh sách; xử lý người/chỗ dư trước, xong tui sẽ đọc lại vote rồi báo bước tiếp theo.";
        }

        if (readiness.State == ZaloDraftReadinessState.Ready)
        {
            var recruitmentMemory = decision?.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting
                ? " Tạm ngưng gọi thêm người vì kèo đã đủ; nếu sau đó lại thiếu chỗ, tui tiếp tục kiếm theo quyết định trước, không bắt trưởng/phó chốt lại."
                : string.Empty;
            return urgent
                ? $"{stalePrefix}Tui vừa đọc lại vote {name}: đủ {count}/{capacity} chỗ ✅ đội vẫn chưa chia.{recruitmentMemory} Sát giờ rồi, nói `draft đi` là tui kiểm tra vote lần cuối rồi chạy."
                : $"{stalePrefix}Tui vừa đọc lại vote {name}: đủ {count}/{capacity} chỗ rồi nha ✅ Đội chưa chia.{recruitmentMemory} Nói `draft đi` là tui kiểm tra vote lần cuối rồi chạy.";
        }

        if (decision?.Kind == ZaloDraftPreparationDecisionKind.KeepRecruiting && count < capacity)
        {
            var change = BuildChange(previousObservedSlotCount, count, capacity);
            var missing = Math.Max(0, capacity - count);
            return urgent
                ? $"{change}Trưởng/phó đã chốt tiếp tục kiếm thêm; hiện còn thiếu {missing} chỗ. Tui tiếp tục theo dõi vote nha 🚨 có thay đổi tui báo ngay."
                : $"{change}Trưởng/phó đã chốt tiếp tục kiếm thêm; hiện còn thiếu {missing} chỗ. Tui tiếp tục theo dõi thay đổi, không hỏi lại cùng một quyết định mỗi lượt 😆";
        }

        if (readiness.State == ZaloDraftReadinessState.NoRoster)
        {
            return $"{stalePrefix}Tui vừa đọc lại đúng vote của {name}: đang 0/{capacity} chỗ. Trưởng/phó cho tui hướng xử lý kèo nha; nếu vẫn gom người thì nói `kiếm thêm`, tui sẽ tiếp tục theo dõi vote.";
        }

        if (readiness.State != ZaloDraftReadinessState.RosterNotFull)
        {
            if (readiness.MissingProfileCount > 0)
                return $"{name} còn {readiness.MissingProfileCount} người thiếu thông tin để chia đội: {string.Join(", ", readiness.MissingProfileNames.Take(6))}. Bổ sung nốt giúp tui trước khi chia đội.";
            return null;
        }

        var changePrefix = stalePrefix + BuildChange(previousObservedSlotCount, count, capacity);
        var even = ZaloDraftPreparationDecisionPolicy.CanAutoDraftEvenly(count, teamCount);
        if (even)
        {
            var perTeam = count / teamCount;
            return urgent
                ? $"{changePrefix}{peopleLabel} vẫn chia được {teamCount} đội x{perTeam}. Giờ sát giờ rồi 🚨 trưởng/phó chốt giúp: `chốt {count}` / `{count} vẫn đánh`, hoặc `kiếm thêm`. Tui làm theo quyết định đó, không tự đoán theo số người."
                : $"{changePrefix}{peopleLabel} vẫn chia được {teamCount} đội x{perTeam} nha. Trưởng/phó chốt giúp `chốt {count}` / `{count} vẫn đánh`, hoặc nói `kiếm thêm`; tui làm đúng quyết định đó.";
        }

        return urgent
            ? $"{changePrefix}{peopleLabel}. Kèo vẫn có thể chơi nếu trưởng/phó muốn, nhưng {count} chỗ hiện tại chưa chia đều {teamCount} đội 🚨 Nếu giữ danh sách hiện tại nói `vẫn đánh`; nếu tiếp tục tuyển nói `kiếm thêm`. Muốn bot tự chia đội thì cần xử lý chỗ dùng chung/luân phiên hoặc để số chỗ chia hết cho {teamCount}."
            : $"{changePrefix}{peopleLabel}. Trưởng/phó có thể nói `vẫn đánh` hoặc `kiếm thêm`; nếu muốn bot tự chia đội thì {count} chỗ hiện tại phải chia đều cho {teamCount} đội, nên cần xử lý chỗ dùng chung/luân phiên hoặc chờ danh sách đổi trước.";
    }

    private static string BuildChange(int? previous, int current, int capacity)
    {
        if (previous is null || previous == current)
            return $"Tui vừa đọc lại vote: hiện {current}/{capacity} chỗ. ";
        return previous > current
            ? $"Danh sách vừa giảm từ {previous}/{capacity} xuống {current}/{capacity} chỗ 😭 "
            : $"Danh sách vừa tăng từ {previous}/{capacity} lên {current}/{capacity} chỗ 😎 ";
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
            var sameBucket = string.Equals(previous?.LastBucketKey, bucket.Key, StringComparison.Ordinal);
            if (sameBucket && previous is not null &&
                !ZaloDraftPreparationReminderObservation.ShouldRefreshSameBucket(previous, now))
                continue;

            // A time bucket is only a cadence boundary, not authority that the product
            // state is unchanged. Re-check same-bucket state on a bounded cadence, then
            // suppress only when roster/pass/profile/readiness state is materially identical.
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

            // Readiness owns unresolved pass/share authority. Keep the entire reminder
            // decision, anti-spam fingerprint and escalation gate on this one coherent
            // snapshot instead of issuing a second ledger query that can race it.
            var activeSlotRisks = readiness.ActivePassSlotRiskCount;
            var observationFingerprint = ZaloDraftPreparationReminderObservation.BuildFingerprint(
                readiness,
                activeSlotRisks);

            if (sameBucket && previous is not null &&
                !ZaloDraftPreparationReminderObservation.HasMaterialChange(previous, readiness, activeSlotRisks))
            {
                // Touch unchanged observations so the worker does not degrade into a
                // provider poll every heavy cycle. Legacy rows are upgraded silently too.
                await reminderStore.UpdateObservationFingerprintAsync(
                    session.Id,
                    observationFingerprint,
                    cancellationToken);
                continue;
            }

            var decision = await decisionStore.GetAsync(session.Id, cancellationToken);
            var decisionWasStale = false;
            int? staleDecisionSlotCount = null;
            if (decision?.Kind == ZaloDraftPreparationDecisionKind.PlayCurrentRoster &&
                !decision.MatchesRoster(readiness))
            {
                decisionWasStale = true;
                staleDecisionSlotCount = decision.EffectiveSlotCount;
                if (!await decisionStore.TryClearAsync(session.Id, decision, cancellationToken))
                {
                    // A leader/deputy replaced the decision after this reminder read it.
                    // Preserve the newer user action and let the next cycle recompute from it.
                    continue;
                }
                decision = null;
            }

            // KeepRecruiting is a durable organizer direction, not a snapshot tied to the
            // current count. When the roster becomes full, suppress recruiting traffic but
            // keep the decision so a later pass/unvote can resume recruitment without making
            // the organizer repeat the same choice. The group-wide recruitment lane and this
            // leader-aware reminder lane must share that ownership contract.

            if (decision?.Kind == ZaloDraftPreparationDecisionKind.StopMatch)
            {
                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket.Key,
                    readiness.EffectiveSlotCount,
                    activeSlotRisks,
                    observationFingerprint,
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
                    observationFingerprint,
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
                bucket.Urgent);
            if (string.IsNullOrWhiteSpace(body))
            {
                await reminderStore.MarkHandledAsync(
                    session.Id,
                    bucket.Key,
                    readiness.EffectiveSlotCount,
                    activeSlotRisks,
                    observationFingerprint,
                    null,
                    cancellationToken);
                continue;
            }

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
                    observationFingerprint,
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
                    observationFingerprint,
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
                    $"draft-prep-v2:{session.Id}:{bucket.Key}:{ZaloDraftPreparationReminderObservation.BuildIdempotencySuffix(readiness, activeSlotRisks)}",
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
                    observationFingerprint,
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
