using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Deterministic plain-text wake phrases for users who address the bot without a
/// native Zalo @mention. This is intentionally narrow: it only detects short direct
/// calls to bot/NPC and never authorizes a domain mutation.
/// </summary>
public static partial class ZaloAmbientWakePhrase
{
    [GeneratedRegex(
        @"^(?:(?:e|alo|oi)\s+)?(?:bot|npc)(?:\s+oi)?(?:\s+(?:bot|npc))?(?:\s+(?:dau|day|con\s+song(?:\s+(?:khong|ko|k))?))?[!?.]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex WakeRegex();

    public static bool IsMatch(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        return normalized.Length > 0 && WakeRegex().IsMatch(normalized);
    }

    public static string BuildReply(string? senderName)
    {
        var name = (senderName ?? string.Empty).Trim();
        var prefix = name.Length > 0 ? $"{name} ơi, " : string.Empty;
        return $"{prefix}tui đây 😄. Cứ hỏi tự nhiên nha — lịch, slot, roster, sân, vote hay xếp team đều được.";
    }
}
