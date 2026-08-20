using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftEscalationStoreTests
{
    [Fact]
    public async Task Execution_claim_is_atomic_and_only_one_approver_wins()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var store = new ZaloDraftEscalationStore(db);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(20);

        var created = await store.CreateOrReuseAsync(
            "conn-1", "group-1", "session-1", "Member",
            "requester", "Requester", "msg-request",
            "fingerprint-1", ZaloDraftEscalationState.AwaitingRequesterConsent,
            expiresAt);
        await store.SetPrimaryApproverAsync(
            created.Id, "leader-1", "prompt-1", DateTimeOffset.UtcNow, expiresAt);
        await store.SetSecondaryApproverAsync(
            created.Id, "leader-2", "prompt-2", DateTimeOffset.UtcNow, expiresAt);

        var tagged = await store.LoadForSessionAsync("conn-1", "group-1", "session-1");
        Assert.NotNull(tagged);
        Assert.Equal(ZaloDraftEscalationState.ApproverTagged, tagged!.State);

        var first = await store.TryClaimExecutionAsync(tagged, "leader-1", "fingerprint-1");
        var second = await store.TryClaimExecutionAsync(tagged, "leader-2", "fingerprint-1");

        Assert.NotNull(first);
        Assert.Null(second);
        var executing = await store.LoadForSessionAsync("conn-1", "group-1", "session-1");
        Assert.Equal(ZaloDraftEscalationState.Executing, executing!.State);
        Assert.Equal(first, executing.ExecutionToken);
    }

    [Fact]
    public async Task Escalation_state_survives_a_new_db_context()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        string requestId;
        await using (var firstDb = await CreateDbAsync(connection))
        {
            var store = new ZaloDraftEscalationStore(firstDb);
            var created = await store.CreateOrReuseAsync(
                "conn-1", "group-1", "session-1", "Proactive",
                null, null, null,
                "fingerprint-1", ZaloDraftEscalationState.ProactiveSoft,
                DateTimeOffset.UtcNow.AddMinutes(20));
            requestId = created.Id;
            await store.MarkSoftNudgeAsync(created.Id, DateTimeOffset.UtcNow);
        }

        await using var secondDb = await CreateDbAsync(connection);
        var reloaded = await new ZaloDraftEscalationStore(secondDb)
            .LoadForSessionAsync("conn-1", "group-1", "session-1");

        Assert.NotNull(reloaded);
        Assert.Equal(requestId, reloaded!.Id);
        Assert.Equal(ZaloDraftEscalationState.ProactiveSoft, reloaded.State);
        Assert.NotNull(reloaded.SoftNudgeSentAt);
    }

    [Fact]
    public async Task Wrong_fingerprint_cannot_claim_execution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var store = new ZaloDraftEscalationStore(db);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(20);
        var created = await store.CreateOrReuseAsync(
            "conn-1", "group-1", "session-1", "Member",
            "requester", "Requester", "msg-request",
            "fingerprint-1", ZaloDraftEscalationState.AwaitingRequesterConsent,
            expiresAt);
        await store.SetPrimaryApproverAsync(
            created.Id, "leader-1", "prompt-1", DateTimeOffset.UtcNow, expiresAt);
        var tagged = await store.LoadForSessionAsync("conn-1", "group-1", "session-1");

        var token = await store.TryClaimExecutionAsync(tagged!, "leader-1", "fingerprint-2");

        Assert.Null(token);
        var unchanged = await store.LoadForSessionAsync("conn-1", "group-1", "session-1");
        Assert.Equal(ZaloDraftEscalationState.ApproverTagged, unchanged!.State);
    }

    private static async Task<VolleyDraftDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
