using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Builds a sender-aware conversational window before any chat history is sent to an
/// AI provider. The database remains the source of truth; this class only decides
/// which untrusted conversation turns are useful context.
///
/// The assembler deliberately reads a wider window than it emits. This lets short
/// referential questions such as "slot đó thì sao?", "vậy còn Nam?" or "cái đó được
/// không?" recover the relevant local thread without blindly sending the whole group
/// history to the model.
/// </summary>
public static class ZaloConversationContextAssembler
{
    private const int DefaultMaxMessages = 16;
    private const int ImmediateTailSize = 6;
    private const int ReferenceChainRadius = 2;

    private static readonly HashSet<string> ReferentialTokens = new(StringComparer.Ordinal)
    {
        "do", "nay", "kia", "vay", "con", "nguoi", "ong", "ba", "ban",
        "slot", "suat", "cho", "team", "doi", "tran", "buoi", "lich"
    };

    public static IReadOnlyList<ZaloAiMessage> Assemble(
        ZaloAiSender sender,
        string question,
        IReadOnlyList<ZaloAiMessage> messages,
        int maxMessages = DefaultMaxMessages)
    {
        maxMessages = Math.Clamp(maxMessages, 1, 24);
        var working = messages.ToList();
        var quote = ZaloTurnQuoteContext.GetFor(sender);
        if (quote is { HasQuote: true })
        {
            working.Add(new ZaloAiMessage(
                "context",
                quote.SenderId ?? string.Empty,
                quote.SenderName ?? "Quoted Zalo member",
                "[UNTRUSTED_ZALO_QUOTE] " + ZaloQuotedContextResolver.BuildAiGrounding(quote),
                quote.SentAt ?? DateTimeOffset.UtcNow));
        }

        if (working.Count == 0) return [];
        if (working.Count <= maxMessages) return working;

        var senderId = NormalizeId(sender.Id);
        var senderName = sender.Name?.Trim() ?? string.Empty;
        var questionTokens = SignificantTokens(question);
        var normalizedQuestion = ZaloBotIntelligence.Normalize(question ?? string.Empty);
        var referentialQuestion = LooksReferential(normalizedQuestion);
        var mentionedNames = MentionedParticipantNames(normalizedQuestion, working);
        var senderIndexes = working
            .Select((message, index) => new { message, index })
            .Where(item => string.Equals(NormalizeId(item.message.SenderId), senderId, StringComparison.Ordinal))
            .Select(item => item.index)
            .ToHashSet();

        // Always retain a local tail. Pronouns and shorthand almost always resolve to
        // this local thread, so widening the tail from four to six makes the classifier
        // materially better at multi-speaker exchanges while keeping the token budget
        // bounded.
        var tailCount = Math.Min(ImmediateTailSize, Math.Max(1, maxMessages / 2));
        var tailStart = Math.Max(0, working.Count - tailCount);
        var selectedIndexes = Enumerable.Range(tailStart, working.Count - tailStart).ToHashSet();

        // If the current question is referential, keep a small chain around the most
        // recent turn from this sender. This preserves "A asks -> B answers -> A says
        // vậy còn tui?" even when unrelated group chatter sits nearby.
        if (referentialQuestion)
        {
            var lastSenderIndex = senderIndexes.Where(index => index < working.Count).DefaultIfEmpty(-1).Max();
            if (lastSenderIndex >= 0)
            {
                for (var index = Math.Max(0, lastSenderIndex - ReferenceChainRadius);
                     index <= Math.Min(working.Count - 1, lastSenderIndex + ReferenceChainRadius);
                     index++)
                    selectedIndexes.Add(index);
            }
        }

        // Never let deterministic preselection exceed the requested budget. Keep the
        // newest items because they are the strongest source for local references.
        if (selectedIndexes.Count > maxMessages)
        {
            selectedIndexes = selectedIndexes
                .OrderByDescending(index => index)
                .Take(maxMessages)
                .ToHashSet();
        }

        var semanticSlots = Math.Max(0, maxMessages - selectedIndexes.Count);
        var semanticIndexes = working
            .Select((message, index) => new
            {
                Index = index,
                Score = Score(
                    message,
                    index,
                    working.Count,
                    senderId,
                    senderName,
                    senderIndexes,
                    questionTokens,
                    referentialQuestion,
                    mentionedNames)
            })
            .Where(item => !selectedIndexes.Contains(item.Index))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Index)
            .Take(semanticSlots)
            .Select(item => item.Index);

        selectedIndexes.UnionWith(semanticIndexes);
        return selectedIndexes
            .OrderBy(index => index)
            .Select(index => working[index])
            .ToList();
    }

    private static int Score(
        ZaloAiMessage message,
        int index,
        int messageCount,
        string senderId,
        string senderName,
        HashSet<int> senderIndexes,
        HashSet<string> questionTokens,
        bool referentialQuestion,
        HashSet<string> mentionedNames)
    {
        var score = index; // deterministic recency tie-breaker
        var normalizedMessageSender = NormalizeId(message.SenderId);
        var normalizedContent = ZaloBotIntelligence.Normalize(message.Content ?? string.Empty);
        var normalizedMessageName = ZaloBotIntelligence.Normalize(message.SenderName ?? string.Empty);

        if (string.Equals(normalizedMessageSender, senderId, StringComparison.Ordinal))
            score += 1_000;

        if (message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
            IsAddressedToSender(message.Content, senderName))
            score += 800;

        if (message.Role.Equals("context", StringComparison.OrdinalIgnoreCase) &&
            message.Content.StartsWith("[UNTRUSTED_ZALO_QUOTE]", StringComparison.Ordinal))
            score += 2_500;

        if (senderIndexes.Contains(index - 1) || senderIndexes.Contains(index + 1))
            score += 550;

        if (referentialQuestion &&
            (senderIndexes.Contains(index - 2) || senderIndexes.Contains(index + 2)))
            score += 260;

        if (index >= messageCount - ImmediateTailSize)
            score += 350;

        var overlap = SignificantTokens(message.Content).Count(questionTokens.Contains);
        score += Math.Min(overlap, 6) * 100;

        if (mentionedNames.Count > 0 &&
            mentionedNames.Any(name => normalizedMessageName.Contains(name, StringComparison.Ordinal) ||
                                       normalizedContent.Contains(name, StringComparison.Ordinal)))
            score += 700;

        // For vague references, operational nouns in earlier turns are often the
        // antecedent ("slot đó", "team đó", "trận đó"). Give those turns a modest
        // bonus, but never enough to outrank an explicit quote or same-sender turn.
        if (referentialQuestion && SignificantTokens(message.Content).Any(ReferentialTokens.Contains))
            score += 180;

        return score;
    }

    private static bool IsAddressedToSender(string content, string senderName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(senderName)) return false;
        return content.Contains($"@{senderName}", StringComparison.OrdinalIgnoreCase) ||
               content.StartsWith(senderName + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksReferential(string normalizedQuestion)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuestion)) return false;
        var tokens = Regex.Split(normalizedQuestion, @"[^a-z0-9]+", RegexOptions.CultureInvariant)
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (tokens.Overlaps(ReferentialTokens)) return true;
        return normalizedQuestion.Contains("cai do", StringComparison.Ordinal) ||
               normalizedQuestion.Contains("nguoi do", StringComparison.Ordinal) ||
               normalizedQuestion.Contains("slot do", StringComparison.Ordinal) ||
               normalizedQuestion.Contains("team do", StringComparison.Ordinal) ||
               normalizedQuestion.StartsWith("vay ", StringComparison.Ordinal) ||
               normalizedQuestion.StartsWith("con ", StringComparison.Ordinal);
    }

    private static HashSet<string> MentionedParticipantNames(
        string normalizedQuestion,
        IReadOnlyList<ZaloAiMessage> messages)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            var name = ZaloBotIntelligence.Normalize(message.SenderName ?? string.Empty).Trim();
            if (name.Length < 2 || name.Length > 40) continue;
            if (normalizedQuestion.Contains(name, StringComparison.Ordinal))
                names.Add(name);
        }
        return names;
    }

    private static HashSet<string> SignificantTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new(StringComparer.Ordinal);
        var normalized = ZaloBotIntelligence.Normalize(value);
        return Regex.Split(normalized, @"[^a-z0-9]+", RegexOptions.CultureInvariant)
            .Where(token => token.Length >= 2)
            .Where(token => token is not ("bot" or "npc" or "minh" or "tui" or "toi" or "anh" or "chi" or "voi" or "khong" or "duoc"))
            .Take(50)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeId(string? value) => (value ?? string.Empty).Trim();
}
