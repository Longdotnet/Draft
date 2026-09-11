using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Conversation;

public sealed class ZaloDelayedQuotedConversationOwnershipFuzzTests
{
    [Fact]
    public void Delayed_draft_choice_reply_keeps_exact_original_anchor_across_intervening_bot_noise()
    {
        var startedAt = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);

        for (var seed = 0; seed < 256; seed++)
        {
            var random = new Random(seed);
            var sourceMessageId = $"user-question-{seed}";
            var originalBotMessageId = $"bot-choice-{seed}";
            var delayMinutes = random.Next(30, 181);
            var requestedExpiry = startedAt.AddMinutes(15);
            var effectiveExpiry = ZaloConversationLifetimePolicy.NormalizeExpiry(
                ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                requestedExpiry,
                startedAt);

            Assert.True(effectiveExpiry > startedAt.AddMinutes(delayMinutes));

            var state = State(sourceMessageId, sourceMessageId, effectiveExpiry);
            var originalQuote = Quote(originalBotMessageId, startedAt.AddMinutes(delayMinutes));
            var originalRelation = Relation(originalBotMessageId, sourceMessageId, startedAt.AddMinutes(1));

            // Simulate 1..12 unrelated bot replies/messages arriving after the original
            // draft-choice prompt but before the member eventually answers it.
            var noiseCount = random.Next(1, 13);
            for (var index = 0; index < noiseCount; index++)
            {
                var noiseBotMessageId = $"bot-noise-{seed}-{index}";
                var noiseParent = $"user-noise-{seed}-{index}";
                var noiseQuote = Quote(noiseBotMessageId, startedAt.AddMinutes(random.Next(2, delayMinutes + 1)));
                var noiseRelation = Relation(noiseBotMessageId, noiseParent, startedAt.AddMinutes(index + 2));

                Assert.False(ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchor(
                    state,
                    noiseQuote,
                    noiseRelation));
            }

            Assert.True(ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchor(
                state,
                originalQuote,
                originalRelation));
        }
    }

    [Fact]
    public void Delayed_reply_never_rebinds_to_newer_unrelated_bot_message_or_other_parent()
    {
        var startedAt = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);

        for (var seed = 0; seed < 256; seed++)
        {
            var random = new Random(unchecked(seed * 7919 + 17));
            var sourceMessageId = $"user-question-{seed}";
            var originalBotMessageId = $"bot-choice-{seed}";
            var newerBotMessageId = $"bot-latest-{seed}";
            var effectiveExpiry = ZaloConversationLifetimePolicy.NormalizeExpiry(
                ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                startedAt.AddMinutes(15),
                startedAt);
            var state = State(sourceMessageId, sourceMessageId, effectiveExpiry);

            var originalQuote = Quote(originalBotMessageId, startedAt.AddMinutes(random.Next(30, 181)));
            var wrongLatestRelation = Relation(originalBotMessageId, $"user-latest-{seed}", startedAt.AddMinutes(20));
            var newerQuote = Quote(newerBotMessageId, startedAt.AddMinutes(random.Next(30, 181)));
            var newerRelation = Relation(newerBotMessageId, $"user-latest-{seed}", startedAt.AddMinutes(20));

            Assert.False(ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchor(
                state,
                originalQuote,
                wrongLatestRelation));
            Assert.False(ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchor(
                state,
                newerQuote,
                newerRelation));
        }
    }

    private static ZaloConversationStateV2Snapshot State(
        string sourceMessageId,
        string lastMessageId,
        DateTimeOffset expiresAt) =>
        new(
            "state-fuzz",
            "group-1",
            "user-1",
            ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
            "{}",
            "[]",
            "[\"session-1\"]",
            sourceMessageId,
            lastMessageId,
            1,
            ZaloConversationStateV2Status.Active,
            expiresAt,
            expiresAt.AddHours(-4),
            expiresAt.AddHours(-4));

    private static ZaloQuotedSemanticContext Quote(string messageId, DateTimeOffset receivedAt) =>
        new(
            messageId,
            "bot-1",
            "Npc",
            "Ông hỏi đội hình trận nào?",
            "text",
            receivedAt,
            true,
            false,
            true);

    private static ZaloMessageGraphRelation Relation(
        string fromMessageId,
        string toMessageId,
        DateTimeOffset observedAt) =>
        new(
            $"relation:{fromMessageId}",
            "connection-1",
            "group-1",
            fromMessageId,
            toMessageId,
            "BotReply",
            null,
            null,
            null,
            fromMessageId,
            observedAt);
}
