using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Shared bounded conversation loader for context-first semantic lanes.
/// It reads conversation evidence only; authority and mutation stay in domain code.
/// </summary>
internal static class ZaloContextFirstConversationLoader
{
    internal static async Task<ZaloReadOnlyConversationContext> LoadAsync(
        VolleyDraftDbContext db,
        string connectionId,
        string groupId,
        ZaloIncomingMessageEvent incoming,
        int maxContextMessages = 8,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-12);
        var currentMessageId = Clean(incoming.MessageId, 160);
        var rows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == connectionId &&
                item.GroupId == groupId &&
                item.SentAt >= cutoff &&
                item.MessageId != currentMessageId)
            .Select(item => new
            {
                item.MessageId,
                item.SenderId,
                item.SenderName,
                item.Content,
                item.IsFromBot,
                item.SentAt
            })
            .ToListAsync(cancellationToken);

        var turns = rows
            .OrderBy(item => item.SentAt)
            .TakeLast(40)
            .Select(item => new ZaloReadOnlyConversationTurn(
                item.MessageId,
                new ZaloAiMessage(
                    item.IsFromBot ? "assistant" : "user",
                    Clean(item.SenderId, 100),
                    Clean(item.SenderName, 80),
                    Clean(item.Content, 650),
                    item.SentAt)))
            .ToList();

        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        if (quote.HasQuote)
        {
            turns.Add(new ZaloReadOnlyConversationTurn(
                Clean(quote.MessageId, 160),
                new ZaloAiMessage(
                    "context",
                    Clean(quote.SenderId, 100),
                    Clean(quote.SenderName, 80),
                    "[UNTRUSTED_ZALO_QUOTE] " + ZaloQuotedContextResolver.BuildAiGrounding(quote),
                    quote.SentAt ?? DateTimeOffset.UtcNow)));
        }

        if (turns.Count == 0) return new([], []);
        var assembled = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender(Clean(incoming.SenderId, 100), Clean(incoming.SenderName, 80)),
            incoming.Content ?? string.Empty,
            turns.Select(item => item.Message).ToArray(),
            Math.Clamp(maxContextMessages, 3, 12));

        return new ZaloReadOnlyConversationContext(assembled, []);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
