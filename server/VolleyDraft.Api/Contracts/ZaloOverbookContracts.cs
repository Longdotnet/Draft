namespace VolleyDraft.Api.Contracts;

public enum ZaloOverbookMessageSource
{
    AdminPool,
    Ai
}

public sealed record UpdateZaloOverbookSettingsRequest(
    bool Enabled,
    int GraceMinutes,
    int ReminderIntervalMinutes,
    int MaxReminders,
    ZaloOverbookMessageSource MessageSource,
    IReadOnlyList<string>? FriendlyMessages,
    IReadOnlyList<string>? SeriousMessages,
    IReadOnlyList<string>? StrictMessages);

public sealed record ConfirmZaloOverbookTargetsRequest(IReadOnlyList<string> ZaloUserIds);

public sealed record ZaloOverbookVoterResponse(
    string ZaloUserId,
    string DisplayName,
    int VotePosition,
    bool SuggestedExcess,
    bool ConfirmedExcess,
    bool IsSharedSlotMember);

public sealed record ZaloOverbookStatusResponse(
    string SessionId,
    string SessionName,
    string? ZaloGroupName,
    bool BotEnabled,
    bool Enabled,
    int Capacity,
    int EffectiveSlotCount,
    int RawVoterCount,
    int ExcessSlotCount,
    int GraceMinutes,
    int ReminderIntervalMinutes,
    int MaxReminders,
    ZaloOverbookMessageSource MessageSource,
    IReadOnlyList<string> FriendlyMessages,
    IReadOnlyList<string> SeriousMessages,
    IReadOnlyList<string> StrictMessages,
    string OrderConfidence,
    bool NeedsConfirmation,
    int ReminderCount,
    DateTimeOffset? LastReminderAt,
    DateTimeOffset? NextReminderAt,
    IReadOnlyList<ZaloOverbookVoterResponse> Voters,
    IReadOnlyList<string> CurrentTargetZaloUserIds,
    string? LastError);
