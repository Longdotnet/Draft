using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionActionExecutorFailureTests
{
    [Fact]
    public async Task PersistFailureAfterRollback_DisposesCompletedTransactionBeforeDurableStatusWrite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionStore(db);
        await store.EnsureAsync();

        var proposal = new ZaloPollSessionProposalData
        {
            Id = "proposal-rollback",
            TrackedGroupId = "tracked-1",
            PollId = "poll-1",
            PollQuestion = "Vote lịch",
            PollCreatorId = "leader-1",
            PollStructureHash = "hash-1",
            CandidatesJson = "[]",
            ClassifierReason = "test",
            Status = ZaloPollSessionProposalStatus.AwaitingApproval,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await store.UpsertProposalAsync(proposal);

        var transaction = await db.Database.BeginTransactionAsync();

        await ZaloAutoSessionActionExecutor.PersistFailureAfterRollbackAsync(
            transaction,
            db,
            store,
            proposal,
            new InvalidOperationException("session creation failed"));

        Assert.Null(db.Database.CurrentTransaction);
        var persisted = await store.GetProposalAsync(proposal.TrackedGroupId, proposal.PollId);
        Assert.NotNull(persisted);
        Assert.Equal(ZaloPollSessionProposalStatus.Failed, persisted!.Status);
        Assert.Equal("session creation failed", persisted.LastError);
    }

    [Fact]
    public async Task PersistFailureAfterRollback_DoesNotDowngradeAlreadyCreatedProposal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        var store = new ZaloAutoSessionStore(db);
        await store.EnsureAsync();

        var winner = new ZaloPollSessionProposalData
        {
            Id = "proposal-race",
            TrackedGroupId = "tracked-race",
            PollId = "poll-race",
            PollQuestion = "Vote lịch",
            PollCreatorId = "leader-1",
            PollStructureHash = "hash-race",
            CandidatesJson = "[]",
            ClassifierReason = "test",
            Status = ZaloPollSessionProposalStatus.Created,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await store.UpsertProposalAsync(winner);

        var loser = new ZaloPollSessionProposalData
        {
            Id = winner.Id,
            TrackedGroupId = winner.TrackedGroupId,
            PollId = winner.PollId,
            PollQuestion = winner.PollQuestion,
            PollCreatorId = winner.PollCreatorId,
            PollStructureHash = winner.PollStructureHash,
            CandidatesJson = winner.CandidatesJson,
            ClassifierReason = winner.ClassifierReason,
            Status = ZaloPollSessionProposalStatus.Approved,
            CreatedAt = winner.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var transaction = await db.Database.BeginTransactionAsync();

        await ZaloAutoSessionActionExecutor.PersistFailureAfterRollbackAsync(
            transaction,
            db,
            store,
            loser,
            new InvalidOperationException("losing execution collided"));

        var persisted = await store.GetProposalAsync(winner.TrackedGroupId, winner.PollId);
        Assert.NotNull(persisted);
        Assert.Equal(ZaloPollSessionProposalStatus.Created, persisted!.Status);
        Assert.Null(persisted.LastError);
    }

    [Fact]
    public async Task RunCommittedPostCreateAsync_DoesNotInheritCancelledRequestLifetime()
    {
        using var request = new CancellationTokenSource();
        request.Cancel();
        var observedCanBeCanceled = true;
        var completed = false;

        await ZaloAutoSessionActionExecutor.RunCommittedPostCreateAsync(token =>
        {
            observedCanBeCanceled = token.CanBeCanceled;
            token.ThrowIfCancellationRequested();
            completed = true;
            return Task.CompletedTask;
        });

        Assert.True(request.IsCancellationRequested);
        Assert.False(observedCanBeCanceled);
        Assert.True(completed);
    }

    [Fact]
    public async Task RunCommittedPostCreateAsync_NormalizesDownstreamCancellationAfterCommit()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ZaloAutoSessionActionExecutor.RunCommittedPostCreateAsync(_ =>
                throw new OperationCanceledException("provider timeout after commit")));

        Assert.Equal("auto_session_post_commit_cancelled", exception.Message);
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public void BuildPostCreateStatusMessage_PostCommitFailuresKeepCommittedCreationTruth()
    {
        var message = ZaloAutoSessionActionExecutor.BuildPostCreateStatusMessage(
            "T6 11/09 17:45",
            linkedCount: 1,
            handoffFailureCount: 1,
            syncFailureCount: 2);

        Assert.Contains("Đã tạo trên website", message, StringComparison.Ordinal);
        Assert.Contains("Session đã được tạo", message, StringComparison.Ordinal);
        Assert.Contains("Session vẫn đã được tạo", message, StringComparison.Ordinal);
        Assert.Contains("1 lifecycle handoff chưa hoàn tất", message, StringComparison.Ordinal);
        Assert.Contains("2 lượt đồng bộ hậu tạo chưa hoàn tất", message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPostCreateStatusMessage_SuccessClaimsSnapshotNotFutureLifecycleStage()
    {
        var message = ZaloAutoSessionActionExecutor.BuildPostCreateStatusMessage(
            "T6 11/09 17:45",
            linkedCount: 1,
            handoffFailureCount: 0,
            syncFailureCount: 0);

        Assert.Contains("Match Lifecycle đã nhận snapshot authoritative", message, StringComparison.Ordinal);
        Assert.DoesNotContain("recruiting/waitlist/pass-slot/profile/draft readiness", message, StringComparison.Ordinal);
    }
}
