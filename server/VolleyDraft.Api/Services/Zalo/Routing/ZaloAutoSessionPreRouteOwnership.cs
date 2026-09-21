using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Decides whether an explicitly addressed turn belongs to the normal deterministic
/// bot lanes before Auto Session conversation V3 gets a chance to interpret it.
/// Auto Session is allowed to own its natural draft-edit/create dialogue, but it must
/// not steal a canonical bot command or a continuation of an already persisted legacy
/// bot workflow merely because an Auto Session poll conversation is also active.
/// </summary>
internal static class ZaloAutoSessionPreRouteOwnership
{
    internal static bool ShouldBypassAutoSession(
        ZaloIncomingMessageEvent incoming,
        bool hasActiveLegacyPending)
    {
        // Draft reminders deliberately teach organizers that bare `draft đi` is enough.
        // Give that narrow destructive-domain confirmation to the draft lane before an
        // unrelated active Auto Session conversation can interpret it as a poll-draft turn.
        if (ZaloDraftConversationPolicy.IsStrongDraftConfirmation(incoming.Content))
            return true;

        if (!incoming.MentionedBot) return false;
        if (hasActiveLegacyPending) return true;

        var question = ZaloBotService.ExtractQuestion(incoming);
        var decision = ZaloBotIntelligence.ClassifyDeterministically(question);
        return decision.Intent != ZaloBotIntent.Unknown;
    }
}
