using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Infers a short conversational lease from an earlier successful bot reply to the
/// same sender in the same Zalo group. The lease is addressing context only: it may
/// keep read-only and social follow-ups conversational, but it never grants mutation
/// authority.
/// </summary>
public sealed class ZaloAmbientConversationLeaseResolver(VolleyDraftDbContext db)
{
    public async Task<bool> IsActiveAsync(
        string zaloConnectionId,
        string groupId,
        string senderId,
        int leaseSeconds = 180,
        CancellationToken cancellationToken = default)
    {
        zaloConnectionId = Clean(zaloConnectionId, 100);
        groupId = Clean(groupId, 100);
        senderId = Clean(senderId, 100);
        if (zaloConnectionId.Length == 0 || groupId.Length == 0 || senderId.Length == 0)
            return false;

        leaseSeconds = Math.Clamp(leaseSeconds, 30, 600);

        // Keep DateTimeOffset ordering/comparison in memory for SQLite/PostgreSQL
        // parity. The sender/group scope is already bounded by the recent message
        // history retained by the bot. A successful Social AI reply opens the same
        // address-only lease as a deterministic Fact/legacy reply; the lease itself
        // never authorizes a write.
        var rows = await db.ZaloGroupMessages
            .AsNoTracking()
            .Where(item => item.ZaloConnectionId == zaloConnectionId &&
                           item.GroupId == groupId &&
                           item.SenderId == senderId &&
                           !item.IsFromBot &&
                           item.BotReplySentAt != null &&
                           (item.ReplyOutcome == "ambient_sent" ||
                            item.ReplyOutcome == "ambient_social_sent" ||
                            item.ReplyOutcome == "sent"))
            .Select(item => item.BotReplySentAt)
            .ToListAsync(cancellationToken);

        var lastReply = rows
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        if (lastReply == DateTimeOffset.MinValue) return false;
        var now = DateTimeOffset.UtcNow;
        return lastReply <= now.AddSeconds(10) &&
               lastReply >= now.AddSeconds(-leaseSeconds);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
