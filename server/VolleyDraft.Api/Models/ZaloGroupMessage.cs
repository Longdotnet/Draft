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
    public string MessageType { get; set; } = "chat";
    public string ObservationSource { get; set; } = "Realtime";
    public bool IsFromBot { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FirstObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public int ReplyAttemptCount { get; set; }
    public DateTimeOffset? BotReplySentAt { get; set; }
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public string? ProcessingToken { get; set; }
    public string? SelectedIntent { get; set; }
    public bool AiCalled { get; set; }
    public string? ReplyOutcome { get; set; }

    public ZaloConnection ZaloConnection { get; set; } = null!;
}
