using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;

namespace VolleyDraft.Api.Services;

internal sealed record ZaloAutoSessionLifecycleHandoffResultV5(
    string SessionId,
    MatchLifecycleStage Stage,
    MatchLifecycleOwner Owner,
    bool NeedsWebsite,
    string ReasonCode,
    DateTimeOffset EvaluatedAt);

internal static class ZaloAutoSessionLifecycleHandoffPolicyV5
{
    public static bool CanHandOff(MatchLifecycleResponse lifecycle) =>
        lifecycle.Stage is not MatchLifecycleStage.NeedsSetup &&
        lifecycle.Stage is not MatchLifecycleStage.NeedsAttention;

    public static string BuildFailureReason(MatchLifecycleResponse lifecycle) =>
        $"lifecycle_not_ready:{lifecycle.Stage}:{lifecycle.ReasonCode}";
}

internal sealed class ZaloAutoSessionLifecycleHandoffStoreV5(VolleyDraftDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool ensured;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (ensured) return;
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionLifecycleHandoffs" (
                "SessionId" TEXT PRIMARY KEY,
                "ProposalId" TEXT NOT NULL,
                "ConversationId" TEXT NOT NULL,
                "Stage" TEXT NOT NULL,
                "Owner" TEXT NOT NULL,
                "NeedsWebsite" INTEGER NOT NULL,
                "ReasonCode" TEXT NOT NULL,
                "SnapshotJson" TEXT NOT NULL,
                "HandedOffAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionLifecycleHandoffs_Proposal"
                ON "ZaloAutoSessionLifecycleHandoffs" ("ProposalId", "HandedOffAt");
            """,
            cancellationToken);
        ensured = true;
    }

    public async Task<ZaloAutoSessionLifecycleHandoffResultV5> HandOffAsync(
        string proposalId,
        string conversationId,
        string adminUserId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = await new MatchLifecycleCoordinator(db)
            .GetAsync(adminUserId, sessionId, cancellationToken);
        if (!lifecycle.IsSuccess || lifecycle.Value is null)
            throw new InvalidOperationException($"auto_session_lifecycle_snapshot_unavailable:{sessionId}");

        if (!ZaloAutoSessionLifecycleHandoffPolicyV5.CanHandOff(lifecycle.Value))
            throw new InvalidOperationException(
                ZaloAutoSessionLifecycleHandoffPolicyV5.BuildFailureReason(lifecycle.Value));

        await EnsureAsync(cancellationToken);
        var snapshotJson = JsonSerializer.Serialize(lifecycle.Value, JsonOptions);
        var handedOffAt = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloAutoSessionLifecycleHandoffs"
                ("SessionId", "ProposalId", "ConversationId", "Stage", "Owner", "NeedsWebsite", "ReasonCode", "SnapshotJson", "HandedOffAt")
            VALUES
                ({{sessionId}}, {{proposalId}}, {{conversationId}}, {{lifecycle.Value.Stage.ToString()}}, {{lifecycle.Value.Owner.ToString()}}, {{lifecycle.Value.NeedsWebsite ? 1 : 0}}, {{lifecycle.Value.ReasonCode}}, {{snapshotJson}}, {{handedOffAt.ToString("O")}})
            ON CONFLICT ("SessionId") DO UPDATE SET
                "ProposalId" = excluded."ProposalId",
                "ConversationId" = excluded."ConversationId",
                "Stage" = excluded."Stage",
                "Owner" = excluded."Owner",
                "NeedsWebsite" = excluded."NeedsWebsite",
                "ReasonCode" = excluded."ReasonCode",
                "SnapshotJson" = excluded."SnapshotJson",
                "HandedOffAt" = excluded."HandedOffAt";
            """, cancellationToken);

        return new ZaloAutoSessionLifecycleHandoffResultV5(
            sessionId,
            lifecycle.Value.Stage,
            lifecycle.Value.Owner,
            lifecycle.Value.NeedsWebsite,
            lifecycle.Value.ReasonCode,
            handedOffAt);
    }
}
