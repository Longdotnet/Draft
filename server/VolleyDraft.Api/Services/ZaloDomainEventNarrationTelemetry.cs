using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Persists rollout metadata for the domain-event narrator without storing the
/// user-visible narration text. This is observability only; it never mutates domain state.
/// </summary>
public sealed class ZaloDomainEventNarrationTelemetry(VolleyDraftDbContext db)
{
    public Task<string> RecordAsync(
        string groupId,
        string sessionId,
        ZaloDomainEventShadowDecision decision,
        ZaloDomainEventNarratorResult narration,
        CancellationToken cancellationToken = default)
    {
        var status = narration.Sent
            ? "Sent"
            : narration.Eligible
                ? "Suppressed"
                : "NotEligible";
        var reason = Clean(narration.Reason, 80);
        var metadata = $"status:{status}|reason:{reason}|before:{decision.BeforeCount}|after:{decision.AfterCount}|capacity:{decision.Capacity}";

        return new ZaloBotTraceStore(db).WriteAsync(
            new ZaloBotTraceEntry(
                MessageId: $"domain-narration:{decision.TraceId}",
                GroupId: Clean(groupId, 100),
                SenderZaloUserId: string.Empty,
                AddressReason: $"DomainEventNarrator{status}",
                IntentSource: "AmbientDomainEventNarrator",
                Intent: decision.EventKind,
                Confidence: 1.0,
                ResolvedSessionId: Clean(sessionId, 100),
                AiCalled: false,
                FallbackReason: metadata),
            cancellationToken);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
