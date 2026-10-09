namespace VolleyDraft.Api.Services.Zalo.Features.Reminder;

/// <summary>Formats reminder intervals without growing the legacy bot orchestrator.</summary>
internal static class ZaloReminderDurationText
{
    public static string Format(int minutes)
    {
        if (minutes % 60 == 0) return $"{minutes / 60} giờ";
        if (minutes < 60) return $"{minutes} phút";
        return $"{minutes / 60} giờ {minutes % 60} phút";
    }
}
