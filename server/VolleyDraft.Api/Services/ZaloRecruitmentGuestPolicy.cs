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

    private static readonly Regex Cancel = new(
        @"(?<![a-z0-9])(?:(?<count>[12])\s*(?:ban|dua|nguoi)\s*(?:tui|toi|minh)?\s*(?:nghi|huy|khong di|ko di)|(?:ban|dua|nguoi)\s*(?:tui|toi|minh)\s*(?:nghi|huy|khong di|ko di)|(?<name>[a-z0-9][a-z0-9 ._-]{1,60})\s+(?:nghi|khong di|ko di))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SequenceRename = new(
        @"(?:ban|dua|nguoi)?\s*#?(?<seq>\d{1,2})\s*(?:ten|la)\s+(?<name>[^,.;]{2,80})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SequenceGender = new(
        @"(?:ban|dua|nguoi)?\s*#?(?<seq>\d{1,2})\s+(?<gender>nam|nu)(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static ZaloRecruitmentGuestCommand? TryParse(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        if (normalized.Length == 0) return null;

        var rename = SequenceRename.Match(normalized);
        if (rename.Success && int.TryParse(rename.Groups["seq"].Value, out var renameSeq))
        {
            return new ZaloRecruitmentGuestCommand(
                ZaloRecruitmentGuestCommandKind.UpdateProfile,
                SponsorSequence: renameSeq,
                RenameTo: CleanName(rename.Groups["name"].Value));
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

        var plus = PlusQuantity.Match(normalized);
        if (!plus.Success) return null;
        var rawCount = plus.Groups["count"].Success ? plus.Groups["count"].Value : plus.Groups["count2"].Value;
        if (!int.TryParse(rawCount, out var parsedCount)) return null;
        var quantityToAdd = Math.Clamp(parsedCount, 1, 2);

        var specs = ParseGuestSpecs(content ?? string.Empty, normalized, quantityToAdd);
        return new ZaloRecruitmentGuestCommand(
            ZaloRecruitmentGuestCommandKind.Add,
            Quantity: quantityToAdd,
            Guests: specs);
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
        if (named.Success)
        {
            var tail = named.Groups["names"].Value.Trim(' ', ',', '.', ':', ';');
            tail = Regex.Replace(tail, @"^(?:tui|toi|minh)\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            tail = Regex.Replace(tail, @"^(?:tên|ten)\s+(?:là|la)?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            tail = Regex.Replace(tail, @"\s+(?:đều|deu)\s+(?:nam|nữ|nu)\s*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            tail = Regex.Replace(tail, @"\s*,?\s*1\s+(?:nam|nữ|nu)\s+1\s+(?:nam|nữ|nu)\s*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var pieces = Regex.Split(tail, @"\s*(?:,|\bvà\b|\bva\b|\bvới\b|\bvoi\b|\b&\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
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
        }

        return result;
    }

    private static PlayerGender? ParseGender(string? value) => value switch
    {
        "nam" => PlayerGender.Male,
        "nu" => PlayerGender.Female,
        _ => null
    };

    private static bool IsPlausibleName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 2 or > 80) return false;
        var normalized = ZaloBotIntelligence.Normalize(value);
        return normalized is not "ban tui" and not "ban toi" and not "ban minh" and not "dua tui" and not "dua toi" and not "dua minh";
    }

    private static string? CleanName(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim(' ', ',', '.', ':', ';', '"', '\'');
        return cleaned.Length == 0 ? null : cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }
}
