namespace VolleyDraft.Api.Models;

public sealed class ZaloBotLearnedRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public string NormalizedTrigger { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string CreatedBySenderId { get; set; } = string.Empty;
    public string CreatedBySenderName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloConnection ZaloConnection { get; set; } = null!;
}
