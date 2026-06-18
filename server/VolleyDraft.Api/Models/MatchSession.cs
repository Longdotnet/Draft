namespace VolleyDraft.Api.Models;

public sealed class MatchSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = string.Empty;
    public string AdminUserId { get; set; } = string.Empty;
    public SessionStatus Status { get; set; } = SessionStatus.Setup;
    public int TeamCount { get; set; } = 3;
    public int TeamSize { get; set; } = 6;
    public int TotalSets { get; set; } = 4;
    public int? CurrentRoundNumber { get; set; }
    public string? CurrentTurnTeamId { get; set; }
    public string? CurrentTurnCaptainSessionPlayerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User AdminUser { get; set; } = null!;
    public List<SessionPlayer> Players { get; set; } = [];
    public List<Team> Teams { get; set; } = [];
    public List<DraftSlot> DraftSlots { get; set; } = [];
    public List<DraftRound> DraftRounds { get; set; } = [];
    public List<BlindBag> BlindBags { get; set; } = [];
    public List<DraftTurn> DraftTurns { get; set; } = [];
    public List<TeamPreferenceGroup> TeamPreferenceGroups { get; set; } = [];
}
