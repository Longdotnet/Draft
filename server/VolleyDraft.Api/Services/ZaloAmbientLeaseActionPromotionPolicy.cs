using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

public sealed record ZaloAmbientLeaseActionPromotion(
    ZaloBotIntent Intent,
    string PromotedContent);

/// <summary>
/// Small allowlist for actions that are safe to enter through an active conversational
/// lease because the legacy router previews them and creates a pending confirmation
/// before any domain mutation. This policy does not grant confirmation authority.
/// </summary>
public static class ZaloAmbientLeaseActionPromotionPolicy
{
    private static readonly Regex NaturalDraftPattern = new(
        @"(?<![a-z0-9])(?:xep|chia)\s+(?:team|doi)(?![a-z0-9])|(?<![a-z0-9])draft(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ZaloAmbientLeaseActionPromotion? TryCreate(string? content)
    {
        var raw = (content ?? string.Empty).Trim();
        if (raw.Length == 0) return null;

        var normalized = ZaloBotIntelligence.Normalize(raw);
        var intent = ZaloBotIntelligence.ClassifyDeterministically(raw).Intent;
        var promotedContent = raw;

        if (intent == ZaloBotIntent.Unknown && NaturalDraftPattern.IsMatch(normalized))
        {
            intent = ZaloBotIntent.AutoDraft;
            promotedContent = $"auto draft {raw}";
        }

        return intent is ZaloBotIntent.AutoDraft or
            ZaloBotIntent.Redraft or
            ZaloBotIntent.RebalanceTeams
            ? new ZaloAmbientLeaseActionPromotion(intent, promotedContent)
            : null;
    }
}
