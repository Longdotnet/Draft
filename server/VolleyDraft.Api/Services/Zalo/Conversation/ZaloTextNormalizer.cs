using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services.Zalo.Conversation;

/// <summary>
/// Single text-normalization primitive for every deterministic Zalo feature.
/// Domain features should depend on this instead of keeping their own accent/case regex helpers.
/// </summary>
public static class ZaloTextNormalizer
{
    public static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty)
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character == 'đ' ? 'd' : character);
            }
        }

        return Regex.Replace(
                builder.ToString().Normalize(NormalizationForm.FormC),
                @"\s+",
                " ",
                RegexOptions.CultureInvariant)
            .Trim();
    }
}
