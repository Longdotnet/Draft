namespace VolleyDraft.Api.Services;

internal enum ZaloSemanticActionRoute
{
    None,
    GeneralChat,
    ReadOnlyQuestion,
    MutationRequest
}

internal enum ZaloSemanticActionKind
{
    None,
    PassOwnSlot,
    ClaimOpenSlot,
    CancelPass,
    CancelClaim,
    ConfirmClaim
}

internal enum ZaloSemanticActionActorKind
{
    None,
    CurrentSender
}

internal enum ZaloSemanticActionTargetDisposition
{
    Apply,
    Exclude,
    Uncertain
}

internal sealed record ZaloSemanticActionSettings(
    bool Enabled,
    double MinimumConfidence,
    int MaxContextMessages,
    int MaxUserCallsPerMinute,
    int MaxGroupCallsPerMinute)
{
    public static ZaloSemanticActionSettings FromConfiguration(IConfiguration configuration) => new(
        Enabled: configuration.GetValue("ZaloBot:Ambient:ActionSemanticAi:Enabled", true),
        MinimumConfidence: Math.Clamp(
            configuration.GetValue("ZaloBot:Ambient:ActionSemanticAi:MinimumConfidence", .85),
            .60,
            .99),
        MaxContextMessages: Math.Clamp(
            configuration.GetValue("ZaloBot:Ambient:ActionSemanticAi:MaxContextMessages", 12),
            3,
            24),
        MaxUserCallsPerMinute: Math.Clamp(configuration.GetValue("ZaloBot:AiPerUserPerMinute", 4), 1, 20),
        MaxGroupCallsPerMinute: Math.Clamp(configuration.GetValue("ZaloBot:AiPerGroupPerMinute", 20), 1, 100));
}

internal sealed record ZaloActionGroundingSession(
    string SessionId,
    string Name,
    DateTimeOffset? StartTime,
    string? LocalDate,
    string? LocalTime,
    string Status,
    int Capacity,
    int PlayerCount,
    string GroupId);

internal sealed record ZaloActionGroundingOffer(
    string OfferId,
    string OwnerZaloUserId,
    string OwnerDisplayName,
    string SessionId,
    string SessionName,
    string? SourceMessageId,
    string? ClaimantZaloUserId,
    string? ClaimantDisplayName,
    string Status);

internal sealed record ZaloActionGroundingSender(
    string ZaloUserId,
    string? MemberId,
    IReadOnlyList<string> OwnedSessionIds);

internal sealed record ZaloActionGroundingSnapshot(
    DateTimeOffset CurrentUtc,
    DateTimeOffset CurrentLocalDateTime,
    string TimeZone,
    IReadOnlyList<ZaloActionGroundingSession> Sessions,
    IReadOnlyList<ZaloReadOnlyGroundingMember> Members,
    IReadOnlyList<ZaloActionGroundingOffer> OpenSlotOffers,
    ZaloActionGroundingSender CurrentSender);

internal sealed record ZaloSemanticActionTarget(
    string ReferenceText,
    string? ResolvedDate,
    string? SessionId,
    string? ReferencedMemberId,
    string? OpenOfferId,
    ZaloSemanticActionTargetDisposition Disposition,
    double Confidence);

internal sealed record ZaloSemanticActionPlan(
    ZaloSemanticActionRoute Route,
    ZaloSemanticActionKind Action,
    double Confidence,
    ZaloSemanticActionActorKind ActorKind,
    string? ActorMemberId,
    IReadOnlyList<ZaloSemanticActionTarget> Targets,
    bool NeedsClarification,
    string Reason)
{
    public static ZaloSemanticActionPlan None(string reason) => new(
        ZaloSemanticActionRoute.None,
        ZaloSemanticActionKind.None,
        0,
        ZaloSemanticActionActorKind.None,
        null,
        [],
        false,
        reason);
}

internal sealed record ZaloSemanticActionValidatedTarget(
    ZaloSemanticActionTarget Target,
    bool Executable,
    string Code);

internal sealed record ZaloSemanticActionPlanValidationResult(
    bool Accepted,
    string Reason,
    ZaloSemanticActionPlan Plan,
    IReadOnlyList<ZaloSemanticActionValidatedTarget> Targets)
{
    public static ZaloSemanticActionPlanValidationResult Reject(ZaloSemanticActionPlan plan, string reason) =>
        new(false, reason, plan, []);

    public static ZaloSemanticActionPlanValidationResult Accept(
        ZaloSemanticActionPlan plan,
        IReadOnlyList<ZaloSemanticActionValidatedTarget> targets) =>
        new(true, "semantic_action_accepted", plan, targets);
}

internal enum ZaloSemanticActionExecutionStatus
{
    Success,
    Rejected,
    Skipped
}

internal sealed record ZaloSemanticActionTargetResult(
    ZaloSemanticActionTarget Target,
    ZaloSemanticActionExecutionStatus Status,
    string Code,
    string? Message,
    string? SessionId,
    string? OpenOfferId);

internal sealed record ZaloSemanticActionExecutionResult(
    ZaloSemanticActionKind Action,
    IReadOnlyList<ZaloSemanticActionTargetResult> Results)
{
    public bool HasSuccess => Results.Any(result => result.Status == ZaloSemanticActionExecutionStatus.Success);
    public bool HasFailure => Results.Any(result => result.Status == ZaloSemanticActionExecutionStatus.Rejected);
}
