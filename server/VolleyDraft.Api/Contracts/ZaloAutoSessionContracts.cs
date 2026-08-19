namespace VolleyDraft.Api.Contracts;

public sealed record CreateZaloAutoSessionGroupRequest(
    string ConnectionId,
    string GroupId);

public sealed record UpdateZaloAutoSessionGroupRequest(
    bool AutoSessionEnabled,
    bool RequireOrganizerApproval,
    int DefaultTeamSize,
    int DefaultTotalSets,
    string DefaultStartTime,
    bool AssumePmForHourUnder12,
    string? DefaultLocation,
    bool BotEnabledForCreatedSessions,
    bool? GlobalEnabled = null,
    string? RolloutMode = null,
    string? LearningSignalId = null,
    string? LearningDecision = null,
    string? LearningReviewNote = null,
    string? TrustedOrganizerZaloUserId = null,
    string? TrustedOrganizerDisplayName = null,
    bool? TrustedOrganizerEnabled = null);

public sealed record ZaloAutoSessionHealthResponse(
    string ConnectionStatus,
    DateTimeOffset? LastPollEventAt,
    DateTimeOffset? LastReconcileAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastErrorAt,
    string? LastError,
    int ConsecutiveFailures,
    DateTimeOffset? NextRetryAt);

public sealed record ZaloAutoSessionLearningSignalResponse(
    string Id,
    string PollId,
    string SignalType,
    string? DayKey,
    DateTimeOffset? OriginalStartTime,
    DateTimeOffset? ActualStartTime,
    string? SuggestedRuleType,
    int? SuggestedMinutes,
    string Status,
    string? ReviewNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ZaloAutoSessionOrganizerCandidateResponse(
    string ZaloUserId,
    string DisplayName,
    string ZaloRole,
    bool IsCurrentOrganizer,
    bool TrustedBackup,
    bool IsFallbackByDefault);

public sealed record ZaloAutoSessionGroupResponse(
    string Id,
    string AdminUserId,
    string ZaloConnectionId,
    string ConnectionDisplayName,
    string AccountZaloId,
    string GroupId,
    string GroupName,
    bool AutoSessionEnabled,
    bool RequireOrganizerApproval,
    int DefaultTeamCount,
    int DefaultTeamSize,
    int Capacity,
    int DefaultTotalSets,
    string DefaultStartTime,
    bool AssumePmForHourUnder12,
    string? DefaultLocation,
    bool BotEnabledForCreatedSessions,
    int ExistingSessionCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ZaloAutoSessionActivityResponse? Activity = null,
    bool GlobalEnabled = true,
    string RolloutMode = "Live",
    ZaloAutoSessionHealthResponse? Health = null,
    IReadOnlyList<ZaloAutoSessionLearningSignalResponse>? LearningSignals = null,
    int PendingLearningCount = 0,
    IReadOnlyList<ZaloAutoSessionOrganizerCandidateResponse>? OrganizerCandidates = null);
