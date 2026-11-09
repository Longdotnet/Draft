namespace VolleyDraft.Api.Models;

public enum SettlementPaymentStatus
{
    Unpaid,
    Paid
}

public enum SettlementActionKind
{
    Reminder,
    Thanks,
    Summary,
    PaymentQr,
    Custom
}

public enum SettlementAudience
{
    Unpaid,
    Paid,
    All,
    Selected
}

public enum SettlementMessageStatus
{
    Pending,
    Sending,
    Sent,
    Skipped,
    Failed
}

public enum GroupFundTransactionKind
{
    SettlementSurplusAdjustment,
    SettlementTotalDueAdjustment,
    SettlementFundCoverageAdjustment
}

public sealed class MatchSettlement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = string.Empty;
    public decimal AmountPerFullSlot { get; set; }
    public decimal? TotalDueOverride { get; set; }
    public bool IsClosed { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSession Session { get; set; } = null!;
    public List<SettlementEntry> Entries { get; set; } = [];
    public List<SettlementMessage> Messages { get; set; } = [];
}

public sealed class SettlementEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SettlementId { get; set; } = string.Empty;
    public string? SessionPlayerId { get; set; }
    public string DisplayNameSnapshot { get; set; } = string.Empty;
    public string? ZaloUserIdSnapshot { get; set; }
    public decimal SlotFactor { get; set; } = 1m;
    public SettlementPaymentStatus PaymentStatus { get; set; } = SettlementPaymentStatus.Unpaid;
    public decimal ActualPaidAmount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSettlement Settlement { get; set; } = null!;
    public SessionPlayer? SessionPlayer { get; set; }
}

public sealed class GroupFundTransaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string SettlementId { get; set; } = string.Empty;
    public string SettlementEntryId { get; set; } = string.Empty;
    public string DisplayNameSnapshot { get; set; } = string.Empty;
    public GroupFundTransactionKind Kind { get; set; } = GroupFundTransactionKind.SettlementSurplusAdjustment;
    public decimal Amount { get; set; }
    public decimal SurplusBefore { get; set; }
    public decimal SurplusAfter { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSettlement Settlement { get; set; } = null!;
}

public sealed class SettlementMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SettlementId { get; set; } = string.Empty;
    public string? ClientRequestId { get; set; }
    public string? ZaloAccountIdSnapshot { get; set; }
    public string? ZaloGroupIdSnapshot { get; set; }
    public SettlementActionKind Kind { get; set; }
    public SettlementAudience Audience { get; set; }
    public string SelectedEntryIdsJson { get; set; } = "[]";
    public string? Message { get; set; }
    public bool IncludePaymentQr { get; set; }
    public string? PaymentQrImageUrlSnapshot { get; set; }
    public byte[]? PreparedImageData { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public SettlementMessageStatus Status { get; set; } = SettlementMessageStatus.Pending;
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MatchSettlement Settlement { get; set; } = null!;
}
