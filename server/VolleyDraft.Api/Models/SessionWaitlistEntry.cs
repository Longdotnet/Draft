namespace VolleyDraft.Api.Models;

public sealed class SessionWaitlistEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string ZaloUserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public SessionWaitlistStatus Status { get; set; } = SessionWaitlistStatus.Waiting;
    public string? SessionPlayerId { get; set; }
    public DateTimeOffset? InvitedAt { get; set; }
    public DateTimeOffset? InviteExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? LastNotifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Version { get; set; }

    public MatchSession Session { get; set; } = null!;
    public SessionPlayer? SessionPlayer { get; set; }
}
