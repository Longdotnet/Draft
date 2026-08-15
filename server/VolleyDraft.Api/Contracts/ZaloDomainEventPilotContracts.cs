namespace VolleyDraft.Api.Contracts;

public sealed record ZaloDomainEventPilotReadinessResponse(
    string SessionId,
    string GroupId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    bool PilotEnabled,
    bool SendEnabled,
    bool GlobalShadowMode,
    int ObservedDecisionCount,
    int NarratableCount,
    int SentCount,
    int SuppressedCount,
    int NotEligibleCount,
    IReadOnlyDictionary<string, int> EventKinds,
    IReadOnlyDictionary<string, int> SuppressionReasons,
    bool ReadyForLiveReview,
    IReadOnlyList<string> ReadinessBlockers);
