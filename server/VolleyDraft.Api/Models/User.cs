namespace VolleyDraft.Api.Models;

public sealed class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<MatchSession> AdminSessions { get; set; } = [];
    public List<SessionPlayer> SessionPlayers { get; set; } = [];
}
