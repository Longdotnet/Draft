using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed partial class ZaloMemberIntelligenceBotService
{
    public async Task<ZaloMemberBotAnswer?> TryHandleConversationSummaryAsync(
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        string question,
        bool aiAllowed,
        CancellationToken cancellationToken)
    {
        var summaryService = new ZaloConversationSummaryService(db, ai);
        var now = ResolveSummaryIncomingTime(incoming);
        var memory = await summaryService.LoadMemoryAsync(connectionId, groupId, incoming.SenderId, now, cancellationToken);
        var deterministic = ZaloConversationSummaryIntentPolicy.TryGetDeterministicIntent(question);
        var plausible = ZaloConversationSummaryIntentPolicy.MightBeQuestion(question);
        var followUpPlausible = memory is not null && ZaloConversationSummaryIntentPolicy.LooksLikeFollowUp(question);
        if (deterministic is null && !plausible && !followUpPlausible && !aiAllowed) return null;

        ZaloConversationSummaryClassification? classification = null;
        var aiCalled = false;
        if (aiAllowed && ai.IsConfigured)
        {
            classification = await ai.ClassifyConversationSummaryAsync(
                new ZaloConversationSummaryClassifierContext(
                    question,
                    NormalizeId(incoming.SenderId),
                    incoming.SenderName,
                    now.ToOffset(VietnamOffset),
                    memory?.Topics.Select(item => item.Title).ToList() ?? []),
                cancellationToken);
            aiCalled = true;
            if (classification is { Kind: ZaloConversationSummaryKind.None } || classification is { Confidence: < .64 })
                classification = null;
        }

        classification ??= BuildDeterministicSummaryClassification(deterministic, question, memory is not null);
        if (classification is null) return null;

        if (classification.Kind == ZaloConversationSummaryKind.FollowUpDetail)
        {
            if (memory is null) return null;
            string? followUpTargetId = null;
            string? followUpTargetName = null;
            var hasHumanMention = incoming.Mentions.Any(mention => NormalizeId(mention.Uid) != NormalizeId(incoming.BotId));
            if (hasHumanMention || !string.IsNullOrWhiteSpace(classification.MemberReference))
            {
                var resolved = await summaryService.ResolveTargetMemberAsync(
                    connectionId,
                    groupId,
                    incoming,
                    classification.MemberReference,
                    cancellationToken);
                if (resolved.Error is not null)
                    return new ZaloMemberBotAnswer(resolved.Error, ZaloBotIntent.ConversationSummaryFollowUp, aiCalled);
                followUpTargetId = resolved.UserId;
                followUpTargetName = resolved.DisplayName;
            }
            var followUp = await summaryService.AnswerFollowUpAsync(
                connectionId,
                groupId,
                incoming,
                question,
                classification,
                memory,
                followUpTargetId,
                followUpTargetName,
                aiAllowed,
                cancellationToken);
            return followUp is null
                ? null
                : new ZaloMemberBotAnswer(followUp.Text, followUp.Intent, aiCalled || followUp.AiCalled, followUp.ProtectedTerms);
        }

        string? targetUserId = null;
        string? targetDisplayName = null;
        if (classification.Kind == ZaloConversationSummaryKind.MemberSummary)
        {
            var resolved = await summaryService.ResolveTargetMemberAsync(
                connectionId,
                groupId,
                incoming,
                classification.MemberReference,
                cancellationToken);
            if (resolved.Error is not null)
                return new ZaloMemberBotAnswer(resolved.Error, ZaloBotIntent.SummarizeMemberConversation, aiCalled);
            targetUserId = resolved.UserId;
            targetDisplayName = resolved.DisplayName;
        }

        var result = await summaryService.SummarizeAsync(
            connectionId,
            groupId,
            incoming,
            question,
            classification,
            targetUserId,
            targetDisplayName,
            aiAllowed,
            cancellationToken);
        return new ZaloMemberBotAnswer(result.Text, result.Intent, aiCalled || result.AiCalled, result.ProtectedTerms);
    }

    private static ZaloConversationSummaryClassification? BuildDeterministicSummaryClassification(
        ZaloBotIntent? deterministic,
        string question,
        bool hasMemory)
    {
        if (deterministic is null) return null;
        var kind = deterministic.Value switch
        {
            ZaloBotIntent.SummarizeMemberConversation => ZaloConversationSummaryKind.MemberSummary,
            ZaloBotIntent.SummarizeHotTopics => ZaloConversationSummaryKind.HotTopics,
            ZaloBotIntent.ConversationSummaryFollowUp when hasMemory => ZaloConversationSummaryKind.FollowUpDetail,
            ZaloBotIntent.SummarizeConversation => ZaloConversationSummaryKind.GroupSummary,
            _ => ZaloConversationSummaryKind.None
        };
        if (kind == ZaloConversationSummaryKind.None) return null;
        var normalized = ZaloBotIntelligence.Normalize(question);
        var scope = normalized.Contains("hom qua", StringComparison.Ordinal)
            ? ZaloConversationSummaryTimeScope.Yesterday
            : normalized.Contains("tuan nay", StringComparison.Ordinal)
                ? ZaloConversationSummaryTimeScope.ThisWeek
                : normalized.Contains("sang gio", StringComparison.Ordinal) || normalized.Contains("tu sang", StringComparison.Ordinal)
                    ? ZaloConversationSummaryTimeScope.SinceMorning
                    : normalized.Contains("hom nay", StringComparison.Ordinal)
                        ? ZaloConversationSummaryTimeScope.Today
                        : ZaloConversationSummaryTimeScope.CurrentConversation;
        int? ordinal = null;
        var ordinalMatch = System.Text.RegularExpressions.Regex.Match(normalized, @"\b(?:so|muc|phan|vu)\s*([1-9])\b");
        if (ordinalMatch.Success && int.TryParse(ordinalMatch.Groups[1].Value, out var parsed)) ordinal = parsed;
        return new ZaloConversationSummaryClassification(kind, .8, scope, null, null, ordinal);
    }

    private static DateTimeOffset ResolveSummaryIncomingTime(ZaloIncomingMessageEvent incoming)
    {
        try
        {
            return incoming.SentAtUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(incoming.SentAtUnixMs) : DateTimeOffset.UtcNow;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
