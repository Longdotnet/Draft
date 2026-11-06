namespace VolleyDraft.Api.Models;

public enum SettlementAccessChallengeStatus
{
    Pending,
    Approved,
    Denied
}

public sealed class SettlementAccessChallenge
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupIdSnapshot { get; set; } = string.Empty;
    public string AccountIdSnapshot { get; set; } = string.Empty;
    public string RequesterKeyHash { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public string RedemptionSecretHash { get; set; } = string.Empty;
    public SettlementAccessChallengeStatus Status { get; set; } = SettlementAccessChallengeStatus.Pending;
    public string? AuthorizedZaloUserId { get; set; }
    public string? AuthorizedDisplayName { get; set; }
    public bool AuthorizedViaConfiguredOperator { get; set; }
    public string? DeniedZaloUserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? DeniedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
    public ZaloConnection ZaloConnection { get; set; } = null!;
}

public sealed class SettlementAccessSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string? SourceChallengeId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string ZaloUserId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool AuthorizedViaConfiguredOperator { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastRoleVerifiedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloConnection ZaloConnection { get; set; } = null!;
}
