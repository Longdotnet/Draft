namespace VolleyDraft.Api.Models;

public sealed class TeamSeparationConstraint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string FirstSessionPlayerId { get; set; } = string.Empty;
    public string SecondSessionPlayerId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
    public SessionPlayer FirstSessionPlayer { get; set; } = null!;
    public SessionPlayer SecondSessionPlayer { get; set; } = null!;
}
