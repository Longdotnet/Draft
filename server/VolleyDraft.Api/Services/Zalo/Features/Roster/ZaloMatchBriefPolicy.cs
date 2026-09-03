using System.Text.RegularExpressions;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Owns intent recognition for the read-only Match Brief feature. This policy is
/// deliberately separate from Draft so status questions cannot silently expand the
/// Draft readiness grammar or steal its approval/escalation conversation.
/// </summary>
internal static class ZaloMatchBriefPolicy
{
    private static readonly Regex MatchSubject = new(
        @"(?<![a-z0-9])(?:tinh\s*hinh|keo|tran|roster|slot|doi\s*hinh|team|draft)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SessionSubject = new(
        @"(?<![a-z0-9])(?:t[2-7]|cn|thu\s*(?:[2-7]|hai|ba|tu|nam|sau|bay)|chu\s*nhat|\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StatusQuestion = new(
        @"\?|(?<![a-z0-9])(?:tinh\s*hinh|sao\s*roi|dang\s*sao|the\s*nao|on\s*khong|toi\s*dau|status|cap\s*nhat|update|can\s*lam\s*gi|co\s*can)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WebQuestion = new(
        @"(?<![a-z0-9])(?:(?:co\s*)?can\s*(?:vao|mo)\s*(?:web|website)|(?:co\s*)?can\s*(?:web|website)|(?:vao|mo)\s*(?:web|website)\s*(?:khong|ko|k))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool IsQuestion(string? content)
    {
        var normalized = Normalize(content);
        if (normalized.Length == 0) return false;
        if (WebQuestion.IsMatch(normalized)) return true;

        if (ZaloDraftConversationPolicy.IsReadinessQuestion(content))
            return false;

        return (MatchSubject.IsMatch(normalized) || SessionSubject.IsMatch(normalized)) &&
               StatusQuestion.IsMatch(normalized);
    }

    internal static bool IsExplicitlyAddressed(ZaloIncomingMessageEvent incoming)
    {
        if (incoming.MentionedBot) return true;
        if (ZaloDraftConversationPolicy.ExplicitlyAddressesBot(incoming.Content)) return true;
        return ZaloQuotedContextResolver.Resolve(incoming, incoming.Content).RepliesToBot;
    }

    internal static string Normalize(string? content) =>
        ZaloBotIntelligence.Normalize(content ?? string.Empty).Trim();
}
