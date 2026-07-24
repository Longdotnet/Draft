namespace VolleyDraft.Api.Models;

public enum ZaloActivityBackfillStage
{
    Queued,
    SyncingMembers,
    ScanningBoard,
    SyncingPollDetails,
    ProbingMessageHistory,
    ImportingMessages,
    RebuildingMetrics,
    Completed
}

public enum ZaloActivityBackfillStatus
{
    Queued,
    Running,
    Completed,
    CompletedWithLimitations,
    FailedRetryable,
    FailedPermanent
}

public enum ZaloMessageHistoryCapability
{
    Unsupported,
    RealtimeOnly,
    PartialHistoricalBackfill,
    SearchOnlyBackfill,
    FullHistoricalBackfill
}

public enum ZaloEngagementStatus
{
    New,
    Active,
    Regular,
    Occasional,
    AtRisk,
    Inactive,
    InsufficientData
}

public sealed class ZaloGroupMember
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string ZaloUserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsCurrentMember { get; set; } = true;
    public DateTimeOffset? LeftAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloConnection ZaloConnection { get; set; } = null!;
}

public sealed class ZaloPollSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string PollId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string CreatorZaloUserId { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAtFromZalo { get; set; }
    public DateTimeOffset? UpdatedAtFromZalo { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsClosed { get; set; }
    public bool IsAnonymous { get; set; }
    public bool AllowsMultipleChoices { get; set; }
    public bool HasVoterIdentities { get; set; }
    public bool IsAnalyticsEligible { get; set; }
    public string? ExclusionReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloConnection ZaloConnection { get; set; } = null!;
    public List<ZaloPollOptionSnapshot> Options { get; set; } = [];
}

public sealed class ZaloPollOptionSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string PollSnapshotId { get; set; } = string.Empty;
    public string ZaloOptionId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset FirstObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloPollSnapshot PollSnapshot { get; set; } = null!;
    public List<ZaloPollVoteActivity> Votes { get; set; } = [];
}

public sealed class ZaloPollVoteActivity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string PollSnapshotId { get; set; } = string.Empty;
    public string PollOptionSnapshotId { get; set; } = string.Empty;
    public string ZaloUserId { get; set; } = string.Empty;
    public DateTimeOffset FirstObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsCurrentlySelected { get; set; } = true;
    public DateTimeOffset? RemovedObservedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloPollSnapshot PollSnapshot { get; set; } = null!;
    public ZaloPollOptionSnapshot PollOptionSnapshot { get; set; } = null!;
}

public sealed class ZaloActivityBackfillJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public ZaloActivityBackfillStage Stage { get; set; } = ZaloActivityBackfillStage.Queued;
    public ZaloActivityBackfillStatus Status { get; set; } = ZaloActivityBackfillStatus.Queued;
    public bool IsFullBackfill { get; set; } = true;
    public int BoardPage { get; set; } = 1;
    public string? BoardCursor { get; set; }
    public string? MessageCursor { get; set; }
    public string? LastBoardPageFingerprint { get; set; }
    public int ProcessedCount { get; set; }
    public int? DiscoveredTotal { get; set; }
    public int TotalBoardItemsScanned { get; set; }
    public int TotalPollsDiscovered { get; set; }
    public int TotalPollsWithVoterIdentities { get; set; }
    public int TotalPollsExcluded { get; set; }
    public int MembersSynchronized { get; set; }
    public int MessagesImported { get; set; }
    public ZaloMessageHistoryCapability MessageHistoryCapability { get; set; } = ZaloMessageHistoryCapability.Unsupported;
    public DateTimeOffset? GroupCreatedAtFromZalo { get; set; }
    public DateTimeOffset? OldestRetrievablePollAt { get; set; }
    public DateTimeOffset? NewestRetrievablePollAt { get; set; }
    public DateTimeOffset? OldestRetrievableMessageAt { get; set; }
    public DateTimeOffset? NewestRetrievableMessageAt { get; set; }
    public DateTimeOffset? LastSuccessfulPollSyncAt { get; set; }
    public DateTimeOffset? LastIncrementalSyncAt { get; set; }
    public DateTimeOffset? BackfillStartedAt { get; set; }
    public DateTimeOffset? BackfillCompletedAt { get; set; }
    public string? LastErrorSummary { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloConnection ZaloConnection { get; set; } = null!;
}
