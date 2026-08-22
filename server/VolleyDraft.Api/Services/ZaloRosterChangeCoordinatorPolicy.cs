namespace VolleyDraft.Api.Services;

internal enum ZaloRosterObservationTransitionKind
{
    Baseline,
    Unchanged,
    Increased,
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
            var kind = previous.PendingDropStartedAt is null
                ? ZaloRosterObservationTransitionKind.Increased
                : ZaloRosterObservationTransitionKind.DropBounced;
            return new(kind, previous with
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

    internal static string BuildSoftUpdate(
        ZaloDraftReadinessSnapshot readiness,
        int from,
        int to,
        int activeSlotRiskCount)
    {
        var risk = activeSlotRiskCount > 0
            ? $" Đang có {activeSlotRiskCount} slot pass/huỷ được xử lý riêng nên tui không réo trùng người."
            : string.Empty;
        return $"Tui vừa sync {readiness.SessionName}: roster tụt {from}/{readiness.Capacity} → {to}/{readiness.Capacity}. Tin @all tuyển gần đây vẫn còn mới nên tui không @all lại để khỏi spam.{risk} Tui vẫn canh poll; ai vào được cứ vote/chốt trên poll nha.";
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
