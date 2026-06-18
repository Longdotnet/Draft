namespace VolleyDraft.Api.Models;

public sealed class SessionPlayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public PlayerRole Role { get; set; } = PlayerRole.Attack;
    public PlayerLevel Level { get; set; } = PlayerLevel.Average;
    public PlayerGender Gender { get; set; } = PlayerGender.Male;
    public double Score { get; set; }
    public bool IsPresent { get; set; } = true;
    public bool IsCaptainEligible { get; set; } = true;
    public bool IsInsideSharedSlot { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
    public User? User { get; set; }
    public List<DraftSlotPlayer> DraftSlotPlayers { get; set; } = [];
}
