using Microsoft.EntityFrameworkCore;

namespace VolleyDraft.Api.Models;

public enum ZaloGuestReservationStatus
{
    Active,
    Waitlisted,
    Cancelled,
    Linked
}

[Index(nameof(SessionId), nameof(SourceMessageId), nameof(GuestIndex), IsUnique = true)]
[Index(nameof(SessionId), nameof(SponsorZaloUserId), nameof(Status), nameof(CreatedAt))]
public sealed class ZaloGuestReservation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public string? SessionPlayerId { get; set; }
    public string SponsorZaloUserId { get; set; } = string.Empty;
    public string SponsorDisplayName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int GuestIndex { get; set; }
    public int SponsorSequence { get; set; }
    public PlayerGender? Gender { get; set; }
    public string SourceMessageId { get; set; } = string.Empty;
    public string? RecruitmentMessageId { get; set; }
    public ZaloGuestReservationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
}
