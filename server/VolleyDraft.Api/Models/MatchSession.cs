namespace VolleyDraft.Api.Models;

public sealed class MatchSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = string.Empty;
    public string AdminUserId { get; set; } = string.Empty;
    public string? ZaloConnectionId { get; set; }
    public string? ZaloGroupId { get; set; }
    public string? ZaloGroupName { get; set; }
    public string? ZaloGroupAvatarUrl { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public string? Location { get; set; }
    public string? ParkingInstructions { get; set; }
    public string? LocationImageUrl { get; set; }
    public string? PaymentInstructions { get; set; }
    public string? PaymentQrImageUrl { get; set; }
    public bool BotEnabled { get; set; }
    public string? BotCustomInstructions { get; set; }
    public string BotOperatorZaloUserIdsJson { get; set; } = "[]";
    public string? BotActionLeaseToken { get; set; }
    public string? BotActionLeaseName { get; set; }
    public DateTimeOffset? BotActionLeaseUntil { get; set; }
    public bool ReminderEnabled { get; set; }
    public int ReminderLeadHours { get; set; } = 72;
    public int ReminderIntervalHours { get; set; } = 12;
    public int ReminderIntervalMinutes { get; set; } = 720;
    public bool ReminderRepeats { get; set; } = true;
    public DateTimeOffset? LastReminderAt { get; set; }
    public DateTimeOffset? NextReminderAt { get; set; }
    public int? ReminderLastKnownPlayerCount { get; set; }
    public string? ReminderLeaseToken { get; set; }
    public DateTimeOffset? ReminderLeaseUntil { get; set; }
    public int ReminderFailureCount { get; set; }
    public string? LastReminderError { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Setup;
    public int TeamCount { get; set; } = 3;
    public int TeamSize { get; set; } = 6;
    public int TotalSets { get; set; } = 4;
    public int? CurrentRoundNumber { get; set; }
    public string? CurrentTurnTeamId { get; set; }
    public string? CurrentTurnCaptainSessionPlayerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User AdminUser { get; set; } = null!;
    public ZaloConnection? ZaloConnection { get; set; }
    public List<SessionPlayer> Players { get; set; } = [];
    public List<Team> Teams { get; set; } = [];
    public List<DraftSlot> DraftSlots { get; set; } = [];
    public List<DraftRound> DraftRounds { get; set; } = [];
    public List<BlindBag> BlindBags { get; set; } = [];
    public List<DraftTurn> DraftTurns { get; set; } = [];
    public List<TeamPreferenceGroup> TeamPreferenceGroups { get; set; } = [];
    public List<PollImport> PollImports { get; set; } = [];
    public List<ZaloReminderSchedule> ReminderSchedules { get; set; } = [];
}
