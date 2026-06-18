using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Contracts;

public sealed record RegisterRequest(string DisplayName, string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record AuthResponse(string Token, UserDto User);

public sealed record UserDto(string Id, string DisplayName, string Email);

public sealed record CreateSessionRequest(
    string Name,
    int TeamCount = 3,
    int TeamSize = 6,
    int TotalSets = 4);

public sealed record UpdateSessionRequest(
    string Name,
    int TotalSets = 4);

public sealed record SessionResponse(
    string Id,
    string Name,
    SessionStatus Status,
    int TeamCount,
    int TeamSize,
    int TotalSets,
    string AdminUserId,
    IReadOnlyList<TeamSummary> Teams);

public sealed record PublicSessionSummaryResponse(
    string Id,
    string Name,
    SessionStatus Status,
    int TeamCount,
    int TeamSize,
    int TotalSets,
    int PlayerCount,
    int RequiredPlayerCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);

public sealed record AddPlayerRequest(
    string DisplayName,
    PlayerRole Role,
    PlayerLevel Level,
    PlayerGender Gender = PlayerGender.Male,
    bool IsPresent = true,
    bool IsCaptainEligible = true);

public sealed record UpdatePlayerRequest(
    string DisplayName,
    PlayerRole Role,
    PlayerLevel Level,
    PlayerGender Gender = PlayerGender.Male,
    bool IsPresent = true,
    bool IsCaptainEligible = true);

public sealed record CreateSharedSlotRequest(
    IReadOnlyList<string> SessionPlayerIds,
    PlayerRole Role);

public sealed record CreateTeamPreferenceGroupRequest(
    IReadOnlyList<string> SessionPlayerIds);

public sealed record SessionPlayerResponse(
    string Id,
    string DisplayName,
    string? UserId,
    PlayerRole Role,
    PlayerLevel Level,
    PlayerGender Gender,
    double Score,
    bool IsPresent,
    bool IsCaptainEligible,
    bool IsInsideSharedSlot);

public sealed record SharedSlotResponse(
    string Id,
    string DisplayName,
    PlayerRole Role,
    PlayerGender Gender,
    double AverageScore,
    IReadOnlyList<string> SessionPlayerIds,
    IReadOnlyList<string> PlayerNames);

public sealed record TeamPreferenceGroupResponse(
    string Id,
    IReadOnlyList<string> SessionPlayerIds,
    IReadOnlyList<string> PlayerNames,
    double AverageScore);

public sealed record ManualCaptainsRequest(IReadOnlyList<string> CaptainSessionPlayerIds);

public sealed record CaptainsResponse(
    IReadOnlyList<CaptainTeamResponse> Captains,
    CaptainBalanceResponse Balance);

public sealed record CaptainTeamResponse(
    string TeamId,
    string TeamName,
    string SessionPlayerId,
    string DisplayName,
    double Score);

public sealed record CaptainBalanceResponse(double Difference, string Status, string? Warning);

public sealed record DraftStateResponse(
    SessionStatus SessionStatus,
    int? CurrentRound,
    int TotalRounds,
    TeamSummary? CurrentTeam,
    CaptainSummary? CurrentCaptain,
    DraftViewerResponse Viewer,
    IReadOnlyList<BlindBagStateResponse> Bags,
    IReadOnlyList<TeamPreviewResponse> TeamPreview,
    string? Message,
    OpenedBagResultResponse? LastOpenedBag);

public sealed record DraftViewerResponse(
    string Id,
    string Role,
    bool CanOpenBag,
    string Mode);

public sealed record BlindBagStateResponse(
    string Id,
    string Label,
    bool IsOpened,
    RevealedSlotResponse? RevealedSlot = null);

public sealed record OpenBagResponse(
    string Message,
    RevealedSlotResponse RevealedSlot,
    TeamSummary AssignedTeam,
    NextTurnResponse? NextTurn);

public sealed record PrepareRevealResponse(
    RevealedSlotResponse RevealedSlot,
    TeamSummary CurrentTeam,
    CaptainSummary CurrentCaptain);

public sealed record OpenedBagResultResponse(
    string Message,
    RevealedSlotResponse RevealedSlot,
    TeamSummary AssignedTeam);

public sealed record RevealedSlotResponse(
    string Id,
    string DisplayName,
    DraftSlotType Type,
    PlayerRole Role,
    PlayerGender Gender,
    double AverageScore);

public sealed record TeamSummary(string Id, string Name);

public sealed record CaptainSummary(string Id, string Name);

public sealed record NextTurnResponse(string TeamName, string CaptainName, int RoundNumber);

public sealed record TeamPreviewResponse(
    string TeamId,
    string TeamName,
    string? CaptainName,
    IReadOnlyList<TeamSlotPreviewResponse> Slots);

public sealed record TeamSlotPreviewResponse(
    string Id,
    string DisplayName,
    DraftSlotType Type,
    PlayerGender Gender,
    bool IsCaptainSlot,
    double AverageScore);

public sealed record ApiErrorResponse(string Message);

public sealed record DeleteResponse(string Message);
