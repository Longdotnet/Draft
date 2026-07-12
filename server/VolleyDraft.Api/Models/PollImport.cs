namespace VolleyDraft.Api.Models;

public sealed class PollImport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string ImportedByUserId { get; set; } = string.Empty;
    public string ZaloGroupId { get; set; } = string.Empty;
    public string PollId { get; set; } = string.Empty;
    public string PollQuestion { get; set; } = string.Empty;
    public string SelectedOptionIdsJson { get; set; } = "[]";
    public int ImportedPlayerCount { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
    public User ImportedByUser { get; set; } = null!;
}
