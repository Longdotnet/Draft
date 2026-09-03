using System.Text.RegularExpressions;
using VolleyDraft.Api.Services.Zalo.Conversation;

namespace VolleyDraft.Api.Services.Zalo.Features.PassSlot;

internal enum ZaloPassSlotHistoryScope
{
    EventToday,
    SessionToday,
    CurrentOpen,
    SessionCurrentOpen,
    SpecificSession
}

/// <summary>
/// Owns only natural-language semantics for the PassSlot feature.
/// Persistence, offer lifecycle and rendering stay outside this policy.
/// </summary>
internal static class ZaloPassSlotQueryPolicy
{
    private static readonly Regex SummaryQuestionPattern = new(
        @"(?:bao\s+nhieu|may\s+nguoi|co\s+may|co\s+bao\s+nhieu|danh\s+sach|list).{0,45}(?:pass|nhuong)|(?:pass|nhuong).{0,45}(?:bao\s+nhieu|may\s+nguoi|co\s+may|danh\s+sach|list)|(?<![a-z0-9])ai\s+(?:dang\s+)?(?:pass|nhuong)(?![a-z0-9])|(?:pass|nhuong)\s+(?:(?:slot|suat|keo)\s+)?(?:la\s+)?ai(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CurrentOpenPattern = new(
        @"(?:slot|suat|keo).{0,35}(?:dang\s+mo|con\s+mo|chua\s+ai\s+nhan|chua\s+co\s+nguoi\s+nhan|can\s+nguoi|ai\s+hot)|(?:con|dang).{0,25}(?:slot|suat).{0,25}(?:pass|mo)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SessionTodayPattern = new(
        @"(?:keo|tran|buoi|session|san)\s+(?:hom\s+nay|hnay)|(?:hom\s+nay|hnay)\s+(?:co\s+)?(?:keo|tran|buoi|session|san)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TodayPattern = new(
        @"(?<![a-z0-9])(?:hom\s+nay|hnay|today)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HistoryCuePattern = new(
        @"(?<![a-z0-9])(?:lich\s+su|tung|truoc\s+do|truoc\s+day|da\s+pass|da\s+nhuong|het\s+han|da\s+hoan\s+tat|da\s+chuyen)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool LooksLikeQuery(string? content)
    {
        var normalized = ZaloTextNormalizer.Normalize(content);
        if (normalized.Length == 0)
            return false;

        return SummaryQuestionPattern.IsMatch(normalized) || CurrentOpenPattern.IsMatch(normalized);
    }

    public static ZaloPassSlotHistoryScope ResolveScope(string normalized)
    {
        normalized = ZaloTextNormalizer.Normalize(normalized);

        if (CurrentOpenPattern.IsMatch(normalized))
            return ZaloPassSlotHistoryScope.CurrentOpen;
        if (SessionTodayPattern.IsMatch(normalized))
            return ZaloPassSlotHistoryScope.SessionToday;
        if (TodayPattern.IsMatch(normalized))
            return ZaloPassSlotHistoryScope.EventToday;
        if (ZaloSessionResolver.LooksLikeSelector(normalized))
        {
            return HistoryCuePattern.IsMatch(normalized)
                ? ZaloPassSlotHistoryScope.SpecificSession
                : ZaloPassSlotHistoryScope.SessionCurrentOpen;
        }

        return ZaloPassSlotHistoryScope.CurrentOpen;
    }

    public static bool IsHistoryRequest(string? content) =>
        HistoryCuePattern.IsMatch(ZaloTextNormalizer.Normalize(content));
}
