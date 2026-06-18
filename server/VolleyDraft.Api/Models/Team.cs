namespace VolleyDraft.Api.Models;

public sealed class Team
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CaptainSessionPlayerId { get; set; }
    public double TotalAverageScore { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
    public SessionPlayer? CaptainSessionPlayer { get; set; }
    public List<DraftSlot> AssignedSlots { get; set; } = [];
}
