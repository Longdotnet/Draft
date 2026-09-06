using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;

namespace VolleyDraft.Api.Services;

public sealed record ZaloLegacyPendingProjectionResult(
    int Scanned,
    int Projected,
    int SkippedDifferentIntent,
    int RemovedStale = 0);

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
        var removedStale = 0;

        foreach (var pending in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupId = Clean(pending.GroupId, 100);
            var senderId = Clean(pending.SenderZaloUserId, 100);
            var intent = Clean(pending.PendingIntent, 120);
            if (groupId.Length == 0 || senderId.Length == 0 || intent.Length == 0) continue;

            var typed = ZaloLegacyPendingPayloadAdapter.Adapt(intent, pending.PendingPayloadJson);
            var existing = await store.LoadActiveAsync(groupId, senderId, cancellationToken);

            // Auto-draft/redraft confirmation payloads are executable references, not
            // historical hints. If their target session disappeared, was moved away,
            // disabled, cancelled, or crossed the draft lifecycle boundary, keeping the
            // row alive makes every `xác nhận` fall back to the same waiting prompt.
            // Remove only the exact legacy snapshot we inspected so a concurrent newer
            // pending action cannot be deleted by this compatibility cleanup.
            if (await IsStaleDraftConfirmationAsync(pending, intent, cancellationToken))
            {
                var deleted = await db.ZaloBotConversationStates
                    .Where(item => item.Id == pending.Id &&
                                   item.PendingIntent == pending.PendingIntent &&
                                   item.PendingPayloadJson == pending.PendingPayloadJson)
                    .ExecuteDeleteAsync(cancellationToken);
                if (deleted > 0)
                {
                    removedStale += deleted;
                    if (existing is not null &&
                        string.Equals(existing.Intent, intent, StringComparison.OrdinalIgnoreCase))
                    {
                        await store.CancelAsync(groupId, senderId, cancellationToken);
                    }
                }
                continue;
            }

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

        return new ZaloLegacyPendingProjectionResult(active.Count, projected, skippedDifferentIntent, removedStale);
    }

    private async Task<bool> IsStaleDraftConfirmationAsync(
        ZaloBotConversationState pending,
        string intent,
        CancellationToken cancellationToken)
    {
        var isAutoDraft = string.Equals(intent, ZaloBotIntent.AutoDraftConfirm.ToString(), StringComparison.Ordinal);
        var isRedraft = string.Equals(intent, ZaloBotIntent.RedraftConfirm.ToString(), StringComparison.Ordinal);
        if (!isAutoDraft && !isRedraft) return false;

        List<string> sessionIds;
        try
        {
            sessionIds = (JsonSerializer.Deserialize<List<string>>(pending.PendingPayloadJson ?? "[]") ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return true;
        }
        if (sessionIds.Count == 0) return true;

        var candidates = await db.MatchSessions.AsNoTracking()
            .Where(session => sessionIds.Contains(session.Id) &&
                              session.ZaloConnectionId == pending.ZaloConnectionId &&
                              session.ZaloGroupId == pending.GroupId &&
                              session.BotEnabled &&
                              session.Status != SessionStatus.Cancelled)
            .Select(session => new { session.Id, session.Status })
            .ToListAsync(cancellationToken);

        return isRedraft
            ? candidates.All(session => session.Status != SessionStatus.Finished)
            : candidates.All(session => session.Status == SessionStatus.Finished);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
