using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloOverbookService
{
    /// <summary>
    /// Priority mutation lane used by the ingress coordinator before read-only/status
    /// pre-routing. Keeping this explicit prevents a phrase such as "cập nhật ... T6"
    /// from being consumed as Match Brief while preserving all normal AI/chat routes.
    /// </summary>
    public Task<bool> TryHandleZaloProfileUpdatePreRouteAsync(
        ZaloIncomingMessageEvent incoming,
        CancellationToken cancellationToken = default)
    {
        // Deterministic classification should see the user's sentence, not the leading
        // structured @Npc token. Preserve person UIDs and remap their visible offsets so
        // commands such as "@Npc cập nhật @Hiệp ..." work even without the word "hồ sơ".
        var question = ZaloBotService.ExtractQuestion(incoming);
        if (string.Equals(question, incoming.Content, StringComparison.Ordinal))
            return TryHandleProfileUpdateConversationAsync(incoming, cancellationToken);

        var remappedMentions = incoming.Mentions
            .Select(mention =>
            {
                var mentionUid = NormalizeProfileConversationId(mention.Uid);
                if (string.Equals(mentionUid, NormalizeProfileConversationId(incoming.BotId), StringComparison.Ordinal))
                    return new ZaloBridgeMention(mentionUid, -1, 0);
                if (mention.Pos < 0 || mention.Len <= 0 || mention.Pos + mention.Len > incoming.Content.Length)
                    return new ZaloBridgeMention(mentionUid, -1, 0);

                var label = incoming.Content.Substring(mention.Pos, mention.Len);
                var newPos = question.IndexOf(label, StringComparison.Ordinal);
                return new ZaloBridgeMention(
                    mentionUid,
                    newPos,
                    newPos >= 0 ? label.Length : 0);
            })
            .ToList();
        var normalized = incoming with
        {
            Content = question,
            Mentions = remappedMentions
        };
        return TryHandleProfileUpdateConversationAsync(normalized, cancellationToken);
    }
}
