using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal enum ZaloRecruitmentGuestReplyAnchorKind
{
    None,
    RecruitmentBroadcast,
    GuestSignupGuidance,
    GuestConversation
}

internal static class ZaloRecruitmentGuestGatePolicy
{
    internal const string GuestSelectedIntentPrefix = "RecruitmentGuest:";
    internal const string GuestConversationReplyOutcome = "guest_recruitment_reply";
    internal const string GuestSignupGuidanceReplyOutcome = "guest_signup_guidance";

    internal static TimeSpan GetSignupWindow(IConfiguration configuration) =>
        TimeSpan.FromHours(Math.Clamp(
            configuration.GetValue("ZaloBot:DraftAutopilot:GuestSignupHoursBeforeStart", 2),
            1,
            6));

    internal static bool IsAddWindowOpen(
        DateTimeOffset? startTime,
        DateTimeOffset now,
        IConfiguration configuration)
    {
        if (startTime is null || startTime <= now) return false;
        return now >= startTime.Value - GetSignupWindow(configuration);
    }

    internal static DateTimeOffset? GetAddWindowOpensAt(
        DateTimeOffset? startTime,
        IConfiguration configuration) =>
        startTime is null ? null : startTime.Value - GetSignupWindow(configuration);

    internal static string GuestSelectedIntent(string sessionId) =>
        $"{GuestSelectedIntentPrefix}{sessionId}";

    internal static string? TryReadGuestSessionId(string? selectedIntent)
    {
        if (string.IsNullOrWhiteSpace(selectedIntent) ||
            !selectedIntent.StartsWith(GuestSelectedIntentPrefix, StringComparison.Ordinal))
            return null;
        var value = selectedIntent[GuestSelectedIntentPrefix.Length..].Trim();
        return value.Length == 0 ? null : value;
    }

    internal static bool LooksLikeSignupGuidance(string? content)
    {
        var normalized = ZaloBotIntelligence.Normalize(content ?? string.Empty);
        if (normalized.Length == 0) return false;
        return normalized.Contains("ban ngoai group", StringComparison.Ordinal) &&
               normalized.Contains("cong slot", StringComparison.Ordinal) &&
               (normalized.Contains("reply dung tin", StringComparison.Ordinal) ||
                normalized.Contains("reply ngay tin nay", StringComparison.Ordinal));
    }

    internal static bool CanHandleFromAnchor(
        ZaloRecruitmentGuestCommandKind commandKind,
        ZaloRecruitmentGuestReplyAnchorKind anchorKind) =>
        commandKind switch
        {
            // Adding outside guests must stay on a grounded mutation lane. The user may
            // reply either to the original recruitment broadcast or to a bot guidance
            // message that has already grounded one exact session.
            ZaloRecruitmentGuestCommandKind.Add =>
                anchorKind is ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast or
                              ZaloRecruitmentGuestReplyAnchorKind.GuestSignupGuidance,

            // Once a guest exists, profile/cancel follow-ups may reply to either the
            // original recruitment message or a bot reply in that guest conversation.
            ZaloRecruitmentGuestCommandKind.Cancel or ZaloRecruitmentGuestCommandKind.UpdateProfile =>
                anchorKind is ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast or
                              ZaloRecruitmentGuestReplyAnchorKind.GuestConversation,
            _ => false
        };
}
