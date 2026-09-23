namespace VolleyDraft.Api.Models;

public enum ZaloScheduledDraftRunState
{
    Pending,
    ReminderSent,
    Executing,
    Completed,
    Skipped,
    Failed
}

public enum ZaloScheduledDraftOverrideKind
{
    None,
    Deferred,
    Skipped
}

public sealed class ZaloScheduledDraftPolicy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int LocalDraftMinuteOfDay { get; set; } = 17 * 60 + 30;
    public int ReminderLeadMinutes { get; set; } = 30;
    public string TimeZoneId { get; set; } = "Asia/Ho_Chi_Minh";
    public string? EnabledByZaloUserId { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ZaloScheduledDraftRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public long PolicyVersion { get; set; }
    public DateTimeOffset ReminderDueAt { get; set; }
    public DateTimeOffset ScheduledFor { get; set; }
    public ZaloScheduledDraftRunState State { get; set; } = ZaloScheduledDraftRunState.Pending;
    public ZaloScheduledDraftOverrideKind OverrideKind { get; set; }
    public DateTimeOffset? DeferredUntil { get; set; }
    public string? OverrideByZaloUserId { get; set; }
    public DateTimeOffset? ReminderAttemptedAt { get; set; }
    public DateTimeOffset? ReminderSentAt { get; set; }
    public string? ReminderMessageId { get; set; }
    public string? RosterFingerprint { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? DraftStartedAt { get; set; }
    public DateTimeOffset? DraftCompletedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
