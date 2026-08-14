using System.Text.RegularExpressions;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Builds a small, sender-aware conversational window before any chat history is
/// sent to an AI provider. The database remains the source of truth; this class
/// only decides which untrusted conversation turns are useful context.
/// </summary>
public static class ZaloConversationContextAssembler
{
    private const int DefaultMaxMessages = 12;
    private const int ImmediateTailSize = 4;

    public static IReadOnlyList<ZaloAiMessage> Assemble(
        ZaloAiSender sender,
        string question,
        IReadOnlyList<ZaloAiMessage> messages,
        int maxMessages = DefaultMaxMessages)
    {
        if (messages.Count == 0) return [];
        maxMessages = Math.Clamp(maxMessages, 1, 20);
        if (messages.Count <= maxMessages) return messages.ToList();

        var senderId = NormalizeId(sender.Id);
        var senderName = sender.Name?.Trim() ?? string.Empty;
        var questionTokens = SignificantTokens(question);
        var senderIndexes = messages
            .Select((message, index) => new { message, index })
            .Where(item => string.Equals(NormalizeId(item.message.SenderId), senderId, StringComparison.Ordinal))
            .Select(item => item.index)
            .ToHashSet();

        // Always retain a small immediate tail. Pure score-based selection can
        // otherwise let older sender-adjacent turns crowd out the latest group
        // message, which is often the missing referent for "cái đó", "ông này",
        // or a short follow-up after another member just spoke.
        var tailCount = Math.Min(ImmediateTailSize, Math.Max(1, maxMessages / 2));
        var tailStart = Math.Max(0, messages.Count - tailCount);
        var selectedIndexes = Enumerable.Range(tailStart, messages.Count - tailStart).ToHashSet();
        var semanticSlots = Math.Max(0, maxMessages - selectedIndexes.Count);

        var semanticIndexes = messages
            .Select((message, index) => new
            {
                Index = index,
                Score = Score(
                    message,
                    index,
                    messages.Count,
                    senderId,
                    senderName,
                    senderIndexes,
                    questionTokens)
            })
            .Where(item => !selectedIndexes.Contains(item.Index))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Index)
            .Take(semanticSlots)
            .Select(item => item.Index);

        selectedIndexes.UnionWith(semanticIndexes);
        return selectedIndexes
            .OrderBy(index => index)
            .Select(index => messages[index])
            .ToList();
    }

    private static int Score(
        ZaloAiMessage message,
        int index,
        int messageCount,
        string senderId,
        string senderName,
        HashSet<int> senderIndexes,
        HashSet<string> questionTokens)
    {
        var score = index; // deterministic recency tie-breaker
        var normalizedMessageSender = NormalizeId(message.SenderId);

        if (string.Equals(normalizedMessageSender, senderId, StringComparison.Ordinal))
            score += 1_000;

        if (message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
            IsAddressedToSender(message.Content, senderName))
            score += 800;

        if (senderIndexes.Contains(index - 1) || senderIndexes.Contains(index + 1))
            score += 550;

        if (index >= messageCount - ImmediateTailSize)
            score += 350;

        var overlap = SignificantTokens(message.Content).Count(questionTokens.Contains);
        score += Math.Min(overlap, 5) * 90;

        return score;
    }

    private static bool IsAddressedToSender(string content, string senderName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(senderName)) return false;
        return content.Contains($"@{senderName}", StringComparison.OrdinalIgnoreCase) ||
               content.StartsWith(senderName + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> SignificantTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new(StringComparer.Ordinal);
        var normalized = ZaloBotIntelligence.Normalize(value);
        return Regex.Split(normalized, @"[^a-z0-9]+", RegexOptions.CultureInvariant)
            .Where(token => token.Length >= 3)
            .Where(token => token is not ("bot" or "npc" or "minh" or "tui" or "toi" or "anh" or "chi" or "cho" or "voi" or "nay" or "kia" or "khong" or "duoc"))
            .Take(40)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeId(string? value) => (value ?? string.Empty).Trim();
}
