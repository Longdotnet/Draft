using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Transitional bridge from V2 quote semantics to legacy handlers that already
/// resolve structured Zalo mentions by UID. A metadata-only mention never removes
/// text because Len=0 and is added only for explicit deictic person references.
/// </summary>
public static class ZaloIncomingIdentityEnricher
{
    public static bool TryAddQuotedPersonMention(ZaloIncomingMessageEvent incoming)
    {
        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        var quotedSenderId = (quote.SenderId ?? string.Empty).Trim();
        var botId = (incoming.BotId ?? string.Empty).Trim();
        if (!quote.RefersToQuotedPerson || quotedSenderId.Length == 0 ||
            string.Equals(quotedSenderId, botId, StringComparison.Ordinal))
            return false;

        if (incoming.Mentions.Any(mention =>
                string.Equals((mention.Uid ?? string.Empty).Trim(), quotedSenderId, StringComparison.Ordinal)))
            return false;

        // ZaloIncomingMessageEvent normalizes constructor input with ToList(), so
        // production/deserialized events have a mutable backing list behind this
        // IReadOnlyList contract. Fail closed for any custom implementation.
        if (incoming.Mentions is not List<ZaloBridgeMention> mentions) return false;
        mentions.Add(new ZaloBridgeMention(quotedSenderId, -1, 0));
        return true;
    }
}
