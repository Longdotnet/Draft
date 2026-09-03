namespace VolleyDraft.Api.Services.Zalo.Routing;

public enum ZaloFeatureId
{
    Help,
    Schedule,
    Draft,
    PassSlot,
    TeamPreference,
    ShareSlot,
    SlotTransfer,
    Waitlist,
    GuestRecruitment,
    Roster,
    TeamLineup,
    PollSync,
    Reminder,
    MemberActivity,
    Social
}

public sealed record ZaloFeatureTurn(
    string AccountId,
    string GroupId,
    string SenderId,
    string SenderName,
    string MessageId,
    string Content,
    bool MentionedBot,
    DateTimeOffset ReceivedAt,
    string? QuotedMessageId = null);

public sealed record ZaloFeatureMatch(
    int Score,
    bool Deterministic,
    string Reason)
{
    public int NormalizedScore => Math.Clamp(Score, 0, 100);
}

public sealed record ZaloFeatureExecutionResult(
    bool Handled,
    string Reason,
    string? ReplyText = null);

public sealed record ZaloFeatureRouteResult(
    bool Handled,
    ZaloFeatureId? Feature,
    string Reason,
    bool Ambiguous = false);

public interface IZaloFeatureModule
{
    ZaloFeatureId Feature { get; }
    int Priority { get; }

    ValueTask<ZaloFeatureMatch?> MatchAsync(
        ZaloFeatureTurn turn,
        CancellationToken cancellationToken = default);

    Task<ZaloFeatureExecutionResult> HandleAsync(
        ZaloFeatureTurn turn,
        CancellationToken cancellationToken = default);
}
