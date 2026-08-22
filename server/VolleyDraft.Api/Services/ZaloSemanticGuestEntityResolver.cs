using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal enum ZaloSemanticGuestEntityResolutionStatus
{
    Resolved,
    Ambiguous,
    NotFound
}

internal sealed record ZaloSemanticGuestEntityResolution(
    ZaloSemanticGuestEntityResolutionStatus Status,
    ZaloSemanticGuestGroundingGuest? Guest,
    IReadOnlyList<ZaloSemanticGuestGroundingGuest> Candidates,
    string Source);

/// <summary>
/// Deterministic entity binder for semantic guest references. AI may describe a
/// reference ("nó", "bạn thứ hai", "Minh hồi nãy") but this resolver is the only
/// component allowed to bind that language to a reservation ID supplied by DB.
/// </summary>
internal static class ZaloSemanticGuestEntityResolver
{
    internal static ZaloSemanticGuestEntityResolution Resolve(
        ZaloSemanticGuestPlanItem source,
        ZaloSemanticGuestGroundingSnapshot snapshot)
    {
        var guests = snapshot.ExistingGuests.OrderBy(item => item.SponsorSequence).ToArray();
        if (guests.Length == 0) return NotFound("no_grounded_guests");

        if (!string.IsNullOrWhiteSpace(source.ReservationId))
        {
            var exact = guests.SingleOrDefault(item =>
                string.Equals(item.ReservationId, source.ReservationId, StringComparison.Ordinal));
            return exact is null ? NotFound("reservation_id_not_grounded") : Resolved(exact, "reservation_id");
        }

        if (source.SponsorSequence is { } sequence)
        {
            var exact = guests.SingleOrDefault(item => item.SponsorSequence == sequence);
            return exact is null ? NotFound("sponsor_sequence_not_grounded") : Resolved(exact, "sponsor_sequence");
        }

        var reference = ZaloBotIntelligence.Normalize(source.ReferenceText ?? string.Empty).Trim();
        if (reference.Length == 0)
            return guests.Length == 1 ? Resolved(guests[0], "single_grounded_guest") : Ambiguous(guests, "missing_reference");

        var ordinal = ResolveOrdinal(reference, guests);
        if (ordinal is not null) return Resolved(ordinal, "ordinal_reference");

        var nameMatches = guests.Where(item =>
        {
            var name = ZaloBotIntelligence.Normalize(item.DisplayName);
            return name.Length > 0 &&
                   (name.Equals(reference, StringComparison.Ordinal) ||
                    name.Contains(reference, StringComparison.Ordinal) ||
                    reference.Contains(name, StringComparison.Ordinal));
        }).Take(3).ToArray();
        if (nameMatches.Length == 1) return Resolved(nameMatches[0], "display_name");
        if (nameMatches.Length > 1) return Ambiguous(nameMatches, "duplicate_display_name");

        if (IsRecentPronoun(reference))
        {
            if (snapshot.AnchorKind == ZaloSemanticGuestAnchorKind.RecentGuestMutation)
            {
                if (guests.Length == 1) return Resolved(guests[0], "recent_mutation_pronoun");
                return Ambiguous(guests, "recent_mutation_pronoun_ambiguous");
            }
            if (guests.Length == 1) return Resolved(guests[0], "single_guest_pronoun");
        }

        if (reference.Contains("nam", StringComparison.Ordinal) || reference.Contains("nu", StringComparison.Ordinal))
        {
            var gender = reference.Contains("nu", StringComparison.Ordinal) ? PlayerGender.Female : PlayerGender.Male;
            var genderMatches = guests.Where(item => item.Gender == gender).Take(3).ToArray();
            if (genderMatches.Length == 1) return Resolved(genderMatches[0], "unique_gender_reference");
            if (genderMatches.Length > 1) return Ambiguous(genderMatches, "gender_reference_ambiguous");
        }

        return NotFound("reference_not_grounded");
    }

    private static ZaloSemanticGuestGroundingGuest? ResolveOrdinal(
        string reference,
        IReadOnlyList<ZaloSemanticGuestGroundingGuest> guests)
    {
        if (guests.Count == 0) return null;
        if (reference.Contains("#1", StringComparison.Ordinal) ||
            reference is "1" or "ban 1" or "guest 1" or "ban dau" or "dua dau" or "thang dau" or
            "ban thu nhat" or "guest dau" or "nguoi dau")
            return guests.ElementAtOrDefault(0);
        if (reference.Contains("#2", StringComparison.Ordinal) ||
            reference is "2" or "ban 2" or "guest 2" or "ban sau" or "dua sau" or "thang sau" or
            "ban thu hai" or "guest thu hai" or "nguoi thu hai")
            return guests.ElementAtOrDefault(1);
        if (reference.Contains("#3", StringComparison.Ordinal) || reference is "ban 3" or "guest 3" or "ban thu ba")
            return guests.ElementAtOrDefault(2);
        if (reference.Contains("#4", StringComparison.Ordinal) || reference is "ban 4" or "guest 4" or "ban thu tu")
            return guests.ElementAtOrDefault(3);
        return null;
    }

    private static bool IsRecentPronoun(string reference) => reference is
        "no" or "ban do" or "dua do" or "thang do" or "nguoi do" or "ban kia" or "dua kia" or
        "thang kia" or "ban vua them" or "dua vua them" or "guest vua them" or "ban hoi nay" or
        "dua hoi nay" or "ban vua noi" or "dua vua noi";

    private static ZaloSemanticGuestEntityResolution Resolved(ZaloSemanticGuestGroundingGuest guest, string source) =>
        new(ZaloSemanticGuestEntityResolutionStatus.Resolved, guest, [guest], source);

    private static ZaloSemanticGuestEntityResolution Ambiguous(IReadOnlyList<ZaloSemanticGuestGroundingGuest> guests, string source) =>
        new(ZaloSemanticGuestEntityResolutionStatus.Ambiguous, null, guests, source);

    private static ZaloSemanticGuestEntityResolution NotFound(string source) =>
        new(ZaloSemanticGuestEntityResolutionStatus.NotFound, null, [], source);
}
