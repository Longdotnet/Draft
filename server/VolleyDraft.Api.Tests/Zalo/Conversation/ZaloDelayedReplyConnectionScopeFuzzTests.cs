using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Conversation;

public sealed class ZaloDelayedReplyConnectionScopeFuzzTests
{
    [Fact]
    public async Task Delayed_quoted_recovery_uses_the_exact_connection_scoped_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var stateStore = new ZaloConversationStateV2Store(db);
        var graph = new ZaloMessageGraphStore(db);
        const string groupId = "shared-group";
        const string senderId = "shared-user";

        for (var seed = 0; seed < 128; seed++)
        {
            var connectionA = $"connection-a-{seed}";
            var connectionB = $"connection-b-{seed}";
            var sourceA = $"source-a-{seed}";
            var sourceB = $"source-b-{seed}";
            var botReplyA = $"bot-a-{seed}";
            var botReplyB = $"bot-b-{seed}";

            using (ZaloConversationStateScope.Push(connectionA))
            {
                await stateStore.SaveActiveAsync(
                    groupId,
                    senderId,
                    ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                    "{}",
                    "[]",
                    "[\"session-a\"]",
                    sourceA,
                    sourceA,
                    DateTimeOffset.UtcNow.AddHours(2));
            }

            using (ZaloConversationStateScope.Push(connectionB))
            {
                await stateStore.SaveActiveAsync(
                    groupId,
                    senderId,
                    ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                    "{}",
                    "[]",
                    "[\"session-b\"]",
                    sourceB,
                    sourceB,
                    DateTimeOffset.UtcNow.AddHours(2));
            }

            await graph.RememberOutboundAsync(connectionA, groupId, botReplyA, sourceA);
            await graph.RememberOutboundAsync(connectionB, groupId, botReplyB, sourceB);

            var quoteA = Quote(botReplyA, seed);
            var quoteB = Quote(botReplyB, seed);

            Assert.True(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
                db, connectionA, groupId, senderId, quoteA));
            Assert.True(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
                db, connectionB, groupId, senderId, quoteB));

            Assert.False(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
                db, connectionA, groupId, senderId, quoteB));
            Assert.False(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
                db, connectionB, groupId, senderId, quoteA));
        }
    }

    [Fact]
    public async Task Legacy_unscoped_state_cannot_authorize_a_scoped_delayed_reply()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        const string connectionId = "connection-live";
        const string groupId = "group-1";
        const string senderId = "user-1";
        const string scopedSource = "scoped-source";
        const string legacySource = "legacy-source";
        const string legacyBotReply = "legacy-bot-reply";
        var store = new ZaloConversationStateV2Store(db);

        using (ZaloConversationStateScope.Push(connectionId))
        {
            await store.SaveActiveAsync(
                groupId,
                senderId,
                ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                "{}",
                "[]",
                "[\"session-live\"]",
                scopedSource,
                scopedSource,
                DateTimeOffset.UtcNow.AddHours(2));
        }

        using (ZaloConversationStateScope.Push(null))
        {
            await store.SaveActiveAsync(
                groupId,
                senderId,
                ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                "{}",
                "[]",
                "[\"session-legacy\"]",
                legacySource,
                legacySource,
                DateTimeOffset.UtcNow.AddHours(2));
        }

        await new ZaloMessageGraphStore(db)
            .RememberOutboundAsync(connectionId, groupId, legacyBotReply, legacySource);

        Assert.False(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
            db,
            connectionId,
            groupId,
            senderId,
            Quote(legacyBotReply, 0)));
    }

    private static ZaloQuotedSemanticContext Quote(string messageId, int seed) =>
        new(
            messageId,
            "bot-account",
            "Npc",
            "Ông hỏi đội hình trận nào?",
            "text",
            DateTimeOffset.UtcNow.AddMinutes(30 + seed % 90),
            true,
            false,
            true);
}
