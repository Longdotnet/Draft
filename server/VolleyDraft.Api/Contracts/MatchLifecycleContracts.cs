namespace VolleyDraft.Api.Contracts;

public enum MatchLifecycleStage
{
    NeedsSetup,
    AwaitingLeaderDecision,
    Recruiting,
    ResolvingOverbook,
    ResolvingPassSlots,
    AwaitingProfiles,
    ReadyForDraft,
    Drafting,
    Drafted,
    Stopped,
    Cancelled,
    NeedsAttention
}

public enum MatchLifecycleOwner
{
    System,
    ZaloBot,
    Leader,
    AdminWebsite,
    None
}

public sealed record MatchLifecycleResponse(
    string SessionId,
    string SessionName,
    MatchLifecycleStage Stage,
    string StageLabel,
    string Headline,
    string NextStep,
    MatchLifecycleOwner Owner,
    bool NeedsWebsite,
    string? WebTarget,
    string? SuggestedZaloCommand,
    DateTimeOffset? StartTime,
    int PresentPlayerCount,
    int EffectiveSlotCount,
    int Capacity,
    int MissingProfileCount,
    IReadOnlyList<string> MissingProfileNames,
    int ActiveSlotRiskCount,
    string? LeaderDecision,
    string ReasonCode,
    DateTimeOffset EvaluatedAt);
