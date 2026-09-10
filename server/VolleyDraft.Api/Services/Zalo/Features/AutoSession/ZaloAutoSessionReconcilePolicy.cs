namespace VolleyDraft.Api.Services;

internal static class ZaloAutoSessionReconcilePolicy
{
    internal static bool ShouldRunProviderSafetyScan(
        ZaloAutoSessionHealthData health,
        DateTimeOffset now,
        TimeSpan eventFreshness,
        TimeSpan maximumSafetySilence)
    {
        if (health.LastReconcileAt is null)
            return true;

        if (now - health.LastReconcileAt.Value >= maximumSafetySilence)
            return true;

        if (health.LastPollEventAt is null || health.LastSuccessAt is null)
            return true;

        // RecordPollEventAsync runs before event processing. Only a success recorded at
        // or after that event proves the realtime path completed safely enough to defer
        // the redundant provider-wide poll listing.
        if (health.LastSuccessAt.Value < health.LastPollEventAt.Value)
            return true;

        var eventAge = now - health.LastPollEventAt.Value;
        return eventAge < TimeSpan.Zero || eventAge > eventFreshness;
    }
}
