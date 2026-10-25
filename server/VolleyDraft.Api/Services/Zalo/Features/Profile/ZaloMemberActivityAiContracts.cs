namespace VolleyDraft.Api.Services;

public enum ZaloActivityTimeRangeKind
{
    PreviousDays,
    PreviousCalendarMonths,
    ThisWeek,
    ThisMonth,
    ThisYear,
    SinceMonth,
    ExplicitRange,
    Recent
}

public sealed record ZaloActivityTimeRangeExtraction(
    ZaloActivityTimeRangeKind Kind,
    int? Amount,
    DateOnly? StartDate,
    DateOnly? EndDate);

public sealed record ZaloMemberActivityClassification(
    ZaloBotIntent Intent,
    double Confidence,
    string? MemberReference,
    ZaloActivityTimeRangeExtraction? TimeRange,
    int? Limit,
    bool NeedsClarification);

public sealed record ZaloMemberActivityClassifierContext(
    string Question,
    string SenderZaloUserId,
    string SenderName,
    DateTimeOffset CurrentVietnamTime);
