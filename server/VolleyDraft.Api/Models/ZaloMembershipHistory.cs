namespace VolleyDraft.Api.Models;

public sealed class ZaloGroupMembershipPeriod
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string ZaloUserId { get; set; } = string.Empty;
    public string DisplayNameSnapshot { get; set; } = string.Empty;
    public DateTimeOffset? JoinedAt { get; set; }
    public string JoinEvidenceSource { get; set; } = "directory_observation";
    public string? JoinEventId { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
    public string? LeaveEvidenceSource { get; set; }
    public string? LeaveEventId { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsCurrentPeriod { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ZaloMembershipCoverage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public DateTimeOffset? ProviderEventTrackingStartedAt { get; set; }
    public DateTimeOffset? LastProviderEventAt { get; set; }
    public DateTimeOffset? LastDirectorySyncAt { get; set; }
    public bool LastDirectorySyncWasComplete { get; set; }
    public bool HasKnownGap { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
