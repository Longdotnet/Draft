using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

/// <summary>
/// High-confidence English same-team requests. This is an operational grammar,
/// not a general language detector: other phrasings still go through semantic AI.
/// </summary>
internal static class ZaloEnglishTogetherPreferenceParser
{
    private static readonly Regex Request = new(
        @"^(?:(?:i(?:['’]d|\s+would)\s+like|i\s+(?:want|wanna))\s+to\s+(?:be|play)\s+(?:on|in)\s+(?:the\s+)?same\s+team\s+(?:as|with)|(?:please\s+)?(?:put|place|keep)\s+me\s+(?:on|in)\s+(?:the\s+)?same\s+team\s+(?:as|with))\s+(?<other>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryParse(string? question, out ZaloTeamPreferenceCommand command)
    {
        command = new ZaloTeamPreferenceCommand([]);
        if (string.IsNullOrWhiteSpace(question) || question.Contains('?', StringComparison.Ordinal)) return false;
        var match = Request.Match(question.Trim());
        if (!match.Success) return false;
        var other = ZaloNaturalCommandParser.RemoveTrailingSessionReference(
            match.Groups["other"].Value, out var sessionReference);
        other = other.Trim(' ', '@', ',', '.', '!', '?');
        if (other.Length < 2 || other.Contains('@', StringComparison.Ordinal)) return false;
        command = new ZaloTeamPreferenceCommand(
            ["me", other],
            SessionReference: sessionReference,
            Relation: ZaloTeamRelationshipKind.Together);
        return true;
    }
}
