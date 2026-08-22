using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal enum ZaloSemanticGuestActionKind
{
    None,
    AddGuests,
    UpdateGuestProfiles,
    CancelGuests
}

internal enum ZaloSemanticGuestAnchorKind
{
    None,
    RecruitmentBroadcast,
    GuestConversation,
    ActiveGuestConversation,
    PendingGuestAction,
    RecentGuestMutation
}

internal sealed record ZaloSemanticGuestField<T>(T? Value, double Confidence) where T : struct;

internal sealed record ZaloSemanticGuestPlanItem(
    string ReferenceText,
    string? ReservationId,
    int? SponsorSequence,
    string? DisplayName,
    double NameConfidence,
    PlayerGender? Gender,
    double GenderConfidence,
    PlayerLevel? Level,
    double LevelConfidence,
    PlayerRole? Role,
    double RoleConfidence,
    double Confidence);

internal sealed record ZaloSemanticGuestPlan(
    ZaloSemanticGuestActionKind Action,
    double Confidence,
    int? Quantity,
    double QuantityConfidence,
    IReadOnlyList<ZaloSemanticGuestPlanItem> Guests,
    bool NeedsClarification,
    string ClarificationReason,
    string Reason)
{
    public static ZaloSemanticGuestPlan None(string reason) => new(
        ZaloSemanticGuestActionKind.None,
        0,
        null,
        0,
        [],
        false,
        string.Empty,
        reason);
}

internal sealed record ZaloSemanticGuestGroundingGuest(
    string ReservationId,
    int SponsorSequence,
    string DisplayName,
    PlayerGender? Gender,
    PlayerLevel? Level,
    PlayerRole? Role,
    string Status);

internal sealed record ZaloSemanticGuestGroundingSnapshot(
    string SessionId,
    string SessionName,
    DateTimeOffset? StartTime,
    int EffectiveSlots,
    int Capacity,
    bool AddWindowOpen,
    string SponsorZaloUserId,
    string SponsorDisplayName,
    ZaloSemanticGuestAnchorKind AnchorKind,
    string? RecruitmentMessageId,
    IReadOnlyList<ZaloSemanticGuestGroundingGuest> ExistingGuests,
    IReadOnlyList<string> PendingMissingFields,
    DateTimeOffset CurrentUtc,
    DateTimeOffset CurrentLocal);

internal sealed record ZaloSemanticGuestValidatedItem(
    string? ReservationId,
    int? SponsorSequence,
    string? DisplayName,
    PlayerGender? Gender,
    PlayerLevel? Level,
    PlayerRole? Role);

internal sealed record ZaloSemanticGuestValidationResult(
    bool Accepted,
    string Reason,
    ZaloSemanticGuestActionKind Action,
    int Quantity,
    IReadOnlyList<ZaloSemanticGuestValidatedItem> Items,
    bool NeedsClarification,
    string ClarificationReason)
{
    public static ZaloSemanticGuestValidationResult Reject(
        ZaloSemanticGuestPlan plan,
        string reason,
        string? clarification = null) => new(
        false,
        reason,
        plan.Action,
        plan.Quantity ?? 0,
        [],
        true,
        clarification ?? plan.ClarificationReason);
}

internal sealed record ZaloSemanticGuestMutationPreview(
    int BeforeEffectiveSlots,
    int Capacity,
    int AdmitCount,
    int WaitlistCount,
    int AfterEffectiveSlots);
