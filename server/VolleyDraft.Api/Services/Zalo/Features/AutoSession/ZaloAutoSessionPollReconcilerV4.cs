namespace VolleyDraft.Api.Services;

internal enum ZaloAutoSessionPollChangeKindV4
{
    OptionAdded,
    OptionRemoved,
    OptionIdentityChanged,
    ExplicitStartTimeChanged,
    VoteCountChanged,
    ApprovedLocationChanged,
    ApprovedTeamSizeChanged
}

internal sealed record ZaloAutoSessionPollChangeV4(
    ZaloAutoSessionPollChangeKindV4 Kind,
    string OptionId,
    string? Before,
    string? After,
    bool RequiresConfirmation);

internal sealed record ZaloAutoSessionPollReconciliationV4(
    ZaloAutoSessionConversationDraft Draft,
    IReadOnlyList<ZaloAutoSessionPollChangeV4> Changes,
    bool RequiresConfirmation)
{
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Deterministic Auto Session V4 poll reconciliation.
///
/// The caller supplies three independently meaningful states:
/// - sourceSnapshot: authoritative poll/policy-derived draft captured when the proposal was born;
/// - durableDraft: latest organizer-approved MatchProposal draft;
/// - currentSnapshot: newly parsed authoritative poll plus current approved group-policy state.
///
/// This separation is important: comparing only the durable draft to the current poll cannot
/// tell an organizer correction from a source/policy mutation. AI has no role in this decision.
/// </summary>
internal static class ZaloAutoSessionPollReconcilerV4
{
    public static ZaloAutoSessionPollReconciliationV4 Reconcile(
        ZaloAutoSessionConversationDraft sourceSnapshot,
        ZaloAutoSessionConversationDraft durableDraft,
        ZaloAutoSessionConversationDraft currentSnapshot)
    {
        var sourceById = sourceSnapshot.Items.ToDictionary(item => item.OptionId, StringComparer.Ordinal);
        var durableById = durableDraft.Items.ToDictionary(item => item.OptionId, StringComparer.Ordinal);
        var currentById = currentSnapshot.Items.ToDictionary(item => item.OptionId, StringComparer.Ordinal);
        var changes = new List<ZaloAutoSessionPollChangeV4>();
        var reconciled = new List<ZaloAutoSessionConversationDraftItem>();

        foreach (var source in sourceSnapshot.Items)
        {
            if (currentById.ContainsKey(source.OptionId)) continue;
            changes.Add(new ZaloAutoSessionPollChangeV4(
                ZaloAutoSessionPollChangeKindV4.OptionRemoved,
                source.OptionId,
                DescribeIdentity(source),
                null,
                true));
        }

        foreach (var current in currentSnapshot.Items)
        {
            if (!sourceById.TryGetValue(current.OptionId, out var source))
            {
                changes.Add(new ZaloAutoSessionPollChangeV4(
                    ZaloAutoSessionPollChangeKindV4.OptionAdded,
                    current.OptionId,
                    null,
                    DescribeIdentity(current),
                    true));
                // A newly introduced poll choice was never approved by the organizer. Keep it
                // visible for clarification, but never silently opt it into execution.
                reconciled.Add(current with { Selected = false });
                continue;
            }

            var identityChanged =
                !string.Equals(source.OptionContent, current.OptionContent, StringComparison.Ordinal) ||
                !string.Equals(source.DayKey, current.DayKey, StringComparison.Ordinal);
            if (identityChanged)
            {
                changes.Add(new ZaloAutoSessionPollChangeV4(
                    ZaloAutoSessionPollChangeKindV4.OptionIdentityChanged,
                    current.OptionId,
                    DescribeIdentity(source),
                    DescribeIdentity(current),
                    true));
            }

            var durable = durableById.TryGetValue(current.OptionId, out var saved)
                ? saved
                : source;

            var sourceStartChanged = source.StartTime != current.StartTime;
            if (sourceStartChanged)
            {
                changes.Add(new ZaloAutoSessionPollChangeV4(
                    ZaloAutoSessionPollChangeKindV4.ExplicitStartTimeChanged,
                    current.OptionId,
                    source.StartTime.ToString("O"),
                    current.StartTime.ToString("O"),
                    true));
            }

            if (source.VoteCount != current.VoteCount)
            {
                changes.Add(new ZaloAutoSessionPollChangeV4(
                    ZaloAutoSessionPollChangeKindV4.VoteCountChanged,
                    current.OptionId,
                    source.VoteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    current.VoteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    false));
            }

            // Poll-owned identity and newly explicit source time come from current authority.
            // Organizer-owned selection remains intact for surviving options. An organizer time
            // correction survives only while the authoritative source time itself did not change.
            reconciled.Add(new ZaloAutoSessionConversationDraftItem(
                current.OptionId,
                current.OptionContent,
                current.DayKey,
                sourceStartChanged ? current.StartTime : durable.StartTime,
                current.VoteCount,
                durable.Selected));
        }

        // Group policy is authoritative only while the organizer has not explicitly overridden
        // that field. This mirrors V4 provenance semantics without allowing a later admin default
        // change to erase an organizer-owned correction.
        var organizerChangedLocation = !string.Equals(
            durableDraft.Location,
            sourceSnapshot.Location,
            StringComparison.Ordinal);
        var locationPolicyChanged = !string.Equals(
            sourceSnapshot.Location,
            currentSnapshot.Location,
            StringComparison.Ordinal);
        var reconciledLocation = organizerChangedLocation
            ? durableDraft.Location
            : currentSnapshot.Location;
        if (!organizerChangedLocation && locationPolicyChanged)
        {
            changes.Add(new ZaloAutoSessionPollChangeV4(
                ZaloAutoSessionPollChangeKindV4.ApprovedLocationChanged,
                "location",
                sourceSnapshot.Location,
                currentSnapshot.Location,
                false));
        }

        var organizerChangedTeamSize = durableDraft.TeamSize != sourceSnapshot.TeamSize;
        var teamSizePolicyChanged = sourceSnapshot.TeamSize != currentSnapshot.TeamSize;
        var reconciledTeamSize = organizerChangedTeamSize
            ? durableDraft.TeamSize
            : currentSnapshot.TeamSize;
        if (!organizerChangedTeamSize && teamSizePolicyChanged)
        {
            changes.Add(new ZaloAutoSessionPollChangeV4(
                ZaloAutoSessionPollChangeKindV4.ApprovedTeamSizeChanged,
                "team_size",
                sourceSnapshot.TeamSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                currentSnapshot.TeamSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                false));
        }

        var requiresConfirmation = changes.Any(change => change.RequiresConfirmation);
        return new ZaloAutoSessionPollReconciliationV4(
            new ZaloAutoSessionConversationDraft(
                reconciled,
                reconciledLocation,
                reconciledTeamSize),
            changes,
            requiresConfirmation);
    }

    private static string DescribeIdentity(ZaloAutoSessionConversationDraftItem item) =>
        $"{item.OptionContent}|{item.DayKey}";
}
