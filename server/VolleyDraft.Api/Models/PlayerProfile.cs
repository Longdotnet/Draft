namespace VolleyDraft.Api.Models;

public sealed class PlayerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloUserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public PlayerGender? Gender { get; set; }
    public PlayerRole? DefaultRole { get; set; }
    public PlayerLevel? DefaultLevel { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? GenderUpdatedAt { get; set; }
    public string? GenderUpdatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SessionPlayer> SessionPlayers { get; set; } = [];
}
