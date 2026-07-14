namespace VolleyDraft.Api.Models;

public sealed class ZaloBotActionHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string? ActorZaloUserId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string BeforeStateJson { get; set; } = string.Empty;
    public string AfterStateJson { get; set; } = string.Empty;
    public string BeforeHash { get; set; } = string.Empty;
    public string AfterHash { get; set; } = string.Empty;
    public bool IsUndoable { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UndoneAt { get; set; }
    public string? UndoneByZaloUserId { get; set; }
    public string? UndoFailure { get; set; }

    public MatchSession Session { get; set; } = null!;
}
