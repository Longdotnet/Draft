namespace VolleyDraft.Api.Models;

public sealed class ZaloGroupMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsFromBot { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public int ReplyAttemptCount { get; set; }
    public DateTimeOffset? BotReplySentAt { get; set; }

    public ZaloConnection ZaloConnection { get; set; } = null!;
}
