using System.Text.RegularExpressions;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal enum ZaloRecruitmentGuestCommandKind
{
    Add,
    Cancel,
    UpdateProfile
}

internal sealed record ZaloRecruitmentGuestSpec(
    string? DisplayName = null,
    PlayerGender? Gender = null);

internal sealed record ZaloRecruitmentGuestCommand(
    ZaloRecruitmentGuestCommandKind Kind,
    int Quantity = 1,
    IReadOnlyList<ZaloRecruitmentGuestSpec>? Guests = null,
    int? SponsorSequence = null,
    string? GuestReference = null,
    string? RenameTo = null,
    PlayerGender? Gender = null,
    bool ApplyAll = false);

internal static class ZaloRecruitmentGuestPolicy
{
    private static readonly Regex PlusQuantity = new(
        @"(?<![a-z0-9])\+(?<count>[12])(?!\d)|(?<![a-z0-9])(?:them|cong|keo|dan|ru|co)\s*(?:them\s*)?(?<count2>[12])\s*(?:ban|dua|nguoi|khach)?(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ImplicitOne = new(
        @"(?<![a-z0-9])(?:\+\s*(?:ban|dua|nguoi|khach)|(?:them|keo|dan|ru)\s+(?:them\s+)?(?:ban|dua|nguoi|khach))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Cancel = new(
        @"(?<![a-z0-9])(?:(?<count>[12])\s*(?:ban|dua|nguoi)\s*(?:tui|toi|minh)?\s*(?:nghi|huy|khong di|ko di)|(?:ban|dua|nguoi)\s*(?:tui|toi|minh)\s*(?:nghi|huy|khong di|ko di)|(?<name>[a-z0-9][a-z0-9 ._-]{1,60})\s+(?:nghi|khong di|ko di))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SequenceRename = new(
        @"(?:ban|dua|nguoi)?\s*#?(?<seq>\d{1,2})\s*(?:ten|la)\s+(?<name>[^,.;]{2,80})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SequenceGender = new(
        @"(?:ban|dua|nguoi)?\s*#?(?<seq>\d{1,2})\s+(?<gender>nam|nu)(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NamedGender = new(
        @"^(?<name>[a-z0-9][a-z0-9 ._-]{1,60})\s+(?:la\s+)?(?<gender>nam|nu)(?:\s+(?:nha|nhe))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool LooksLikeAddRequest(string? content)
    {
        var parsed = TryParse(content);
        if (parsed?.Kind == ZaloRecruitmentGuestCommandKind.Add)
            return true;

        // Catch natural phrases that describe bringing an outside friend but do not
        // use the canonical +1/+2 syntax. This is routing-only: it must never itself
        // become mutation authority. Example: "nay tui di chung voi 1 ban o ngoai gr".
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        if (normalized.Length == 0) return false;

        var mentionsGuest = Regex.IsMatch(
            normalized,
            @"(?<![a-z0-9])(?:ban|dua|nguoi|khach)(?![a-z0-9])",
            RegexOptions.CultureInvariant);
        var outsideGroup = Regex.IsMatch(
            normalized,
            @"(?<![a-z0-9])(?:ngoai\s*(?:group|gr|nhom)|khong\s+(?:o|trong)\s+(?:group|gr|nhom)|chua\s+(?:o|trong)\s+(?:group|gr|nhom))(?![a-z0-9])",
            RegexOptions.CultureInvariant);
        var addMeaning = Regex.IsMatch(
            normalized,
            @"(?<![a-z0-9])(?:1|2|mot|hai|them|keo|dan|ru|di\s+chung|di\s+voi|choi\s+chung)(?![a-z0-9])",
            RegexOptions.CultureInvariant);
        return mentionsGuest && outsideGroup && addMeaning;
    }

    internal static ZaloRecruitmentGuestCommand? TryParse(string? content)
    {
        var original = (content ?? string.Empty).Trim();
        var normalized = ZaloBotIntelligence.Normalize(original);
        if (normalized.Length == 0) return null;

        // Preserve the exact display-name spelling/casing that the member typed. The
        // normalized form is only for intent detection and sequence extraction.
        var rename = SequenceRename.Match(normalized);
        if (rename.Success && int.TryParse(rename.Groups["seq"].Value, out var renameSeq))
        {
            var originalRename = Regex.Match(
                original,
                @"(?:bạn|ban|đứa|dua|người|nguoi)?\s*#?\d{1,2}\s*(?:tên|ten|là|la)\s+(?<name>[^,.;]{2,80})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var renameTo = originalRename.Success
                ? CleanName(originalRename.Groups["name"].Value)
                : CleanName(rename.Groups["name"].Value);
            return new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.UpdateProfile,
                SponsorSequence: renameSeq,
                RenameTo: renameTo);
        }

        // Cancellation must win over signup wording if a member says that their guest
        // is no longer going. It is still grounded later to this sender's reservations.
        var cancel = Cancel.Match(normalized);
        if (cancel.Success)
        {
            var quantity = int.TryParse(cancel.Groups["count"].Value, out var count) ? Math.Clamp(count, 1, 2) : 1;
            var name = CleanName(cancel.Groups["name"].Value);
            return new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.Cancel,
                Quantity: quantity,
                GuestReference: string.IsNullOrWhiteSpace(name) ? null : name,
                ApplyAll: quantity == 2 && (normalized.Contains("het", StringComparison.Ordinal) || normalized.Contains("ca 2", StringComparison.Ordinal)));
        }

        // Explicit +1/+2/add language is authoritative. Parse it before generic profile
        // language so "+2 bạn tui, 1 nam 1 nữ" remains an Add command rather than being
        // misclassified as a two-guest gender update.
        var plus = PlusQuantity.Match(normalized);
        int? quantityToAdd = null;
        if (plus.Success)
        {
            var rawCount = plus.Groups["count"].Success ? plus.Groups["count"].Value : plus.Groups["count2"].Value;
            if (int.TryParse(rawCount, out var parsedCount))
                quantityToAdd = Math.Clamp(parsedCount, 1, 2);
        }
        else if (ImplicitOne.IsMatch(normalized))
        {
            quantityToAdd = 1;
        }

        if (quantityToAdd is { } addCount)
        {
            var specs = ParseGuestSpecs(original, normalized, addCount);
            return new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.Add,
                Quantity: addCount,
                Guests: specs);
        }

        if (Regex.IsMatch(normalized, @"(?:2|hai)\s*(?:ban|dua|nguoi)?\s*(?:tui|toi|minh)?\s*(?:deu\s*)?(?:la\s*)?(?<gender>nam|nu)(?:\s|$)", RegexOptions.CultureInvariant))
        {
            var genderText = Regex.Match(normalized, @"(?<gender>nam|nu)(?:\s|$)", RegexOptions.CultureInvariant).Groups["gender"].Value;
            return new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.UpdateProfile,
                Quantity: 2,
                Gender: ParseGender(genderText),
                ApplyAll: true);
        }

        var sequenceGender = SequenceGender.Match(normalized);
        if (sequenceGender.Success && int.TryParse(sequenceGender.Groups["seq"].Value, out var genderSeq))
        {
            return new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.UpdateProfile,
                SponsorSequence: genderSeq,
                Gender: ParseGender(sequenceGender.Groups["gender"].Value));
        }

        // Profile completion is intentionally evaluated after cancellation and signup
        // language. Name references remain normalized because they are only used for
        // grounded matching against this sponsor's existing guest reservations.
        var namedGender = NamedGender.Match(normalized);
        if (namedGender.Success)
        {
            return new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.UpdateProfile,
                GuestReference: CleanName(namedGender.Groups["name"].Value),
                Gender: ParseGender(namedGender.Groups["gender"].Value));
        }

        return null;
    }

    private static IReadOnlyList<ZaloRecruitmentGuestSpec> ParseGuestSpecs(
        string original,
        string normalized,
        int quantity)
    {
        var result = Enumerable.Range(0, quantity)
            .Select(_ => new ZaloRecruitmentGuestSpec())
            .ToArray();

        if (quantity == 2 && Regex.IsMatch(normalized, @"1\s+nam\s+(?:1\s+)?nu|1\s+nu\s+(?:1\s+)?nam", RegexOptions.CultureInvariant))
        {
            var firstMale = normalized.IndexOf("nam", StringComparison.Ordinal) < normalized.IndexOf("nu", StringComparison.Ordinal);
            result[0] = result[0] with { Gender = firstMale ? PlayerGender.Male : PlayerGender.Female };
            result[1] = result[1] with { Gender = firstMale ? PlayerGender.Female : PlayerGender.Male };
        }
        else
        {
            var allGender = Regex.Match(normalized, @"(?:deu\s+)?(?<gender>nam|nu)(?:\s|$)", RegexOptions.CultureInvariant);
            if (allGender.Success && normalized.Contains("deu", StringComparison.Ordinal))
            {
                var gender = ParseGender(allGender.Groups["gender"].Value);
                for (var index = 0; index < result.Length; index += 1)
                    result[index] = result[index] with { Gender = gender };
            }
        }

        var named = Regex.Match(
            original,
            @"(?:\+\s*[12]|thêm\s*[12]|them\s*[12]|kéo\s*(?:thêm\s*)?[12]|keo\s*(?:them\s*)?[12])\s*(?:bạn|ban|đứa|dua|người|nguoi|khách|khach)?\s*(?<names>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!named.Success) return result;

        var tail = named.Groups["names"].Value.Trim(' ', ',', '.', ':', ';');
        tail = Regex.Replace(
            tail,
            @"\s+(?:đều|deu)\s+(?:nam|nữ|nu)\s*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        tail = Regex.Replace(
            tail,
            @"\s*,?\s*1\s+(?:nam|nữ|nu)\s+1\s+(?:nam|nữ|nu)\s*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        tail = Regex.Replace(
            tail,
            @"^(?:tên|ten)\s+(?:là|la)?\s*",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // "mình" is a pronoun, while the very common name "Minh" must remain valid.
        // For unaccented chat, lowercase "minh" is treated conservatively as a pronoun;
        // a member can still provide the explicit name as "Minh" or rename later.
        if (quantity == 1 && IsStandaloneSelfPronoun(tail))
            return result;

        // Normalize only separators, never names themselves, so "Minh với Huy" keeps
        // display casing while still accepting accented/unaccented conjunctions.
        tail = Regex.Replace(
            tail,
            @"\s+(?:và|va|với|voi)\s+",
            ",",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        tail = tail.Replace('&', ',');
        var pieces = tail
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(quantity)
            .ToList();

        if (pieces.Count == quantity && pieces.All(IsPlausibleName))
        {
            for (var index = 0; index < quantity; index += 1)
                result[index] = result[index] with { DisplayName = pieces[index] };
        }
        else if (quantity == 1 && IsPlausibleName(CleanName(tail)))
        {
            result[0] = result[0] with { DisplayName = CleanName(tail) };
        }

        return result;
    }

    private static PlayerGender? ParseGender(string? value) => value switch
    {
        "nam" => PlayerGender.Male,
        "nu" => PlayerGender.Female,
        _ => null
    };

    private static bool IsStandaloneSelfPronoun(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Equals("mình", StringComparison.OrdinalIgnoreCase)) return true;
        return trimmed.Equals("minh", StringComparison.Ordinal);
    }

    private static bool IsPlausibleName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 2 or > 80) return false;
        var normalized = ZaloBotIntelligence.Normalize(value);
        if (normalized is "tui" or "toi" or "ban" or "dua" or "nguoi" or "khach" or
            "ban tui" or "ban toi" or "ban minh" or "dua tui" or "dua toi" or "dua minh")
            return false;
        if (Regex.IsMatch(normalized, @"^(?:t[2-7]|cn|thu\s*[2-7]|chu\s*nhat)(?:\s|$)", RegexOptions.CultureInvariant))
            return false;
        return true;
    }

    private static string? CleanName(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim(' ', ',', '.', ':', ';', '"', '\'');
        return cleaned.Length == 0 ? null : cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }
}
