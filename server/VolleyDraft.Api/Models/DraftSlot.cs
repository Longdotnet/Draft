namespace VolleyDraft.Api.Models;

public sealed class DraftSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public DraftSlotType Type { get; set; } = DraftSlotType.Single;
    public string DisplayName { get; set; } = string.Empty;
    public PlayerRole Role { get; set; } = PlayerRole.Attack;
    public PlayerGender Gender { get; set; } = PlayerGender.Unknown;
    public double AverageScore { get; set; }
    public string? AssignedTeamId { get; set; }
    public bool IsCaptainSlot { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
    public Team? AssignedTeam { get; set; }
    public List<DraftSlotPlayer> Players { get; set; } = [];
    public BlindBag? BlindBag { get; set; }
}
