using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// A structured, read-only interpretation of a conversational domain turn.
/// It carries references forward but never grants authority to mutate state.
/// Existing deterministic validators remain the only path to real changes.
/// </summary>
internal sealed record ZaloSemanticConversationPlan(
    ZaloAmbientDomainIntentKind Kind,
    double Confidence,
    string ActorSenderId,
    string? ReferencedMemberId,
    string? ReferencedMemberName,
    string? SourceMessageId,
    bool NeedsClarification,
    bool RequiresAuthoritativeValidation,
    string Reason)
{
    // NeedsClarification means the AI itself could not ground every reference. It does
    // not block the deterministic router: that router may still resolve a unique live
    // open offer from authoritative state. If it cannot, it fails closed as before.
    public bool CanEnterDeterministicRouter =>
        Kind != ZaloAmbientDomainIntentKind.None &&
        RequiresAuthoritativeValidation;
}

internal static class ZaloSemanticConversationPlanner
{
    public static ZaloSemanticConversationPlan Build(
        ZaloIncomingMessageEvent incoming,
        ZaloAmbientDomainIntentDecision decision)
    {
        var actor = Clean(incoming.SenderId, 100);
        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);

        string? referencedMemberId = null;
        string? referencedMemberName = null;
        string? sourceMessageId = null;
        var needsClarification = false;

        if (decision.Kind == ZaloAmbientDomainIntentKind.ClaimOpenSlot)
        {
            if (quote.HasQuote)
            {
                referencedMemberId = NullIfEmpty(Clean(quote.SenderId, 100));
                referencedMemberName = NullIfEmpty(Clean(quote.SenderName, 80));
                sourceMessageId = NullIfEmpty(Clean(quote.MessageId, 160));
            }
            else
            {
                // Do not invent an owner. The deterministic open-offer resolver gets
                // the chance to bind a unique live offer; otherwise it rejects safely.
                needsClarification = true;
            }
        }

        if (decision.Kind == ZaloAmbientDomainIntentKind.PassOwnSlot && actor.Length == 0)
            needsClarification = true;

        return new ZaloSemanticConversationPlan(
            decision.Kind,
            Math.Clamp(decision.Confidence, 0, 1),
            actor,
            referencedMemberId,
            referencedMemberName,
            sourceMessageId,
            needsClarification,
            RequiresAuthoritativeValidation: decision.Kind != ZaloAmbientDomainIntentKind.None,
            decision.Reason);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
