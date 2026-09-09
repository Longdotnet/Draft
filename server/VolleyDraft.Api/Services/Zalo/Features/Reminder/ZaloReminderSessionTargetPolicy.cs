using VolleyDraft.Api.Services.Zalo.Conversation;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Keeps exact calendar dates authoritative for reminder targeting while the legacy
/// reminder handler still supports its broader multi-session weekday/name grammar.
/// A concrete date must never fall through to a weekday token and schedule another
/// week's match (for example, "CN 13/9" must not also target "CN 20/9").
/// </summary>
internal static class ZaloReminderSessionTargetPolicy
{
    public static IReadOnlySet<string>? ResolveExplicitCalendarDateCandidateIds(
        string selector,
        IReadOnlyList<ZaloSessionReference> candidates,
        DateTimeOffset? now = null)
    {
        var resolution = ZaloSessionResolver.Resolve(selector, candidates, now);
        if (!string.Equals(resolution.Reason, "calendar_date", StringComparison.Ordinal))
            return null;

        return resolution.CandidateIds.ToHashSet(StringComparer.Ordinal);
    }
}
