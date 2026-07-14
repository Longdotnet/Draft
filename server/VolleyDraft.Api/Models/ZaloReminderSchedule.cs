namespace VolleyDraft.Api.Models;

public enum ZaloReminderAudience
{
    All,
    Roster
}

public sealed class ZaloReminderSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string CreatedBySenderId { get; set; } = string.Empty;
    public string CreatedBySenderName { get; set; } = string.Empty;
    public string? Message { get; set; }
    public ZaloReminderAudience Audience { get; set; } = ZaloReminderAudience.All;
    public bool OnlyIfMissingSlots { get; set; }
    public bool Repeats { get; set; }
    public int? IntervalMinutes { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
}
