namespace VolleyDraft.Api.Models;

public sealed class DraftRound
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public int RoundNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public DraftRoundStatus Status { get; set; } = DraftRoundStatus.Waiting;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
    public List<BlindBag> BlindBags { get; set; } = [];
    public List<DraftTurn> DraftTurns { get; set; } = [];
}
