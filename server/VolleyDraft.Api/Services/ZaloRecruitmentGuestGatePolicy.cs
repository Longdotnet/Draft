using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

internal enum ZaloRecruitmentGuestReplyAnchorKind
{
    None,
    RecruitmentBroadcast,
    GuestConversation
}

internal static class ZaloRecruitmentGuestGatePolicy
{
    internal const string GuestSelectedIntentPrefix = "RecruitmentGuest:";
    internal const string GuestConversationReplyOutcome = "guest_recruitment_reply";

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

    internal static bool CanHandleFromAnchor(
        ZaloRecruitmentGuestCommandKind commandKind,
        ZaloRecruitmentGuestReplyAnchorKind anchorKind) =>
        commandKind switch
        {
            // Adding outside guests is deliberately stricter: the member must reply to
            // the group-wide recruitment message where the bot offered the shortcut.
            ZaloRecruitmentGuestCommandKind.Add =>
                anchorKind == ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast,

            // Once a guest exists, profile/cancel follow-ups may reply to either the
            // original recruitment message or a bot reply in that guest conversation.
            ZaloRecruitmentGuestCommandKind.Cancel or ZaloRecruitmentGuestCommandKind.UpdateProfile =>
                anchorKind is ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast or
                              ZaloRecruitmentGuestReplyAnchorKind.GuestConversation,
            _ => false
        };
}
