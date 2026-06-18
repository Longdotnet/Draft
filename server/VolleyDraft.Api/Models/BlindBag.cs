namespace VolleyDraft.Api.Models;

public sealed class BlindBag
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string RoundId { get; set; } = string.Empty;
    public string DraftSlotId { get; set; } = string.Empty;
    public int BagNumber { get; set; }
    public bool IsOpened { get; set; }
    public string? OpenedByUserId { get; set; }
    public string? OpenedForTeamId { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }

    public MatchSession Session { get; set; } = null!;
    public DraftRound Round { get; set; } = null!;
    public DraftSlot DraftSlot { get; set; } = null!;
    public User? OpenedByUser { get; set; }
    public Team? OpenedForTeam { get; set; }
}
