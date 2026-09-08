using System.Globalization;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloAutoSessionCapacityResolutionV5(
    bool HasExplicitCapacity,
    bool IsValid,
    int Capacity,
    int TeamSize,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Deterministic capacity authority for Auto Session V5.
///
/// VolleyDraft currently supports exactly three teams. A poll may therefore override the
/// approved per-team default only when it explicitly states a total capacity that can be
/// represented safely by the current three-team domain model. AI never participates here.
/// </summary>
internal static partial class ZaloAutoSessionCapacityPolicyV5
{
    internal const int SupportedTeamCount = 3;

    [GeneratedRegex(
        @"(?<![a-z0-9])(?:max|toi\s*da|capacity)\s*(?<capacity>\d{1,2})\s*(?:slot|slots|nguoi)(?:\s*/\s*san)?(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitCapacityRegex();

    public static ZaloAutoSessionCapacityResolutionV5 Resolve(string? pollQuestion)
    {
        var normalized = ZaloPollScheduleParser.NormalizeText(pollQuestion);
        var match = ExplicitCapacityRegex().Match(normalized);
        if (!match.Success)
            return new ZaloAutoSessionCapacityResolutionV5(false, true, 0, 0, null, null);

        if (!int.TryParse(
                match.Groups["capacity"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var capacity) ||
            capacity < SupportedTeamCount * 2 ||
            capacity > SupportedTeamCount * 30 ||
            capacity % SupportedTeamCount != 0)
        {
            return new ZaloAutoSessionCapacityResolutionV5(
                true,
                false,
                capacity,
                0,
                "explicit_capacity_not_supported",
                $"Poll ghi tối đa {capacity} slot nhưng hệ thống hiện chia cố định {SupportedTeamCount} đội. " +
                $"Hãy dùng tổng slot chia hết cho {SupportedTeamCount} (mỗi đội 2-30 người) hoặc sửa lại poll trước khi tạo website.");
        }

        return new ZaloAutoSessionCapacityResolutionV5(
            true,
            true,
            capacity,
            capacity / SupportedTeamCount,
            null,
            null);
    }
}
