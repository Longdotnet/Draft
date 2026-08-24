using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

public static class ZaloDraftConversationPolicy
{
    private static readonly Regex ReadinessSubject = new(
        @"(?<![a-z0-9])(?:doi\s*hinh|team|chia\s*(?:team|doi)|draft)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReadinessQuestion = new(
        @"\?|(?<![a-z0-9])(?:khi\s*nao|bao\s*gio|dau\s*roi|dau|co\s*chua|chua\s*co|xong\s*chua|chua\s*xong|sao\s*chua|chua\s*(?:chia|draft)|sap\s*(?:danh|choi)|may\s*gio)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MatchBriefSubject = new(
        @"(?<![a-z0-9])(?:tinh\s*hinh|keo|tran|roster|slot|doi\s*hinh|team|draft)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MatchBriefSessionSubject = new(
        @"(?<![a-z0-9])(?:t[2-7]|cn|thu\s*(?:[2-7]|hai|ba|tu|nam|sau|bay)|chu\s*nhat|\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MatchBriefQuestion = new(
        @"\?|(?<![a-z0-9])(?:tinh\s*hinh|sao\s*roi|dang\s*sao|the\s*nao|on\s*khong|toi\s*dau|status|cap\s*nhat|update|can\s*lam\s*gi|co\s*can)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MatchBriefWebQuestion = new(
        @"(?<![a-z0-9])(?:(?:co\s*)?can\s*(?:vao|mo)\s*(?:web|website)|(?:co\s*)?can\s*(?:web|website)|(?:vao|mo)\s*(?:web|website)\s*(?:khong|ko|k))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StrongConfirmation = new(
        @"(?<![a-z0-9])(?:draft\s*(?:di|luon|nha)|chay\s*draft|trien\s*draft|xac\s*nhan\s*draft|chia\s*(?:team|doi)\s*(?:di|luon|nha)|chot\s*team(?:\s*(?:di|luon|nha))?)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EscalationConsent = new(
        @"(?<![a-z0-9])(?:(?:goi|tag|keu|nhan|nho)\b.*(?:di|luon|giup|truong|pho)|(?:u|uh|oke?|duoc|yes)\b.*(?:goi|tag|keu)|(?:goi|tag|keu)\s*(?:truong|pho|admin))(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CancelEscalation = new(
        @"(?<![a-z0-9])(?:khoi\s*(?:tag|goi|keu)|khong\s*can\s*(?:tag|goi|keu)|dung\s*(?:tag|goi|keu)|huy(?:\s*yeu\s*cau)?|thoi\s*khoi)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WeakConfirmation = new(
        @"^(?:ok|oke|okay|u|uh|uhm|um|duoc|dong\s*y|yes|yep|chot|👍|👌|✅)[\s.!]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitBotAddress = new(
        @"^\s*(?:@?[a-z0-9._-]*bot|npc|volley\s*bot)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsReadinessQuestion(string? content)
    {
        var normalized = Normalize(content);
        return normalized.Length > 0 &&
               ReadinessSubject.IsMatch(normalized) &&
               ReadinessQuestion.IsMatch(normalized);
    }

    public static bool IsMatchBriefQuestion(string? content)
    {
        var normalized = Normalize(content);
        if (normalized.Length == 0) return false;
        var hasMatchSubject = MatchBriefSubject.IsMatch(normalized) || MatchBriefSessionSubject.IsMatch(normalized);
        return MatchBriefWebQuestion.IsMatch(normalized) ||
               (hasMatchSubject && MatchBriefQuestion.IsMatch(normalized));
    }

    public static bool IsStrongDraftConfirmation(string? content) =>
        StrongConfirmation.IsMatch(Normalize(content));

    public static bool IsEscalationConsent(string? content) =>
        EscalationConsent.IsMatch(Normalize(content));

    public static bool IsEscalationCancel(string? content) =>
        CancelEscalation.IsMatch(Normalize(content));

    public static bool IsWeakConfirmation(string? content) =>
        WeakConfirmation.IsMatch(Normalize(content));

    public static bool ExplicitlyAddressesBot(string? content) =>
        ExplicitBotAddress.IsMatch(Normalize(content));

    public static string Normalize(string? content) =>
        ZaloBotIntelligence.Normalize(content ?? string.Empty).Trim();
}
