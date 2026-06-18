namespace VolleyDraft.Api.Models;

public sealed class TeamPreferenceGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
    public List<TeamPreferenceGroupPlayer> Players { get; set; } = [];
}
