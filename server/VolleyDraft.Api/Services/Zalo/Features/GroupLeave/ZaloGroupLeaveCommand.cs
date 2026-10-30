using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

internal enum ZaloGroupLeaveAction
{
    None,
    Request,
    Confirm,
    Cancel
}

/// <summary>
/// A deliberately narrow parser: a group leave must never be inferred by AI,
/// quoted conversation or an unrelated mention of the word "out".
/// </summary>
internal static class ZaloGroupLeaveCommand
{
    public static ZaloGroupLeaveAction Parse(string? question, bool explicitlyMentioned)
    {
        var text = Normalize(question);
        var invocation = Regex.Match(text, @"^(?:bot|bott)\s+oi(?:\s+|$)", RegexOptions.CultureInvariant);
        if (invocation.Success)
            text = text[invocation.Length..].Trim();

        // An untagged command is only accepted when it starts with "bot ơi".
        if (!explicitlyMentioned && !invocation.Success)
            return ZaloGroupLeaveAction.None;

        if (Regex.IsMatch(text, @"^(?:tu\s+)?(?:out|roi|thoat)\s+(?:khoi\s+)?(?:nhom|group)(?:\s+di)?$", RegexOptions.CultureInvariant))
            return ZaloGroupLeaveAction.Request;

        if (Regex.IsMatch(text, @"^(?:xac\s+nhan|dong\s+y)\s+(?:cho\s+bot\s+)?(?:out|roi|thoat)\s+(?:khoi\s+)?(?:nhom|group)$", RegexOptions.CultureInvariant))
            return ZaloGroupLeaveAction.Confirm;

        if (Regex.IsMatch(text, @"^(?:huy|thoi\s+khong|huy\s+lenh)\s+(?:cho\s+bot\s+)?(?:out|roi|thoat)\s+(?:khoi\s+)?(?:nhom|group)$", RegexOptions.CultureInvariant))
            return ZaloGroupLeaveAction.Cancel;

        return ZaloGroupLeaveAction.None;
    }

    private static string Normalize(string? input)
    {
        var value = (input ?? string.Empty).ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }
        var plain = Regex.Replace(builder.ToString(), @"[^a-z0-9\s]", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(plain, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }
}
