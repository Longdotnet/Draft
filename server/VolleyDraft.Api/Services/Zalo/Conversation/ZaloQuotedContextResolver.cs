using System.Text.RegularExpressions;
using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

public sealed record ZaloQuotedSemanticContext(
    string? MessageId,
    string? SenderId,
    string? SenderName,
    string Content,
    string? MessageType,
    DateTimeOffset? SentAt,
    bool RepliesToBot,
    bool RefersToQuotedPerson,
    bool RefersToQuotedObject)
{
    public bool HasQuote => !string.IsNullOrWhiteSpace(MessageId) ||
                            !string.IsNullOrWhiteSpace(SenderId) ||
                            !string.IsNullOrWhiteSpace(Content);
}

/// <summary>
/// Converts wire-level Zalo quote metadata into an explicit semantic relation.
/// Quoted text remains untrusted data; this resolver never turns quoted content
/// into instructions and never mutates the user's current question.
/// </summary>
public static class ZaloQuotedContextResolver
{
    private static readonly Regex QuotedPersonReference = new(
        @"(?<![a-z0-9])(?:ong|anh|chi|ban|nguoi|thang|dua|b(?:a|e))\s+(?:nay|do|kia)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedObjectReference = new(
        @"(?<![a-z0-9])(?:cai|vu|tran|keo|buoi|tin|noi dung)\s+(?:nay|do|kia)(?![a-z0-9])|(?<![a-z0-9])(?:cai do|cai nay|do|nay)(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ZaloQuotedSemanticContext Resolve(ZaloIncomingMessageEvent incoming, string? question = null)
    {
        var quote = incoming.Quote;
        var currentQuestion = ZaloBotIntelligence.Normalize(question ?? incoming.Content ?? string.Empty);
        var sentAt = ToTimestamp(quote?.SentAtUnixMs);
        var quoteSenderId = Clean(quote?.SenderId);
        var botId = Clean(incoming.BotId);

        return new ZaloQuotedSemanticContext(
            Clean(quote?.MessageId),
            quoteSenderId,
            Clean(quote?.SenderName),
            Truncate(quote?.Content, 4000),
            Clean(quote?.MessageType),
            sentAt,
            botId.Length > 0 && string.Equals(quoteSenderId, botId, StringComparison.Ordinal),
            quote is not null && QuotedPersonReference.IsMatch(currentQuestion),
            quote is not null && QuotedObjectReference.IsMatch(currentQuestion));
    }

    public static string BuildAiGrounding(ZaloQuotedSemanticContext context)
    {
        if (!context.HasQuote) return string.Empty;
        var relation = context.RepliesToBot ? "reply_to_bot" : "reply_to_message";
        var personReference = context.RefersToQuotedPerson ? "yes" : "no";
        var objectReference = context.RefersToQuotedObject ? "yes" : "no";
        var safeSender = Truncate(context.SenderName, 160);
        var safeContent = Truncate(context.Content, 1200);
        return $"QuoteRelation={relation}; QuotedMessageId={context.MessageId ?? "unknown"}; " +
               $"QuotedSenderId={context.SenderId ?? "unknown"}; QuotedSenderName={safeSender}; " +
               $"RefersToQuotedPerson={personReference}; RefersToQuotedObject={objectReference}; " +
               $"QuotedContent={safeContent}";
    }

    private static DateTimeOffset? ToTimestamp(long? unixMs)
    {
        if (unixMs is null) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim();

    private static string Truncate(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
