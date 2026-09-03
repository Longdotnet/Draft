using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloLegacyPendingProjectionResult(int Scanned, int Projected, int SkippedDifferentIntent);

/// <summary>
/// Reprojects active legacy pending workflows into typed ConversationState V2 data.
/// Legacy handlers remain the execution source during migration, but V2 no longer
/// needs to treat their opaque JSON payload as the collected-arguments contract.
/// </summary>
public sealed class ZaloLegacyPendingStateProjector(VolleyDraftDbContext db)
{
    public async Task<ZaloLegacyPendingProjectionResult> ProjectAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var now = DateTimeOffset.UtcNow;
        // SQLite cannot translate DateTimeOffset comparisons/order reliably. Keep
        // entity selection in the provider and evaluate temporal semantics in memory
        // so SQLite and PostgreSQL use the same rule.
        var rows = await db.ZaloBotConversationStates.AsNoTracking()
            .ToListAsync(cancellationToken);
        var active = rows
            .Where(item => item.ExpiresAt > now)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(limit)
            .ToList();
        return await ProjectRowsAsync(active, cancellationToken);
    }

    public async Task<ZaloLegacyPendingProjectionResult> ProjectScopeAsync(
        string groupId,
        string senderZaloUserId,
        CancellationToken cancellationToken = default)
    {
        groupId = Clean(groupId, 100);
        senderZaloUserId = Clean(senderZaloUserId, 100);
        if (groupId.Length == 0 || senderZaloUserId.Length == 0) return new(0, 0, 0);
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ZaloBotConversationStates.AsNoTracking()
            .Where(item => item.GroupId == groupId &&
                           item.SenderZaloUserId == senderZaloUserId)
            .ToListAsync(cancellationToken);
        var active = rows
            .Where(item => item.ExpiresAt > now)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(1)
            .ToList();
        return await ProjectRowsAsync(active, cancellationToken);
    }

    private async Task<ZaloLegacyPendingProjectionResult> ProjectRowsAsync(
        IReadOnlyList<ZaloBotConversationState> active,
        CancellationToken cancellationToken)
    {
        var store = new ZaloConversationStateV2Store(db);
        var projected = 0;
        var skippedDifferentIntent = 0;

        foreach (var pending in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupId = Clean(pending.GroupId, 100);
            var senderId = Clean(pending.SenderZaloUserId, 100);
            var intent = Clean(pending.PendingIntent, 120);
            if (groupId.Length == 0 || senderId.Length == 0 || intent.Length == 0) continue;

            var typed = ZaloLegacyPendingPayloadAdapter.Adapt(intent, pending.PendingPayloadJson);
            var existing = await store.LoadActiveAsync(groupId, senderId, cancellationToken);
            if (existing is not null && !string.Equals(existing.Intent, intent, StringComparison.OrdinalIgnoreCase))
            {
                skippedDifferentIntent += 1;
                continue;
            }
            if (existing is not null &&
                existing.CollectedArgumentsJson == typed.CollectedArgumentsJson &&
                existing.MissingArgumentsJson == typed.MissingArgumentsJson &&
                existing.CandidateEntitiesJson == typed.CandidateEntitiesJson &&
                existing.ExpiresAt == pending.ExpiresAt)
                continue;

            await store.SaveActiveAsync(
                groupId,
                senderId,
                intent,
                typed.CollectedArgumentsJson,
                typed.MissingArgumentsJson,
                typed.CandidateEntitiesJson,
                existing?.SourceMessageId,
                existing?.LastMessageId,
                pending.ExpiresAt,
                cancellationToken);
            projected += 1;
        }

        return new ZaloLegacyPendingProjectionResult(active.Count, projected, skippedDifferentIntent);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
