using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Contracts;

public sealed record CreateSettlementRequest(decimal AmountPerFullSlot);

public sealed record UpdateSettlementRequest(
    decimal AmountPerFullSlot,
    decimal? TotalDueOverride = null);

public sealed record UpdateSettlementEntryRequest(
    decimal SlotFactor,
    bool IsPaid,
    decimal? ActualPaidAmount = null);

public sealed record CreateSettlementActionRequest(
    SettlementActionKind Kind,
    SettlementAudience Audience,
    IReadOnlyList<string>? SelectedEntryIds,
    string? Message,
    bool IncludePaymentQr,
    int? DelayMinutes,
    string? ClientRequestId = null);

public sealed record SettlementEntryResponse(
    string Id,
    string? SessionPlayerId,
    string DisplayName,
    string? ZaloUserId,
    decimal SlotFactor,
    SettlementPaymentStatus PaymentStatus,
    bool IsPaid,
    decimal AmountDue,
    decimal ActualPaidAmount,
    decimal SurplusAmount,
    DateTimeOffset UpdatedAt);

public sealed record SettlementActionResponse(
    string Id,
    SettlementActionKind Kind,
    SettlementAudience Audience,
    string? Message,
    bool IncludePaymentQr,
    DateTimeOffset DueAt,
    SettlementMessageStatus Status,
    int FailureCount,
    string? LastError,
    DateTimeOffset? SentAt);

public sealed record SettlementResponse(
    string Id,
    string SessionId,
    string SessionName,
    decimal AmountPerFullSlot,
    decimal? TotalDueOverride,
    string? PaymentQrImageUrl,
    bool IsClosed,
    DateTimeOffset? ClosedAt,
    decimal TotalDue,
    decimal TotalCollected,
    decimal TotalReceived,
    decimal SettlementSurplus,
    decimal GroupFundBalance,
    int PaidCount,
    int TotalCount,
    IReadOnlyList<SettlementEntryResponse> Entries,
    IReadOnlyList<SettlementActionResponse> PendingActions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
