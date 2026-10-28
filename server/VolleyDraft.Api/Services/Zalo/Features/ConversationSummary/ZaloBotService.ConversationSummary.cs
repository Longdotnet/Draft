using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloBotService
{
    private async Task<BotAnswer?> TryBuildConversationSummaryAnswerAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string question,
        CancellationToken cancellationToken)
    {
        var deterministic = ZaloBotIntelligence.ClassifyDeterministically(question);
        // Do not spend a free-model classifier call on every ordinary GeneralChat/Unknown
        // turn. This is only a candidate gate; natural/slang summary wording still goes to
        // the semantic classifier via MightBeQuestion/LooksLikeFollowUp below.
        var semanticProbe = ZaloConversationSummaryIntentPolicy.ShouldProbeSemantically(
            deterministic.Intent,
            question);
        var aiAllowed = semanticProbe &&
                        ai.IsConfigured &&
                        await IsAiCallAllowedAsync(connectionId, groupId, incoming.SenderId, cancellationToken);
        var answer = await memberIntelligence.TryHandleConversationSummaryAsync(
            connectionId,
            groupId,
            incoming,
            question,
            aiAllowed,
            cancellationToken);
        return answer is null
            ? null
            : new BotAnswer(answer.Text, null, answer.Intent, answer.AiCalled, ProtectedTerms: answer.ProtectedTerms);
    }
}
