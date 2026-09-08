using System.Security.Cryptography;
using System.Text;

namespace VolleyDraft.Api.Services;

internal static class ZaloDraftPreparationReminderObservation
{
    private const string VersionPrefix = "obs3:";
    private const string PreviousVersionPrefix = "obs2:";
    internal static readonly TimeSpan SameBucketRefreshInterval = TimeSpan.FromMinutes(2);

    internal static string BuildFingerprint(
        ZaloDraftReadinessSnapshot readiness,
        int activeSlotRiskCount)
    {
        var missingProfiles = readiness.MissingProfileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var missingProfileIdentity = BuildFramedCanonical(missingProfiles);

        var canonical = BuildFramedCanonical(
            readiness.Fingerprint ?? string.Empty,
            readiness.State.ToString(),
            readiness.EffectiveSlotCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            readiness.PresentPlayerCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            readiness.Capacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            readiness.MissingProfileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            missingProfileIdentity,
            readiness.HasTeams ? "1" : "0",
            readiness.CanEscalate ? "1" : "0",
            Math.Max(0, activeSlotRiskCount).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Hash(VersionPrefix, canonical);
    }

    internal static string BuildIdempotencySuffix(
        ZaloDraftReadinessSnapshot readiness,
        int activeSlotRiskCount) =>
        BuildFingerprint(readiness, activeSlotRiskCount)[VersionPrefix.Length..(VersionPrefix.Length + 24)];

    internal static bool ShouldRefreshSameBucket(
        ZaloDraftPreparationReminderState previous,
        DateTimeOffset now)
    {
        // Product state can move in both directions between reminder sends: a clean roster
        // can gain a new pass/share blocker, and an existing blocker can be completed or
        // cancelled. The heavy worker already runs on a two-minute cadence, so every
        // same-bucket observation is eligible on that cadence. Material fingerprint
        // suppression still prevents duplicate notifications when nothing actually changed.
        return now - previous.UpdatedAt >= SameBucketRefreshInterval;
    }

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

        if (previous.LastFingerprint?.StartsWith(PreviousVersionPrefix, StringComparison.Ordinal) == true)
        {
            // obs2 did not include missing-profile identity. Compare using the exact old
            // algorithm first so deploying obs3 cannot manufacture a duplicate reminder.
            // The unchanged row is then silently upgraded to obs3 by the caller; subsequent
            // same-bucket observations can detect A-completed/B-became-missing transitions.
            var previousVersionCurrent = BuildPreviousVersionFingerprint(readiness, activeSlotRiskCount);
            return !string.Equals(previous.LastFingerprint, previousVersionCurrent, StringComparison.Ordinal);
        }

        // Pre-observation rows stored only the authoritative roster/share fingerprint.
        // Preserve anti-spam on rollout: a format upgrade alone must not manufacture a reminder.
        return !string.Equals(previous.LastFingerprint, readiness.Fingerprint, StringComparison.Ordinal);
    }

    internal static bool NeedsSilentUpgrade(ZaloDraftPreparationReminderState previous) =>
        previous.LastFingerprint?.StartsWith(VersionPrefix, StringComparison.Ordinal) != true;

    internal static string BuildPreviousVersionFingerprintForEval(
        ZaloDraftReadinessSnapshot readiness,
        int activeSlotRiskCount) =>
        BuildPreviousVersionFingerprint(readiness, activeSlotRiskCount);

    private static string BuildPreviousVersionFingerprint(
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
        return Hash(PreviousVersionPrefix, canonical);
    }

    private static string BuildFramedCanonical(params string[] fields)
    {
        var builder = new StringBuilder();
        foreach (var field in fields)
        {
            builder.Append(field.Length)
                .Append(':')
                .Append(field);
        }
        return builder.ToString();
    }

    private static string Hash(string prefix, string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
