using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Contracts;

public sealed record ZaloAutoSessionCandidateActivityResponse(
    string OptionId,
    string OptionContent,
    string DayKey,
    DateTimeOffset StartTime,
    int VoteCount,
    string? SessionId,
    string? SessionName,
    SessionStatus? SessionStatus,
    int? PresentPlayerCount,
    int? Capacity,
    DateTimeOffset? LastRosterSyncAt,
    int? EffectiveSlotCount,
    int? ExcessSlotCount,
    bool? OverbookNeedsConfirmation);

public sealed record ZaloAutoSessionProposalActivityResponse(
    string Id,
    string PollId,
    string PollQuestion,
    string PollCreatorId,
    string Status,
    double ClassifierConfidence,
    string ClassifierReason,
    string? ProposalMessageId,
    string? ApprovedByZaloUserId,
    DateTimeOffset? ApprovedAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ZaloAutoSessionCandidateActivityResponse> Candidates);

public sealed record ZaloAutoSessionActivityResponse(
    string TrackedGroupId,
    string GroupId,
    string GroupName,
    bool AutoSessionEnabled,
    int ProposalCount,
    int AwaitingApprovalCount,
    int CreatedCount,
    int FailedCount,
    IReadOnlyList<ZaloAutoSessionProposalActivityResponse> Proposals);
