using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

internal sealed record ZaloAutoSessionLifecycleHandoffCandidateV5(
    string ProposalId,
    string AdminUserId,
    string SessionId);

internal sealed record ZaloAutoSessionLifecycleReconciliationResultV5(
    int CandidateCount,
    int HandedOffCount,
    int FailedCount);

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
                "Stage" TEXT NOT NULL,
                "Owner" TEXT NOT NULL,
                "NeedsWebsite" INTEGER NOT NULL,
                "ReasonCode" TEXT NOT NULL,
                "SnapshotJson" TEXT NOT NULL,
                "HandedOffAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionLifecycleHandoffs_Proposal"
                ON "ZaloAutoSessionLifecycleHandoffs" ("ProposalId", "HandedOffAt");

            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionLifecycleHandoffAttempts" (
                "SessionId" TEXT PRIMARY KEY,
                "LastAttemptAt" TEXT NOT NULL,
                "AttemptCount" INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionLifecycleHandoffAttempts_LastAttempt"
                ON "ZaloAutoSessionLifecycleHandoffAttempts" ("LastAttemptAt");
            """,
            cancellationToken);
        ensured = true;
    }

    public async Task<ZaloAutoSessionLifecycleHandoffResultV5> HandOffAsync(
        string proposalId,
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
                ("SessionId", "ProposalId", "Stage", "Owner", "NeedsWebsite", "ReasonCode", "SnapshotJson", "HandedOffAt")
            VALUES
                ({{sessionId}}, {{proposalId}}, {{lifecycle.Value.Stage.ToString()}}, {{lifecycle.Value.Owner.ToString()}}, {{(lifecycle.Value.NeedsWebsite ? 1 : 0)}}, {{lifecycle.Value.ReasonCode}}, {{snapshotJson}}, {{handedOffAt.ToString("O")}})
            ON CONFLICT ("SessionId") DO UPDATE SET
                "ProposalId" = excluded."ProposalId",
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

    public async Task<IReadOnlyList<ZaloAutoSessionLifecycleHandoffCandidateV5>> GetMissingAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await new ZaloAutoSessionStore(db).EnsureAsync(cancellationToken);
        await EnsureAsync(cancellationToken);
        limit = Math.Clamp(limit, 1, 200);

        await using var command = await CreateCommandAsync(
            """
            SELECT p."Id", g."AdminUserId", l."SessionId"
            FROM "ZaloAutoSessionLinks" l
            INNER JOIN "ZaloPollSessionProposals" p
                ON p."TrackedGroupId" = l."TrackedGroupId"
               AND p."PollId" = l."PollId"
            INNER JOIN "ZaloTrackedGroups" g
                ON g."Id" = l."TrackedGroupId"
            LEFT JOIN "ZaloAutoSessionLifecycleHandoffs" h
                ON h."SessionId" = l."SessionId"
            LEFT JOIN "ZaloAutoSessionLifecycleHandoffAttempts" a
                ON a."SessionId" = l."SessionId"
            WHERE p."Status" = 'Created'
              AND h."SessionId" IS NULL
            GROUP BY p."Id", g."AdminUserId", l."SessionId", a."LastAttemptAt"
            ORDER BY
                CASE WHEN a."LastAttemptAt" IS NULL THEN 0 ELSE 1 END ASC,
                a."LastAttemptAt" ASC,
                MIN(l."CreatedAt") ASC
            LIMIT @Limit;
            """,
            cancellationToken);
        AddParameter(command, "@Limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ZaloAutoSessionLifecycleHandoffCandidateV5>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ZaloAutoSessionLifecycleHandoffCandidateV5(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return result;
    }

    internal async Task MarkAttemptAsync(
        string sessionId,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloAutoSessionLifecycleHandoffAttempts"
                ("SessionId", "LastAttemptAt", "AttemptCount")
            VALUES
                ({{sessionId}}, {{attemptedAt.ToString("O")}}, 1)
            ON CONFLICT ("SessionId") DO UPDATE SET
                "LastAttemptAt" = excluded."LastAttemptAt",
                "AttemptCount" = "ZaloAutoSessionLifecycleHandoffAttempts"."AttemptCount" + 1;
            """, cancellationToken);
    }

    public async Task<ZaloAutoSessionLifecycleReconciliationResultV5> ReconcileMissingAsync(
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var candidates = await GetMissingAsync(cancellationToken: cancellationToken);
        var handedOff = 0;
        var failed = 0;

        foreach (var candidate in candidates)
        {
            // Persist scheduling fairness before invoking the lifecycle coordinator. If this
            // process dies mid-attempt, the candidate is delayed behind never/less-recently
            // attempted sessions instead of monopolizing every future batch.
            await MarkAttemptAsync(candidate.SessionId, DateTimeOffset.UtcNow, cancellationToken);
            try
            {
                await HandOffAsync(
                    candidate.ProposalId,
                    candidate.AdminUserId,
                    candidate.SessionId,
                    cancellationToken);
                handedOff++;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
                logger.LogWarning(
                    exception,
                    "Auto Session V5 lifecycle reconciliation deferred Proposal={ProposalId} Session={SessionId}",
                    candidate.ProposalId,
                    candidate.SessionId);
            }
        }

        return new ZaloAutoSessionLifecycleReconciliationResultV5(candidates.Count, handedOff, failed);
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
