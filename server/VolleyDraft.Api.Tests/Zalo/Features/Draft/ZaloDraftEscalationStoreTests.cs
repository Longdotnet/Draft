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
    public async Task Supersede_before_execution_wins_and_blocks_a_late_claim()
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

        var superseded = await store.TrySupersedeBeforeExecutionAsync(
            "conn-1", "group-1", "session-1");
        var token = await store.TryClaimExecutionAsync(tagged!, "leader-1", "fingerprint-1");

        Assert.True(superseded);
        Assert.Null(token);
        var final = await store.LoadForSessionAsync("conn-1", "group-1", "session-1");
        Assert.Equal(ZaloDraftEscalationState.Superseded, final!.State);
        Assert.Null(final.ExecutionToken);
    }

    [Fact]
    public async Task Execution_claim_wins_before_supersede_and_keeps_its_fence_token()
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

        var token = await store.TryClaimExecutionAsync(tagged!, "leader-1", "fingerprint-1");
        var superseded = await store.TrySupersedeBeforeExecutionAsync(
            "conn-1", "group-1", "session-1");

        Assert.NotNull(token);
        Assert.False(superseded);
        var final = await store.LoadForSessionAsync("conn-1", "group-1", "session-1");
        Assert.Equal(ZaloDraftEscalationState.Executing, final!.State);
        Assert.Equal(token, final.ExecutionToken);
    }

    [Fact]
    public async Task Executing_request_does_not_expire_or_lose_its_token_after_ttl()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var store = new ZaloDraftEscalationStore(db);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(2);
        var created = await store.CreateOrReuseAsync(
            "conn-1", "group-1", "session-1", "Member",
            "requester", "Requester", "msg-request",
            "fingerprint-1", ZaloDraftEscalationState.AwaitingRequesterConsent,
            expiresAt);
        await store.SetPrimaryApproverAsync(
            created.Id, "leader-1", "prompt-1", DateTimeOffset.UtcNow, expiresAt);
        var tagged = await store.LoadForSessionAsync("conn-1", "group-1", "session-1");
        var token = await store.TryClaimExecutionAsync(tagged!, "leader-1", "fingerprint-1");
        Assert.NotNull(token);

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"ZaloDraftEscalationRequests\" SET \"ExpiresAt\" = {0} WHERE \"Id\" = {1};",
            DateTimeOffset.UtcNow.AddMinutes(-1), created.Id);

        var reloaded = await store.LoadForSessionAsync("conn-1", "group-1", "session-1");

        Assert.NotNull(reloaded);
        Assert.Equal(ZaloDraftEscalationState.Executing, reloaded!.State);
        Assert.Equal(token, reloaded.ExecutionToken);
    }

    [Fact]
    public async Task Supersede_is_scoped_to_connection_group_and_session()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var store = new ZaloDraftEscalationStore(db);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(20);
        var first = await store.CreateOrReuseAsync(
            "conn-1", "group-1", "session-1", "Member",
            "requester-1", "Requester 1", "msg-1",
            "fingerprint-1", ZaloDraftEscalationState.AwaitingRequesterConsent,
            expiresAt);
        var second = await store.CreateOrReuseAsync(
            "conn-2", "group-1", "session-2", "Member",
            "requester-2", "Requester 2", "msg-2",
            "fingerprint-2", ZaloDraftEscalationState.AwaitingRequesterConsent,
            expiresAt);
        await store.SetPrimaryApproverAsync(
            first.Id, "leader-1", "prompt-1", DateTimeOffset.UtcNow, expiresAt);
        await store.SetPrimaryApproverAsync(
            second.Id, "leader-2", "prompt-2", DateTimeOffset.UtcNow, expiresAt);

        Assert.True(await store.TrySupersedeBeforeExecutionAsync(
            "conn-1", "group-1", "session-1"));

        var untouched = await store.LoadForSessionAsync("conn-2", "group-1", "session-2");
        Assert.Equal(ZaloDraftEscalationState.ApproverTagged, untouched!.State);
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
