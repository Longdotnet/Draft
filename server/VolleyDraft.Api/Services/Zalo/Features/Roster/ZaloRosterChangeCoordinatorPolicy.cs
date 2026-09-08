namespace VolleyDraft.Api.Services;

internal enum ZaloRosterObservationTransitionKind
{
    Baseline,
    Unchanged,
    Increased,
    Recovered,
    DropPending,
    DropConfirmed,
    DropBounced
}

internal sealed record ZaloRosterObservationTransition(
    ZaloRosterObservationTransitionKind Kind,
    ZaloRecruitmentRosterObservation State,
    int? DropFrom = null,
    int? DropTo = null);

internal static class ZaloRosterChangeCoordinatorPolicy
{
    internal static TimeSpan GetDebounce(IConfiguration configuration) =>
        TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("ZaloBot:DraftAutopilot:RosterDropDebounceMinutes", 2),
            1,
            5));

    internal static TimeSpan GetRecentBroadcastWindow(IConfiguration configuration) =>
        TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("ZaloBot:DraftAutopilot:RosterDropRecentBroadcastMinutes", 15),
            5,
            30));

    internal static ZaloRosterObservationTransition Observe(
        ZaloRecruitmentRosterObservation? previous,
        string sessionId,
        int effectiveSlots,
        int presentPlayers,
        string fingerprint,
        DateTimeOffset now,
        TimeSpan debounce)
    {
        if (previous is null)
        {
            return new(
                ZaloRosterObservationTransitionKind.Baseline,
                NewState(sessionId, effectiveSlots, presentPlayers, fingerprint, now));
        }

        if (effectiveSlots > previous.StableEffectiveSlotCount)
        {
            if (previous.PendingDropStartedAt is not null)
            {
                return new(
                    ZaloRosterObservationTransitionKind.DropBounced,
                    previous with
                    {
                        StableEffectiveSlotCount = effectiveSlots,
                        StablePresentPlayerCount = presentPlayers,
                        StableFingerprint = fingerprint,
                        PendingDropFromCount = null,
                        PendingDropToCount = null,
                        PendingDropStartedAt = null,
                        LastObservedAt = now,
                        LastDropAt = null,
                        LastDropNotifiedAt = null,
                        LastDropFromCount = null,
                        LastDropToCount = null,
                        UpdatedAt = now
                    });
            }

            var recoveryTarget = previous.LastDropFromCount;
            var recoveredConfirmedDrop = previous.LastDropAt is not null &&
                                         recoveryTarget is not null &&
                                         effectiveSlots >= recoveryTarget.Value;
            if (recoveredConfirmedDrop)
            {
                return new(
                    ZaloRosterObservationTransitionKind.Recovered,
                    previous with
                    {
                        StableEffectiveSlotCount = effectiveSlots,
                        StablePresentPlayerCount = presentPlayers,
                        StableFingerprint = fingerprint,
                        PendingDropFromCount = null,
                        PendingDropToCount = null,
                        PendingDropStartedAt = null,
                        LastObservedAt = now,
                        LastDropAt = null,
                        LastDropNotifiedAt = null,
                        LastDropFromCount = null,
                        LastDropToCount = null,
                        UpdatedAt = now
                    },
                    previous.StableEffectiveSlotCount,
                    effectiveSlots);
            }

            // A confirmed 18→16 incident may recover in more than one step (16→17→18).
            // Keep its provenance until the original stable count is restored so the product
            // can close the user-visible recruitment incident exactly once at full recovery.
            return new(
                ZaloRosterObservationTransitionKind.Increased,
                previous with
                {
                    StableEffectiveSlotCount = effectiveSlots,
                    StablePresentPlayerCount = presentPlayers,
                    StableFingerprint = fingerprint,
                    PendingDropFromCount = null,
                    PendingDropToCount = null,
                    PendingDropStartedAt = null,
                    LastObservedAt = now,
                    UpdatedAt = now
                },
                previous.StableEffectiveSlotCount,
                effectiveSlots);
        }

        if (effectiveSlots == previous.StableEffectiveSlotCount)
        {
            var bounced = previous.PendingDropStartedAt is not null;
            return new(
                bounced ? ZaloRosterObservationTransitionKind.DropBounced : ZaloRosterObservationTransitionKind.Unchanged,
                previous with
                {
                    StablePresentPlayerCount = presentPlayers,
                    StableFingerprint = fingerprint,
                    PendingDropFromCount = null,
                    PendingDropToCount = null,
                    PendingDropStartedAt = null,
                    LastObservedAt = now,
                    UpdatedAt = now
                });
        }

        // Current count is below the last stable roster. Keep the first timestamp so
        // 15→14→13 is coalesced into one incident rather than restarting the debounce.
        var pendingSince = previous.PendingDropStartedAt ?? now;
        var pendingFrom = previous.PendingDropFromCount ?? previous.StableEffectiveSlotCount;
        if (now - pendingSince < debounce)
        {
            return new(
                ZaloRosterObservationTransitionKind.DropPending,
                previous with
                {
                    StablePresentPlayerCount = presentPlayers,
                    StableFingerprint = fingerprint,
                    PendingDropFromCount = pendingFrom,
                    PendingDropToCount = effectiveSlots,
                    PendingDropStartedAt = pendingSince,
                    LastObservedAt = now,
                    UpdatedAt = now
                },
                pendingFrom,
                effectiveSlots);
        }

        return new(
            ZaloRosterObservationTransitionKind.DropConfirmed,
            previous with
            {
                StableEffectiveSlotCount = effectiveSlots,
                StablePresentPlayerCount = presentPlayers,
                StableFingerprint = fingerprint,
                PendingDropFromCount = null,
                PendingDropToCount = null,
                PendingDropStartedAt = null,
                LastObservedAt = now,
                LastDropAt = now,
                LastDropNotifiedAt = null,
                LastDropFromCount = pendingFrom,
                LastDropToCount = effectiveSlots,
                UpdatedAt = now
            },
            pendingFrom,
            effectiveSlots);
    }

    internal static bool IsFullRosterBreak(int from, int to, int capacity) =>
        from >= capacity && to < capacity;

    internal static bool ShouldAnnounceRecoveredReady(
        ZaloRecruitmentRosterObservation previous,
        ZaloRosterObservationTransition transition,
        ZaloDraftReadinessSnapshot readiness) =>
        transition.Kind == ZaloRosterObservationTransitionKind.Recovered &&
        previous.LastDropNotifiedAt is not null &&
        readiness.State == ZaloDraftReadinessState.Ready &&
        readiness.EffectiveSlotCount == readiness.Capacity &&
        readiness.ActivePassSlotRiskCount == 0;

    internal static string BuildRecoveredReadyUpdate(
        ZaloDraftReadinessSnapshot readiness,
        int from,
        int to) =>
        $"Kèo {readiness.SessionName} vừa đủ lại {to}/{readiness.Capacity} chỗ rồi ✅ " +
        "Không còn chỗ đang nhường/chờ nhận. Tui ngưng gọi thêm người. " +
        "Trưởng/phó muốn chia đội thì nói `draft đi`; tui sẽ đọc lại vote lần cuối trước khi chạy.";

    internal static string BuildSoftUpdate(
        ZaloDraftReadinessSnapshot readiness,
        int from,
        int to,
        int activeSlotRiskCount)
    {
        var risk = activeSlotRiskCount > 0
            ? $" Đang có {activeSlotRiskCount} chỗ nhường/huỷ được xử lý riêng nên tui không réo trùng người."
            : string.Empty;
        return $"Tui vừa đọc lại vote {readiness.SessionName}: danh sách tụt {from}/{readiness.Capacity} → {to}/{readiness.Capacity}. Tin @all tuyển gần đây vẫn còn mới nên tui không @all lại để khỏi spam.{risk} Tui vẫn canh bình chọn; ai vào được cứ vote/chốt trên đó nha.";
    }

    private static ZaloRecruitmentRosterObservation NewState(
        string sessionId,
        int effectiveSlots,
        int presentPlayers,
        string fingerprint,
        DateTimeOffset now) => new(
        sessionId,
        effectiveSlots,
        presentPlayers,
        fingerprint,
        null,
        null,
        null,
        now,
        null,
        null,
        null,
        null,
        now);
}
