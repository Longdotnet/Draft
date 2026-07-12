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
    string? ZaloConnectionId,
    string? ZaloGroupId,
    string? ZaloGroupName,
    string? ZaloGroupAvatarUrl,
    DateTimeOffset? StartTime,
    string? Location,
    string? ParkingInstructions,
    string? LocationImageUrl,
    string? PaymentInstructions,
    string? PaymentQrImageUrl,
    bool BotEnabled,
    string? BotCustomInstructions,
    string? BotTrainingExamples,
    bool ReminderEnabled,
    int ReminderLeadHours,
    int ReminderIntervalHours,
    DateTimeOffset? LastReminderAt,
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

public sealed record AdminSessionSummaryResponse(
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
    PlayerGender Gender = PlayerGender.Unknown,
    bool IsPresent = true,
    bool IsCaptainEligible = true);

public sealed record UpdatePlayerRequest(
    string DisplayName,
    PlayerRole Role,
    PlayerLevel Level,
    PlayerGender Gender = PlayerGender.Unknown,
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
    string? PlayerProfileId,
    string? ZaloUserId,
    string? AvatarUrl,
    PlayerRole Role,
    PlayerLevel Level,
    PlayerGender Gender,
    double Score,
    bool IsPresent,
    bool IsCaptainEligible,
    bool IsInsideSharedSlot);

public sealed record StartZaloQrLoginResponse(
    string LoginId,
    string Status,
    DateTimeOffset ExpiresAt);

public sealed record ZaloQrLoginStatusResponse(
    string LoginId,
    string Status,
    string? QrImageBase64,
    string? DisplayName,
    string? AvatarUrl,
    string? Error,
    ZaloConnectionResponse? Connection);

public sealed record ZaloConnectionResponse(
    string Id,
    string AccountZaloId,
    string DisplayName,
    string? AvatarUrl,
    ZaloConnectionStatus Status,
    DateTimeOffset LastValidatedAt);

public sealed record ZaloGroupResponse(
    string Id,
    string Name,
    string? AvatarUrl,
    int TotalMembers);

public sealed record LinkZaloGroupRequest(
    string ConnectionId,
    string GroupId);

public sealed record UpdateZaloBotSettingsRequest(
    DateTimeOffset? StartTime,
    string? Location,
    string? ParkingInstructions,
    string? LocationImageUrl,
    string? PaymentInstructions,
    string? PaymentQrImageUrl,
    bool BotEnabled,
    string? BotCustomInstructions,
    string? BotTrainingExamples,
    bool ReminderEnabled,
    int ReminderLeadHours,
    int ReminderIntervalHours);

public sealed record ZaloBotSettingsResponse(
    string SessionId,
    DateTimeOffset? StartTime,
    string? Location,
    string? ParkingInstructions,
    string? LocationImageUrl,
    string? PaymentInstructions,
    string? PaymentQrImageUrl,
    bool BotEnabled,
    string? BotCustomInstructions,
    string? BotTrainingExamples,
    bool ReminderEnabled,
    int ReminderLeadHours,
    int ReminderIntervalHours,
    DateTimeOffset? LastReminderAt);

public sealed record ZaloIncomingMessageEvent(
    string AccountId,
    string BotId,
    string GroupId,
    string MessageId,
    string SenderId,
    string SenderName,
    string Content,
    IReadOnlyList<ZaloBridgeMention> Mentions,
    bool MentionedBot,
    long SentAtUnixMs);

public sealed record ZaloBridgeMention(string Uid, int Pos, int Len);

public sealed record ZaloPollOptionResponse(
    string Id,
    string Content,
    int VoteCount);

public sealed record ZaloPollResponse(
    string Id,
    string Question,
    IReadOnlyList<ZaloPollOptionResponse> Options,
    bool AllowMultipleChoices,
    bool IsAnonymous,
    bool IsClosed,
    bool HideVotePreview,
    int UniqueVoteCount,
    long CreatedAtUnixMs,
    long UpdatedAtUnixMs,
    long ExpiredAtUnixMs);

public sealed record CreateZaloImportPreviewRequest(
    string PollId,
    IReadOnlyList<string> SelectedOptionIds);

public sealed record ZaloImportPreviewResponse(
    string PollId,
    string PollQuestion,
    IReadOnlyList<ZaloPollOptionResponse> SelectedOptions,
    IReadOnlyList<ZaloImportCandidateResponse> Candidates,
    int UniqueVoterCount,
    bool CanDivideIntoTeams,
    int? PlayersPerTeam,
    long PollUpdatedAtUnixMs);

public sealed record ZaloImportCandidateResponse(
    string ZaloUserId,
    string DisplayName,
    string? AvatarUrl,
    PlayerGender? Gender,
    bool NeedsGenderSelection,
    PlayerRole Role,
    PlayerLevel Level,
    bool AlreadyInSession,
    IReadOnlyList<string> OptionIds,
    IReadOnlyList<string> OptionNames);

public sealed record ConfirmZaloPollImportRequest(
    string PollId,
    IReadOnlyList<string> SelectedOptionIds,
    long ExpectedPollUpdatedAtUnixMs,
    IReadOnlyList<ZaloImportCandidateDecision> Candidates);

public sealed record ZaloImportCandidateDecision(
    string ZaloUserId,
    bool Include,
    PlayerGender Gender,
    PlayerRole Role,
    PlayerLevel Level);

public sealed record ZaloPollImportResultResponse(
    int AddedCount,
    int UpdatedProfileCount,
    int SkippedExistingCount,
    int SessionPlayerCount,
    string Message);

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
