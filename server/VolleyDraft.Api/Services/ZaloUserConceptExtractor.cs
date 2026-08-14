using System.Text.Json;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

public sealed record ZaloUserConceptDraft(
    string ConceptType,
    string Key,
    string ValueJson,
    double Confidence = 1.0,
    string Scope = "User",
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// High-precision deterministic extraction for explicit self facts/preferences.
/// This intentionally does not infer concepts from ordinary group chatter.
/// Stored values stay structured and minimal so quoted user text is not replayed
/// into future AI prompts as pseudo-instructions.
/// </summary>
public static class ZaloUserConceptExtractor
{
    private static readonly Regex PreferredNameOriginalRegex = new(
        @"\b(?:gọi|goi|kêu|keu)\s+(?:tui|tôi|toi|mình|minh|em)\s+(?:(?:là|la)\s+)?(?<name>[\p{L}][\p{L}\p{M}\d ._-]{0,39})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryExtract(string question, ZaloAiSender sender, out ZaloUserConceptDraft draft)
    {
        draft = null!;
        var original = (question ?? string.Empty).Trim();
        var normalized = ZaloBotIntelligence.Normalize(original);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        normalized = Regex.Replace(normalized, @"^(?:@?bot|@?npc)\s+", string.Empty, RegexOptions.CultureInvariant).Trim();
        normalized = Regex.Replace(normalized, @"^(?:nho la|nho rang|tu gio|lan sau)\s+", string.Empty, RegexOptions.CultureInvariant).Trim();

        var preferredName = PreferredNameOriginalRegex.Match(original);
        if (preferredName.Success)
        {
            var name = preferredName.Groups["name"].Value.Trim(' ', '.', ',', '!', '?');
            if (name.Length is >= 1 and <= 40 && !LooksLikeInstruction(ZaloBotIntelligence.Normalize(name)))
            {
                draft = new ZaloUserConceptDraft(
                    "Alias",
                    "preferred_name",
                    JsonSerializer.Serialize(new { name }),
                    1.0);
                return true;
            }
        }

        var sessions = ExtractSessionTokens(normalized);
        if (sessions.Count > 0 && RefersToSelf(normalized) && LooksLikeSessionPreference(normalized))
        {
            var mode = HasAny(normalized, "khong danh", "khong choi", "khong di", "nghi", "khong tham gia")
                ? "avoid"
                : HasAny(normalized, "chi danh", "chi choi", "chi di")
                    ? "only"
                    : HasAny(normalized, "danh duoc", "choi duoc", "di duoc", "tham gia duoc")
                        ? "available"
                        : "prefer";
            draft = new ZaloUserConceptDraft(
                "Preference",
                "session_availability",
                JsonSerializer.Serialize(new { sessions, mode }),
                .98);
            return true;
        }

        if (RefersToSelf(normalized) && TryExtractRole(normalized, out var role))
        {
            draft = new ZaloUserConceptDraft(
                "DomainFact",
                "volleyball_role",
                JsonSerializer.Serialize(new { role }),
                .98);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> ExtractSessionTokens(string normalized)
    {
        var result = new List<string>();
        for (var day = 2; day <= 7; day++)
        {
            if (Regex.IsMatch(normalized, $@"(?<![a-z0-9])(?:t{day}|thu\s*{day})(?![a-z0-9])", RegexOptions.CultureInvariant))
                result.Add($"T{day}");
        }
        if (Regex.IsMatch(normalized, @"(?<![a-z0-9])(?:cn|chu nhat)(?![a-z0-9])", RegexOptions.CultureInvariant))
            result.Add("CN");
        return result;
    }

    private static bool TryExtractRole(string normalized, out string role)
    {
        role = string.Empty;
        if (!HasAny(normalized, "danh", "choi", "vi tri", "la")) return false;
        if (HasAny(normalized, "libero")) role = "Libero";
        else if (HasAny(normalized, "chuyen 2", "chuyen hai", "setter")) role = "Setter";
        else if (HasAny(normalized, "phu cong", "middle")) role = "Middle";
        else if (HasAny(normalized, "chu cong", "outside")) role = "Outside";
        else if (HasAny(normalized, "doi chuyen", "opposite")) role = "Opposite";
        return role.Length > 0;
    }

    private static bool RefersToSelf(string normalized) =>
        Regex.IsMatch(normalized, @"(?<![a-z0-9])(?:tui|toi|minh|em)(?![a-z0-9])", RegexOptions.CultureInvariant);

    private static bool LooksLikeSessionPreference(string normalized) => HasAny(
        normalized,
        "hay danh", "thuong danh", "thich danh", "hay choi", "thuong choi", "thich choi",
        "chi danh", "chi choi", "chi di", "khong danh", "khong choi", "khong di", "nghi",
        "danh duoc", "choi duoc", "di duoc", "tham gia duoc");

    private static bool LooksLikeInstruction(string value) => HasAny(
        value,
        "tra loi", "thuc hien", "xoa", "doi team", "draft", "chuyen slot", "gui tin",
        "bo qua", "chi dan", "system", "prompt", "instruction", "ignore", "previous", "developer", "assistant");

    private static bool HasAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.Ordinal));
}
