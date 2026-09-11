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

internal static class ZaloAutoSessionLifecycleReconciliationPolicyV5
{
    // Lifecycle handoff is durable post-commit recovery work. It must never monopolize
    // the shared scheduler long enough to delay the next reminder/pass-slot cycle.
    // A normal poll creates only a handful of sessions, while historical/repeated
    // failures can accumulate a much larger backlog. Fairness is already persisted by
    // LastAttemptAt, so a bounded batch rotates through that backlog across cycles.
    internal const int MaxCandidatesPerCycle = 12;
    internal static readonly TimeSpan CycleBudget = TimeSpan.FromSeconds(60);
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

            CREATE TABLE IF NOT EXISTS "ZaloAutoSessionLifecycleOwnerships" (
                "ProposalId" TEXT PRIMARY KEY,
                "State" TEXT NOT NULL,
                "LinkedSessionCount" INTEGER NOT NULL,
                "HandedOffAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ZaloAutoSessionLifecycleOwnerships_State"
                ON "ZaloAutoSessionLifecycleOwnerships" ("State", "HandedOffAt");
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
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloAutoSessionLifecycleHandoffs"
                ("SessionId", "ProposalId", "Stage", "Owner", "NeedsWebsite", "ReasonCode", "SnapshotJson", "HandedOffAt")
            VALUES
                ({{sessionId}}, {{proposalId}}, {{lifecycle.Value.Stage.ToString()}}, {{lifecycle.Value.Owner.ToString()}}, {{(lifecycle.Value.NeedsWebsite ? 1 : 0)}}, {{lifecycle.Value.ReasonCode}}, {{snapshotJson}}, {{handedOffAt.ToString("O")}})
            ON CONFLICT ("SessionId") DO UPDATE SET
                "Stage" = excluded."Stage",
                "Owner" = excluded."Owner",
                "NeedsWebsite" = excluded."NeedsWebsite",
                "ReasonCode" = excluded."ReasonCode",
                "SnapshotJson" = excluded."SnapshotJson",
                "HandedOffAt" = excluded."HandedOffAt"
            WHERE "ZaloAutoSessionLifecycleHandoffs"."ProposalId" = excluded."ProposalId";
            """, cancellationToken);

        // SessionId is the durable handoff identity. Never move an already-handed-off session
        // to a different proposal: doing so makes proposal-level ownership oscillate across
        // scheduler cycles and can leave multiple proposals falsely terminal at once.
        if (affected == 0)
            throw new InvalidOperationException(
                $"auto_session_lifecycle_session_proposal_conflict:{sessionId}");

        await TryFinalizeProposalOwnershipAsync(proposalId, cancellationToken);

        return new ZaloAutoSessionLifecycleHandoffResultV5(
            sessionId,
            lifecycle.Value.Stage,
            lifecycle.Value.Owner,
            lifecycle.Value.NeedsWebsite,
            lifecycle.Value.ReasonCode,
            handedOffAt);
    }

    public async Task<IReadOnlyList<ZaloAutoSessionLifecycleHandoffCandidateV5>> GetMissingAsync(
        int limit = ZaloAutoSessionLifecycleReconciliationPolicyV5.MaxCandidatesPerCycle,
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
               AND h."ProposalId" = p."Id"
            LEFT JOIN "ZaloAutoSessionLifecycleHandoffAttempts" a
                ON a."SessionId" = l."SessionId"
            WHERE p."Status" = 'Created'
              AND h."SessionId" IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "ZaloAutoSessionLifecycleHandoffs" existing_handoff
                  WHERE existing_handoff."SessionId" = l."SessionId"
                    AND existing_handoff."ProposalId" <> p."Id"
              )
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

    internal async Task<bool> TryFinalizeProposalOwnershipAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        await new ZaloAutoSessionStore(db).EnsureAsync(cancellationToken);
        await EnsureAsync(cancellationToken);
        var handedOffAt = DateTimeOffset.UtcNow.ToString("O");
        await using var command = await CreateCommandAsync(
            """
            INSERT INTO "ZaloAutoSessionLifecycleOwnerships"
                ("ProposalId", "State", "LinkedSessionCount", "HandedOffAt")
            SELECT p."Id", 'HandedOff', COUNT(DISTINCT l."SessionId"), @HandedOffAt
            FROM "ZaloPollSessionProposals" p
            INNER JOIN "ZaloAutoSessionLinks" l
                ON l."TrackedGroupId" = p."TrackedGroupId"
               AND l."PollId" = p."PollId"
            WHERE p."Id" = @ProposalId
              AND p."Status" = 'Created'
              AND NOT EXISTS (
                  SELECT 1
                  FROM "ZaloAutoSessionLinks" missing
                  LEFT JOIN "ZaloAutoSessionLifecycleHandoffs" h
                    ON h."SessionId" = missing."SessionId"
                   AND h."ProposalId" = p."Id"
                  WHERE missing."TrackedGroupId" = p."TrackedGroupId"
                    AND missing."PollId" = p."PollId"
                    AND h."SessionId" IS NULL
              )
            GROUP BY p."Id"
            HAVING COUNT(DISTINCT l."SessionId") > 0
            ON CONFLICT ("ProposalId") DO UPDATE SET
                "State" = excluded."State",
                "LinkedSessionCount" = excluded."LinkedSessionCount";
            """,
            cancellationToken);
        AddParameter(command, "@ProposalId", proposalId);
        AddParameter(command, "@HandedOffAt", handedOffAt);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    internal async Task<int> ReconcileCompletedOwnershipsAsync(
        CancellationToken cancellationToken = default)
    {
        await new ZaloAutoSessionStore(db).EnsureAsync(cancellationToken);
        await EnsureAsync(cancellationToken);

        // Older builds could move one SessionId handoff between proposals while leaving both
        // proposal aggregates terminal. Remove only Created-proposal aggregates whose current
        // links no longer have matching proposal-scoped handoff evidence; the insert below then
        // reconstructs any aggregate that is still fully grounded.
        await using (var cleanup = await CreateCommandAsync(
            """
            DELETE FROM "ZaloAutoSessionLifecycleOwnerships"
            WHERE "State" = 'HandedOff'
              AND EXISTS (
                  SELECT 1
                  FROM "ZaloPollSessionProposals" p
                  WHERE p."Id" = "ZaloAutoSessionLifecycleOwnerships"."ProposalId"
                    AND p."Status" = 'Created'
                    AND EXISTS (
                        SELECT 1
                        FROM "ZaloAutoSessionLinks" missing
                        LEFT JOIN "ZaloAutoSessionLifecycleHandoffs" h
                          ON h."SessionId" = missing."SessionId"
                         AND h."ProposalId" = p."Id"
                        WHERE missing."TrackedGroupId" = p."TrackedGroupId"
                          AND missing."PollId" = p."PollId"
                          AND h."SessionId" IS NULL
                    )
              );
            """,
            cancellationToken))
        {
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        var handedOffAt = DateTimeOffset.UtcNow.ToString("O");
        await using var command = await CreateCommandAsync(
            """
            INSERT INTO "ZaloAutoSessionLifecycleOwnerships"
                ("ProposalId", "State", "LinkedSessionCount", "HandedOffAt")
            SELECT p."Id", 'HandedOff', COUNT(DISTINCT l."SessionId"), @HandedOffAt
            FROM "ZaloPollSessionProposals" p
            INNER JOIN "ZaloAutoSessionLinks" l
                ON l."TrackedGroupId" = p."TrackedGroupId"
               AND l."PollId" = p."PollId"
            LEFT JOIN "ZaloAutoSessionLifecycleOwnerships" ownership
                ON ownership."ProposalId" = p."Id"
            WHERE p."Status" = 'Created'
              AND ownership."ProposalId" IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "ZaloAutoSessionLinks" missing
                  LEFT JOIN "ZaloAutoSessionLifecycleHandoffs" h
                    ON h."SessionId" = missing."SessionId"
                   AND h."ProposalId" = p."Id"
                  WHERE missing."TrackedGroupId" = p."TrackedGroupId"
                    AND missing."PollId" = p."PollId"
                    AND h."SessionId" IS NULL
              )
            GROUP BY p."Id"
            HAVING COUNT(DISTINCT l."SessionId") > 0;
            """,
            cancellationToken);
        AddParameter(command, "@HandedOffAt", handedOffAt);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<bool> HasHandedOffOwnershipAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var command = await CreateCommandAsync(
            "SELECT 1 FROM \"ZaloAutoSessionLifecycleOwnerships\" WHERE \"ProposalId\" = @ProposalId AND \"State\" = 'HandedOff' LIMIT 1;",
            cancellationToken);
        AddParameter(command, "@ProposalId", proposalId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<ZaloAutoSessionLifecycleReconciliationResultV5> ReconcileMissingAsync(
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        using var budgetCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCancellation.CancelAfter(ZaloAutoSessionLifecycleReconciliationPolicyV5.CycleBudget);
        var budgetToken = budgetCancellation.Token;
        IReadOnlyList<ZaloAutoSessionLifecycleHandoffCandidateV5> candidates = [];
        var handedOff = 0;
        var failed = 0;

        try
        {
            candidates = await GetMissingAsync(cancellationToken: budgetToken);

            foreach (var candidate in candidates)
            {
                // Persist scheduling fairness before invoking the lifecycle coordinator. If this
                // process dies mid-attempt, the candidate is delayed behind never/less-recently
                // attempted sessions instead of monopolizing every future batch.
                await MarkAttemptAsync(candidate.SessionId, DateTimeOffset.UtcNow, budgetToken);
                try
                {
                    await HandOffAsync(
                        candidate.ProposalId,
                        candidate.AdminUserId,
                        candidate.SessionId,
                        budgetToken);
                    handedOff++;
                }
                catch (OperationCanceledException) when (
                    budgetCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    failed++;
                    logger.LogWarning(
                        "Auto Session V5 lifecycle reconciliation exhausted its {BudgetSeconds}s scheduler budget after {HandedOff}/{CandidateCount} handoffs",
                        ZaloAutoSessionLifecycleReconciliationPolicyV5.CycleBudget.TotalSeconds,
                        handedOff,
                        candidates.Count);
                    return new ZaloAutoSessionLifecycleReconciliationResultV5(
                        candidates.Count,
                        handedOff,
                        failed);
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

            // Backfill the proposal-level ownership terminal for sessions handed off before this
            // aggregate existed, including clean restarts where no per-session retry is needed.
            await ReconcileCompletedOwnershipsAsync(budgetToken);
        }
        catch (OperationCanceledException) when (
            budgetCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            failed++;
            logger.LogWarning(
                "Auto Session V5 lifecycle reconciliation exhausted its {BudgetSeconds}s scheduler budget before completing the batch",
                ZaloAutoSessionLifecycleReconciliationPolicyV5.CycleBudget.TotalSeconds);
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
