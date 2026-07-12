namespace VolleyDraft.Api.Models;

public sealed class ZaloBotConversationState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string SenderZaloUserId { get; set; } = string.Empty;
    public string PendingIntent { get; set; } = string.Empty;
    public string PendingPayloadJson { get; set; } = "{}";
    public string? PreviousCommand { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloConnection ZaloConnection { get; set; } = null!;
}
