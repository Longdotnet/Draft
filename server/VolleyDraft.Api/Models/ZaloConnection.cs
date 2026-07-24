namespace VolleyDraft.Api.Models;

public sealed class ZaloConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string AdminUserId { get; set; } = string.Empty;
    public string AccountZaloId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string EncryptedCredentials { get; set; } = string.Empty;
    public ZaloConnectionStatus Status { get; set; } = ZaloConnectionStatus.Connected;
    public DateTimeOffset LastValidatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User AdminUser { get; set; } = null!;
    public List<MatchSession> MatchSessions { get; set; } = [];
    public List<ZaloGroupMessage> GroupMessages { get; set; } = [];
    public List<ZaloGroupMember> GroupMembers { get; set; } = [];
    public List<ZaloPollSnapshot> PollSnapshots { get; set; } = [];
    public List<ZaloActivityBackfillJob> ActivityBackfillJobs { get; set; } = [];
}
