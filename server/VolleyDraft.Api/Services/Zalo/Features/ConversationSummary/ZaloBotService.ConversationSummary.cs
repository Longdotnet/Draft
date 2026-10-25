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
        var semanticProbe = deterministic.Intent is
            ZaloBotIntent.GeneralChat or
            ZaloBotIntent.Unknown or
            ZaloBotIntent.SummarizeConversation or
            ZaloBotIntent.SummarizeMemberConversation or
            ZaloBotIntent.SummarizeHotTopics or
            ZaloBotIntent.ConversationSummaryFollowUp;
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
