using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

public sealed record ZaloLegacyOutboundCanonicalizationResult(int Scanned, int Canonicalized, int Ambiguous);

/// <summary>
/// Replaces legacy synthetic bot:{guid} core-history IDs with the provider ID after
/// a successful send. Matching is deliberately strict: same connection/group,
/// same SHA-256 content fingerprint and a tight timestamp window. Ambiguous matches
/// are left untouched rather than guessed.
/// </summary>
public sealed class ZaloLegacyOutboundCanonicalizer(VolleyDraftDbContext db)
{
    public async Task<ZaloLegacyOutboundCanonicalizationResult> CanonicalizeAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var receiptStore = new ZaloOutboundReceiptStore(db);
        var receipts = await receiptStore.LoadRecentAsync(limit, cancellationToken);
        var canonicalized = 0;
        var ambiguous = 0;

        foreach (var receipt in receipts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await db.ZaloGroupMessages.AsNoTracking().AnyAsync(item =>
                    item.ZaloConnectionId == receipt.ZaloConnectionId &&
                    item.MessageId == receipt.ProviderMessageId,
                    cancellationToken))
            {
                await receiptStore.DeleteAsync(receipt.Id, cancellationToken);
                continue;
            }

            var lower = receipt.CreatedAt.AddMinutes(-2);
            var upper = receipt.CreatedAt.AddMinutes(2);
            // Keep stable identity/text predicates in SQL and evaluate the
            // DateTimeOffset window in memory for consistent SQLite/PostgreSQL behavior.
            var candidates = await db.ZaloGroupMessages
                .Where(item => item.ZaloConnectionId == receipt.ZaloConnectionId &&
                               item.GroupId == receipt.GroupId &&
                               item.IsFromBot &&
                               item.MessageId.StartsWith("bot:"))
                .ToListAsync(cancellationToken);
            candidates = candidates
                .Where(item => item.SentAt >= lower && item.SentAt <= upper)
                .ToList();

            var exact = candidates
                .Where(item => string.Equals(
                    ZaloOutboundReceiptStore.Fingerprint(item.Content),
                    receipt.ContentSha256,
                    StringComparison.Ordinal))
                .ToList();

            if (exact.Count != 1)
            {
                if (exact.Count > 1) ambiguous += 1;
                continue;
            }

            var selected = exact[0];
            selected.MessageId = receipt.ProviderMessageId;
            selected.ObservationSource = "ProviderIdCanonicalized";
            selected.LastObservedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await receiptStore.DeleteAsync(receipt.Id, cancellationToken);
            canonicalized += 1;
        }

        return new ZaloLegacyOutboundCanonicalizationResult(receipts.Count, canonicalized, ambiguous);
    }
}
