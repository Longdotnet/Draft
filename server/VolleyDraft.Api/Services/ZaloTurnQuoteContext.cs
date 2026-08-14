using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Async-flow-scoped quote context for one inbound webhook turn. This avoids changing
/// every AI context constructor while still letting both classification and answer
/// assembly see the same first-class reply relation. Values are bound to the current
/// sender UID and are explicitly cleared/replaced on every observed inbound message.
/// </summary>
public static class ZaloTurnQuoteContext
{
    private sealed record State(string SenderZaloUserId, ZaloQuotedSemanticContext? Quote);
    private static readonly AsyncLocal<State?> CurrentState = new();

    public static void Set(ZaloIncomingMessageEvent incoming)
    {
        var senderId = Clean(incoming.SenderId);
        var quote = incoming.Quote is null ? null : ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        CurrentState.Value = new State(senderId, quote);

        // During incremental migration, existing member-targeting handlers already
        // understand structured mention UIDs. Project only an explicit deictic quote
        // ("ông này", "anh đó"...) into that safe UID path without changing text.
        ZaloIncomingIdentityEnricher.TryAddQuotedPersonMention(incoming);
    }

    public static ZaloQuotedSemanticContext? GetFor(ZaloAiSender sender)
    {
        var state = CurrentState.Value;
        if (state is null || string.IsNullOrWhiteSpace(sender.Id)) return null;
        return string.Equals(state.SenderZaloUserId, sender.Id.Trim(), StringComparison.Ordinal)
            ? state.Quote
            : null;
    }

    public static void Clear() => CurrentState.Value = null;

    private static string Clean(string? value) => (value ?? string.Empty).Trim();
}
