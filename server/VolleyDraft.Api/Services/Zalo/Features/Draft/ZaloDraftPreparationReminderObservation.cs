using System.Security.Cryptography;
using System.Text;

namespace VolleyDraft.Api.Services;

internal static class ZaloDraftPreparationReminderObservation
{
    private const string VersionPrefix = "obs2:";
    internal static readonly TimeSpan SameBucketRefreshInterval = TimeSpan.FromMinutes(5);

    internal static string BuildFingerprint(
        ZaloDraftReadinessSnapshot readiness,
        int activeSlotRiskCount)
    {
        var canonical = string.Join('|',
            readiness.Fingerprint ?? string.Empty,
            readiness.State,
            readiness.EffectiveSlotCount,
            readiness.PresentPlayerCount,
            readiness.Capacity,
            readiness.MissingProfileCount,
            readiness.HasTeams ? 1 : 0,
            readiness.CanEscalate ? 1 : 0,
            Math.Max(0, activeSlotRiskCount));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return VersionPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string BuildIdempotencySuffix(
        ZaloDraftReadinessSnapshot readiness,
        int activeSlotRiskCount) =>
        BuildFingerprint(readiness, activeSlotRiskCount)[VersionPrefix.Length..(VersionPrefix.Length + 24)];

    internal static bool ShouldRefreshSameBucket(
        ZaloDraftPreparationReminderState previous,
        DateTimeOffset now) =>
        now - previous.UpdatedAt >= SameBucketRefreshInterval;

    internal static bool HasMaterialChange(
        ZaloDraftPreparationReminderState previous,
        ZaloDraftReadinessSnapshot readiness,
        int activeSlotRiskCount)
    {
        if (previous.LastSlotCount != readiness.EffectiveSlotCount ||
            previous.LastOpenOfferCount != Math.Max(0, activeSlotRiskCount))
            return true;

        var current = BuildFingerprint(readiness, activeSlotRiskCount);
        if (previous.LastFingerprint?.StartsWith(VersionPrefix, StringComparison.Ordinal) == true)
            return !string.Equals(previous.LastFingerprint, current, StringComparison.Ordinal);

        // Legacy rows stored only the authoritative roster/share fingerprint. Preserve
        // anti-spam on rollout: a format upgrade alone must not manufacture a reminder.
        // Once the row is silently upgraded, profile/readiness changes become observable
        // inside the same time bucket as well.
        return !string.Equals(previous.LastFingerprint, readiness.Fingerprint, StringComparison.Ordinal);
    }

    internal static bool NeedsSilentUpgrade(ZaloDraftPreparationReminderState previous) =>
        previous.LastFingerprint?.StartsWith(VersionPrefix, StringComparison.Ordinal) != true;
}
