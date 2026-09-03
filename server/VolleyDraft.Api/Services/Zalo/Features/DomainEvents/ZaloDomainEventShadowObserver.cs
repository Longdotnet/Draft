using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloDomainRosterSnapshot(
    string SessionId,
    string GroupId,
    int PresentCount,
    int Capacity);

public sealed record ZaloDomainEventShadowDecision(
    string EventKind,
    int BeforeCount,
    int AfterCount,
    int Capacity,
    string TraceId);

/// <summary>
/// Observes authoritative roster changes caused by poll synchronization and emits
/// metadata-only shadow telemetry. This service never sends a Zalo message and never
/// derives registration truth from chat text.
/// </summary>
public sealed class ZaloDomainEventShadowObserver(VolleyDraftDbContext db)
{
    public async Task<ZaloDomainRosterSnapshot?> CaptureAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        sessionId = (sessionId ?? string.Empty).Trim();
        if (sessionId.Length == 0) return null;

        return await db.MatchSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => new ZaloDomainRosterSnapshot(
                session.Id,
                session.ZaloGroupId ?? string.Empty,
                session.Players.Count(player => player.IsPresent),
                Math.Max(0, session.TeamCount * session.TeamSize)))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<ZaloDomainEventShadowDecision?> ObserveAfterPollSyncAsync(
        ZaloDomainRosterSnapshot before,
        string? actorZaloUserId,
        string? boardId,
        long occurredAtUnixMs,
        CancellationToken cancellationToken = default)
    {
        var after = await CaptureAsync(before.SessionId, cancellationToken);
        if (after is null ||
            !string.Equals(before.GroupId, after.GroupId, StringComparison.Ordinal) ||
            before.PresentCount == after.PresentCount)
            return null;

        var eventKind = Classify(before.PresentCount, after.PresentCount, after.Capacity);
        var sourceId = CleanToken(boardId);
        if (sourceId.Length == 0) sourceId = occurredAtUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var traceMessageId = $"domain-poll:{sourceId}:{after.SessionId}:{before.PresentCount}-{after.PresentCount}";

        var traceId = await new ZaloBotTraceStore(db).WriteAsync(
            new ZaloBotTraceEntry(
                MessageId: traceMessageId,
                GroupId: after.GroupId,
                SenderZaloUserId: CleanToken(actorZaloUserId),
                AddressReason: "PollAuthoritativeStateChange",
                IntentSource: "AmbientDomainEventShadow",
                Intent: eventKind,
                Confidence: 1.0,
                ResolvedSessionId: after.SessionId,
                AiCalled: false,
                FallbackReason: $"roster:{before.PresentCount}->{after.PresentCount};capacity:{after.Capacity}"),
            cancellationToken);

        return new ZaloDomainEventShadowDecision(
            eventKind,
            before.PresentCount,
            after.PresentCount,
            after.Capacity,
            traceId);
    }

    internal static string Classify(int beforeCount, int afterCount, int capacity)
    {
        if (capacity > 0 && beforeCount < capacity && afterCount >= capacity)
            return "RosterFilled";
        if (capacity > 0 && beforeCount >= capacity && afterCount < capacity)
            return "RosterReopened";
        return afterCount > beforeCount ? "RosterIncreased" : "RosterDecreased";
    }

    private static string CleanToken(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length > 160) text = text[..160];
        return text;
    }
}
