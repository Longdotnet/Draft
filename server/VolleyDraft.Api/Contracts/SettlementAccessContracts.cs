using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Contracts;

public sealed record CreateSettlementAccessChallengeRequest(string SessionId);

public sealed record SettlementAccessChallengeResponse(
    string ChallengeId,
    string RedemptionSecret,
    string Code,
    string VerificationCommand,
    string SessionName,
    DateTimeOffset ExpiresAt);

public sealed record RedeemSettlementAccessChallengeRequest(
    string ChallengeId,
    string RedemptionSecret);

public sealed record SettlementAccessChallengeStatusResponse(
    SettlementAccessChallengeStatus Status,
    bool Authorized,
    string? DisplayName,
    string Message,
    DateTimeOffset ExpiresAt);

public sealed record SettlementAccessStatusResponse(
    bool Authorized,
    string SessionId,
    string SessionName,
    DateTimeOffset? StartTime,
    string ZaloUserId,
    string? DisplayName,
    DateTimeOffset ExpiresAt);

public sealed record SettlementAccessVerificationResult(
    bool Approved,
    string Message);
