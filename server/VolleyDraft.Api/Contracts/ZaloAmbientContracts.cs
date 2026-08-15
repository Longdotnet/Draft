namespace VolleyDraft.Api.Contracts;

public sealed record ZaloAmbientShadowMetricsResponse(
    string SessionId,
    string GroupId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    bool AmbientEnabled,
    bool ShadowMode,
    int WouldReplyThreshold,
    int ObservedCount,
    int WouldReplyCount,
    double WouldReplyRate,
    double AverageScore,
    int HighConfidenceFactCount,
    IReadOnlyDictionary<string, int> CandidateKinds,
    IReadOnlyDictionary<string, int> TopIntents,
    IReadOnlyDictionary<string, int> SuppressionReasons);
