namespace VolleyDraft.Api.Models;

public enum ZaloScheduledDraftRunState
{
    Pending,
    ReminderSent,
    Drafted,
    AlreadyDrafted,
    Blocked,
    Skipped
}

public sealed class ZaloScheduledDraftPolicy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int LocalDraftMinuteOfDay { get; set; } = 17 * 60 + 30;
    public int ReminderMinutes { get; set; } = 30;
    public string TimeZoneId { get; set; } = "Asia/Ho_Chi_Minh";
    public string? EnabledByZaloUserId { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloConnection ZaloConnection { get; set; } = null!;
}

public sealed class ZaloScheduledDraftDecision
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public bool Skip { get; set; }
    public DateTimeOffset? DeferredUntil { get; set; }
    public string ChangedByZaloUserId { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public int PolicyVersion { get; set; }

    public MatchSession Session { get; set; } = null!;
}

public sealed class ZaloScheduledDraftRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public int PolicyVersion { get; set; }
    public DateTimeOffset ReminderDueAt { get; set; }
    public DateTimeOffset DraftDueAt { get; set; }
    public DateTimeOffset? ReminderSentAt { get; set; }
    public DateTimeOffset? DraftedAt { get; set; }
    public DateTimeOffset? ResultMessageSentAt { get; set; }
    public ZaloScheduledDraftRunState State { get; set; } = ZaloScheduledDraftRunState.Pending;
    public string? RosterFingerprint { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
}
