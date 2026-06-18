namespace VolleyDraft.Api.Models;

public sealed class DraftTurn
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string RoundId { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public string CaptainSessionPlayerId { get; set; } = string.Empty;
    public int TurnOrder { get; set; }
    public DraftTurnStatus Status { get; set; } = DraftTurnStatus.Waiting;
    public string? OpenedBagId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public MatchSession Session { get; set; } = null!;
    public DraftRound Round { get; set; } = null!;
    public Team Team { get; set; } = null!;
    public SessionPlayer CaptainSessionPlayer { get; set; } = null!;
    public BlindBag? OpenedBag { get; set; }
}
