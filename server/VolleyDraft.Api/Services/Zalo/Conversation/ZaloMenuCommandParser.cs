using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services.Zalo.Conversation;

/// <summary>
/// Parses numeric bot menu commands without leaking session parsing rules into feature handlers.
/// </summary>
public static class ZaloMenuCommandParser
{
    private static readonly Regex MenuCommandRegex = new(
        @"^(?:@?(?:[a-z0-9._-]*bot|npc|volley\s*bot)\s+)?(?<command>10|12|[1-9])(?:\s+(?<reference>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string value, out int command, out string? sessionReference)
    {
        command = 0;
        sessionReference = null;

        var normalized = ZaloTextNormalizer.Normalize(value);
        var match = MenuCommandRegex.Match(normalized);
        if (!match.Success || !int.TryParse(match.Groups["command"].Value, out command))
        {
            command = 0;
            return false;
        }

        var reference = match.Groups["reference"].Value.Trim();
        if (reference.Length == 0)
            return true;

        if (!ZaloSessionResolver.LooksLikeSelector(reference))
        {
            command = 0;
            return false;
        }

        sessionReference = reference;
        return true;
    }
}
