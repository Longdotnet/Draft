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
