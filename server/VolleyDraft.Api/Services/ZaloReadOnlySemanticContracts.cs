namespace VolleyDraft.Api.Services;

internal enum ZaloReadOnlySemanticRoute
{
    None,
    GeneralChat,
    ReadOnlyQuestion,
    MutationRequest
}

internal enum ZaloReadOnlyFactKind
{
    None,
    SessionSchedule,
    SelfMembership,
    LocationParking,
    MissingSlots,
    UpcomingSessions,
    Roster,
    WeeklySessionCount,
    TeamLineup,
    ReminderStatus,
    WaitlistStatus,
    TeamPreference,
    MemberTeam,
    MemberMembership,
    CanMemberTakeSlot
}

internal sealed record ZaloReadOnlySemanticSettings(
    bool Enabled,
    double MinimumConfidence,
    int MaxContextMessages,
    int MaxUserCallsPerMinute,
    int MaxGroupCallsPerMinute)
{
    public static ZaloReadOnlySemanticSettings FromConfiguration(IConfiguration configuration) => new(
        Enabled: configuration.GetValue("ZaloBot:Ambient:ReadOnlySemanticAi:Enabled", true),
        MinimumConfidence: Math.Clamp(
            configuration.GetValue("ZaloBot:Ambient:ReadOnlySemanticAi:MinimumConfidence", .85),
            .60,
            .99),
        MaxContextMessages: Math.Clamp(
            configuration.GetValue("ZaloBot:Ambient:ReadOnlySemanticAi:MaxContextMessages", 12),
            3,
            24),
        MaxUserCallsPerMinute: Math.Clamp(configuration.GetValue("ZaloBot:AiPerUserPerMinute", 4), 1, 20),
        MaxGroupCallsPerMinute: Math.Clamp(configuration.GetValue("ZaloBot:AiPerGroupPerMinute", 20), 1, 100));
}

internal sealed record ZaloReadOnlyGroundingSession(
    string SessionId,
    string Name,
    DateTimeOffset? StartTime,
    string? Location,
    string Status,
    int Capacity,
    int PlayerCount);

internal sealed record ZaloReadOnlyGroundingMember(
    string MemberId,
    string SessionId,
    string? PlayerProfileId,
    string? ZaloUserId,
    string DisplayName,
    bool IsPresent);

internal sealed record ZaloReadOnlyGroundingTeam(
    string TeamId,
    string TeamName,
    string SessionId,
    IReadOnlyList<string> MemberIds);

internal sealed record ZaloReadOnlyGroundingWaitlistEntry(
    string EntryId,
    string SessionId,
    string? MemberId,
    string ZaloUserId,
    string DisplayName,
    string Status);

internal sealed record ZaloReadOnlyGroundingOffer(
    string OfferId,
    string OwnerZaloUserId,
    string OwnerDisplayName,
    string SessionId,
    string SessionName,
    string? SourceMessageId,
    string Status);

internal sealed record ZaloReadOnlyGroundingReminder(
    string SessionId,
    int EnabledCount,
    DateTimeOffset? NextRunAt);

internal sealed record ZaloReadOnlyGroundingSnapshot(
    IReadOnlyList<ZaloReadOnlyGroundingSession> Sessions,
    IReadOnlyList<ZaloReadOnlyGroundingMember> Members,
    IReadOnlyList<ZaloReadOnlyGroundingTeam> Teams,
    IReadOnlyList<ZaloReadOnlyGroundingWaitlistEntry> Waitlist,
    IReadOnlyList<ZaloReadOnlyGroundingOffer> OpenOffers,
    IReadOnlyList<ZaloReadOnlyGroundingReminder> Reminders);

internal sealed record ZaloReadOnlySemanticPlan(
    ZaloReadOnlySemanticRoute Route,
    ZaloReadOnlyFactKind FactKind,
    double Confidence,
    string? SessionId,
    string? SubjectMemberId,
    bool SubjectIsCurrentSender,
    string? ReferencedMemberId,
    string? SourceMessageId,
    string? OpenOfferId,
    bool NeedsClarification,
    string Reason)
{
    public static ZaloReadOnlySemanticPlan None(string reason) => new(
        ZaloReadOnlySemanticRoute.None,
        ZaloReadOnlyFactKind.None,
        0,
        null,
        null,
        false,
        null,
        null,
        null,
        false,
        reason);
}

internal sealed record ZaloReadOnlyPlanValidationResult(
    bool Accepted,
    string Reason,
    ZaloReadOnlySemanticPlan Plan)
{
    public static ZaloReadOnlyPlanValidationResult Reject(ZaloReadOnlySemanticPlan plan, string reason) =>
        new(false, reason, plan);

    public static ZaloReadOnlyPlanValidationResult Accept(ZaloReadOnlySemanticPlan plan) =>
        new(true, "semantic_readonly_accepted", plan);
}
