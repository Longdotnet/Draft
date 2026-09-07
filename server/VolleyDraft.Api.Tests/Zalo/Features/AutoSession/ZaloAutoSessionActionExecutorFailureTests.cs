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
}
