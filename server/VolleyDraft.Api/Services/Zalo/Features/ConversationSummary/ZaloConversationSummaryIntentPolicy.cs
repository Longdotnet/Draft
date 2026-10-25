using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

internal static class ZaloConversationSummaryIntentPolicy
{
    public static bool MightBeQuestion(string value)
    {
        var q = ZaloBotIntelligence.Normalize(value);
        if (string.IsNullOrWhiteSpace(q)) return false;
        return HasAny(q,
            "tom tat", "tong ket", "recap", "summary", "nay gio", "nãy giờ",
            "sang gio", "sáng giờ", "tu sang", "hom nay", "hôm nay", "hom qua", "tuan nay",
            "co gi hot", "gi hot", "dang chu y", "noi bat", "drama", "ban gi", "noi gi", "chem gi", "tam gi",
            "noi ky", "noi ro", "chi tiet", "vu do", "vu nay", "phan do", "cai do", "cai vu", "vu so",
            "ai de xuat", "ai noi", "ai dong y", "ket luan sao", "chot sao");
    }

    public static ZaloBotIntent? TryGetDeterministicIntent(string value)
    {
        var q = ZaloBotIntelligence.Normalize(value);
        if (string.IsNullOrWhiteSpace(q)) return null;
        if (HasAny(q, "noi ky", "noi ro", "chi tiet", "vu do", "phan do", "cai do", "cai vu", "vu so"))
            return ZaloBotIntent.ConversationSummaryFollowUp;
        if (HasAny(q, "co gi hot", "gi hot", "dang chu y", "noi bat", "drama gi"))
            return ZaloBotIntent.SummarizeHotTopics;
        if (HasAny(q, "tom tat cuoc tro chuyen cua", "tom tat cua") ||
            Regex.IsMatch(q, @"\b(?:nay gio|sang gio|hom nay|hom qua|tuan nay)\b.{0,36}\b(?:noi gi|chem gi|tam gi)\b", RegexOptions.CultureInvariant))
            return ZaloBotIntent.SummarizeMemberConversation;
        if (HasAny(q, "tom tat", "tong ket", "recap") ||
            Regex.IsMatch(q, @"\b(?:nay gio|sang gio|tu sang|hom nay|hom qua|tuan nay)\b.{0,36}\b(?:ban gi|co gi|noi chuyen gi)\b", RegexOptions.CultureInvariant))
            return ZaloBotIntent.SummarizeConversation;
        return null;
    }

    public static bool LooksLikeFollowUp(string value)
    {
        var q = ZaloBotIntelligence.Normalize(value);
        if (string.IsNullOrWhiteSpace(q)) return false;
        return HasAny(q,
                   "noi ky", "noi ro", "chi tiet", "vu do", "vu nay", "phan do", "cai do", "cai vu", "vu so",
                   "ai de xuat", "ai noi", "ai dong y", "ai phan doi", "tai sao", "ket luan", "chot sao", "roi sao") ||
               Regex.IsMatch(q, @"\b(?:so|muc|phan|vu)\s*[1-9]\b", RegexOptions.CultureInvariant);
    }

    private static bool HasAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(ZaloBotIntelligence.Normalize(term), StringComparison.Ordinal));
}
