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
    IReadOnlyList<string>? StrictMessages,
    IReadOnlyDictionary<int, IReadOnlyList<string>>? ReminderMessageBanks = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? StageMessageBanks = null);

public sealed record ConfirmZaloOverbookTargetsRequest(
    IReadOnlyList<string> ZaloUserIds,
    string? ExpectedPollId = null,
    IReadOnlyList<string>? ExpectedSelectedOptionIds = null);

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
    IReadOnlyDictionary<int, IReadOnlyList<string>> ReminderMessageBanks,
    IReadOnlyDictionary<string, IReadOnlyList<string>> StageMessageBanks,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultStageMessageBanks,
    string OrderConfidence,
    bool NeedsConfirmation,
    int ReminderCount,
    DateTimeOffset? LastReminderAt,
    DateTimeOffset? NextReminderAt,
    string? CurrentPollId,
    IReadOnlyList<string> CurrentSelectedOptionIds,
    IReadOnlyList<ZaloOverbookVoterResponse> Voters,
    IReadOnlyList<string> CurrentTargetZaloUserIds,
    string? LastError,
    MatchLifecycleResponse? Lifecycle = null);

public sealed record CopyZaloOverbookSettingsRequest(
    string SourceSessionId,
    IReadOnlyList<string> TargetSessionIds,
    bool CopyMessages = true,
    bool CopyTiming = false,
    bool CopyMaxReminders = false,
    bool CopyMessageSource = false);