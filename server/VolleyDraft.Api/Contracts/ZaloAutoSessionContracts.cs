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
    bool BotEnabledForCreatedSessions);

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
    DateTimeOffset UpdatedAt);
