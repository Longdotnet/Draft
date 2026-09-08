using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Conversation;

public sealed class ZaloConversationStateV2ConnectionScopeTests
{
    [Fact]
    public async Task Same_group_and_sender_keep_independent_pending_state_per_connection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloConversationStateV2Store(db);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        ZaloConversationStateV2Snapshot first;
        using (ZaloConversationStateScope.Push("connection-a"))
        {
            first = await store.SaveActiveAsync(
                "same-group", "same-user", "DraftReadinessSessionChoice",
                "{}", "[]", "[\"session-a\"]", "message-a", "message-a", expiresAt);
        }

        ZaloConversationStateV2Snapshot second;
        using (ZaloConversationStateScope.Push("connection-b"))
        {
            second = await store.SaveActiveAsync(
                "same-group", "same-user", "MatchBriefSessionChoice",
                "{}", "[]", "[\"session-b\"]", "message-b", "message-b", expiresAt);
        }

        Assert.NotEqual(first.Id, second.Id);

        using (ZaloConversationStateScope.Push("connection-a"))
        {
            var loaded = await store.LoadActiveAsync("same-group", "same-user");
            Assert.NotNull(loaded);
            Assert.Equal("same-group", loaded!.GroupId);
            Assert.Equal("DraftReadinessSessionChoice", loaded.Intent);
            Assert.Contains("session-a", loaded.CandidateEntitiesJson);
            Assert.DoesNotContain("session-b", loaded.CandidateEntitiesJson);
            Assert.Equal(1, await store.CancelAsync("same-group", "same-user"));
        }

        using (ZaloConversationStateScope.Push("connection-b"))
        {
            var loaded = await store.LoadActiveAsync("same-group", "same-user");
            Assert.NotNull(loaded);
            Assert.Equal("MatchBriefSessionChoice", loaded!.Intent);
            Assert.Contains("session-b", loaded.CandidateEntitiesJson);
        }
    }

    [Fact]
    public async Task Scoped_pending_state_survives_new_store_instance_for_same_connection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        string id;
        using (ZaloConversationStateScope.Push("connection-a"))
        {
            id = (await new ZaloConversationStateV2Store(db).SaveActiveAsync(
                "g1", "u1", "DraftReadinessSessionChoice",
                "{}", "[]", "[\"session-a\"]", "m1", "m1",
                DateTimeOffset.UtcNow.AddMinutes(10))).Id;
        }

        using (ZaloConversationStateScope.Push("connection-a"))
        {
            var reloaded = await new ZaloConversationStateV2Store(db).LoadActiveAsync("g1", "u1");
            Assert.NotNull(reloaded);
            Assert.Equal(id, reloaded!.Id);
            Assert.Equal("g1", reloaded.GroupId);
        }
    }

    [Fact]
    public void Scope_key_uses_unambiguous_connection_and_group_framing()
    {
        string first;
        using (ZaloConversationStateScope.Push("ab"))
            first = ZaloConversationStateScope.ScopeGroupId("c");

        string second;
        using (ZaloConversationStateScope.Push("a"))
            second = ZaloConversationStateScope.ScopeGroupId("bc");

        Assert.NotEqual(first, second);
        Assert.StartsWith("scope:", first, StringComparison.Ordinal);
        Assert.StartsWith("scope:", second, StringComparison.Ordinal);
    }
}
