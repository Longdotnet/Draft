using System.Text.Json;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Treats an exact reply to an expired draft-confirmation prompt as durable context only.
/// It can recover the one session that the old prompt referred to, but it never restores
/// the expired mutation authority. Callers must route the turn back through a fresh
/// deterministic draft-readiness command so current roster/session state is re-read and a
/// new short-lived confirmation is issued only when the current state still permits it.
/// </summary>
internal static class ZaloExpiredDraftConfirmationRecoveryPolicy
{
    internal static readonly TimeSpan MaximumContextAge = TimeSpan.FromHours(4);

    internal static string? ResolveSessionId(
        ZaloBotConversationState? pending,
        ZaloQuotedSemanticContext quote,
        ZaloMessageGraphRelation? quotedBotRelation,
        ZaloGroupMessage? promptSource,
        DateTimeOffset now)
    {
        if (pending is null ||
            pending.ExpiresAt > now ||
            pending.UpdatedAt < now.Subtract(MaximumContextAge) ||
            !string.Equals(
                pending.PendingIntent,
                ZaloBotIntent.AutoDraftConfirm.ToString(),
                StringComparison.Ordinal))
            return null;

        if (!quote.RepliesToBot || string.IsNullOrWhiteSpace(quote.MessageId))
            return null;

        if (quotedBotRelation is null ||
            !string.Equals(quotedBotRelation.RelationType, "BotReply", StringComparison.Ordinal) ||
            !string.Equals(quotedBotRelation.FromMessageId, quote.MessageId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(quotedBotRelation.ToMessageId))
            return null;

        if (promptSource is null ||
            promptSource.IsFromBot ||
            promptSource.BotReplySentAt is null ||
            promptSource.BotReplySentAt.Value < pending.UpdatedAt ||
            !string.Equals(promptSource.MessageId, quotedBotRelation.ToMessageId, StringComparison.Ordinal) ||
            !string.Equals(promptSource.ZaloConnectionId, pending.ZaloConnectionId, StringComparison.Ordinal) ||
            !string.Equals(promptSource.GroupId, pending.GroupId, StringComparison.Ordinal) ||
            !string.Equals(promptSource.SenderId, pending.SenderZaloUserId, StringComparison.Ordinal) ||
            !string.Equals(
                promptSource.SelectedIntent,
                ZaloBotIntent.AutoDraft.ToString(),
                StringComparison.Ordinal))
            return null;

        try
        {
            var sessionIds = JsonSerializer.Deserialize<List<string>>(pending.PendingPayloadJson ?? "[]")
                ?.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToList() ?? [];
            return sessionIds.Count == 1 ? sessionIds[0] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
