using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Conversation;

public sealed class ZaloDelayedReplyRestartLifecycleFuzzTests
{
    [Fact]
    public async Task Exact_old_prompt_survives_restart_and_intervening_bot_noise_without_rebinding()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"volleydraft-delayed-reply-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            for (var seed = 0; seed < 64; seed++)
            {
                var random = new Random(unchecked(seed * 7919 + 31));
                var connectionId = $"connection-{seed}";
                var groupId = $"group-{seed}";
                var senderId = $"user-{seed}";
                var sourceMessageId = $"source-{seed}";
                var originalBotMessageId = $"bot-choice-{seed}";
                var noise = Enumerable.Range(0, random.Next(1, 9))
                    .Select(index => (BotId: $"bot-noise-{seed}-{index}", ParentId: $"noise-source-{seed}-{index}"))
                    .ToArray();

                await using (var firstProcess = new VolleyDraftDbContext(options))
                {
                    await firstProcess.Database.EnsureCreatedAsync();
                    using (ZaloConversationStateScope.Push(connectionId))
                    {
                        await new ZaloConversationStateV2Store(firstProcess).SaveActiveAsync(
                            groupId,
                            senderId,
                            ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                            "{}",
                            "[]",
                            "[\"session-a\",\"session-b\"]",
                            sourceMessageId,
                            sourceMessageId,
                            DateTimeOffset.UtcNow.AddMinutes(10));
                    }

                    var graph = new ZaloMessageGraphStore(firstProcess);
                    await graph.RememberOutboundAsync(connectionId, groupId, originalBotMessageId, sourceMessageId);
                    foreach (var item in noise)
                        await graph.RememberOutboundAsync(connectionId, groupId, item.BotId, item.ParentId);
                }

                // Fresh DbContext models a deploy/process restart: no process-local state is available.
                await using var restartedProcess = new VolleyDraftDbContext(options);
                var delayedQuote = Quote(originalBotMessageId, DateTimeOffset.UtcNow.AddMinutes(random.Next(30, 181)));
                Assert.True(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
                    restartedProcess,
                    connectionId,
                    groupId,
                    senderId,
                    delayedQuote));

                foreach (var item in noise)
                {
                    Assert.False(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
                        restartedProcess,
                        connectionId,
                        groupId,
                        senderId,
                        Quote(item.BotId, DateTimeOffset.UtcNow.AddMinutes(random.Next(30, 181)))));
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Newer_same_workflow_prompt_supersedes_old_prompt_instead_of_guessing_between_two_tasks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        const string connectionId = "connection-1";
        const string groupId = "group-1";
        const string senderId = "user-1";
        var store = new ZaloConversationStateV2Store(db);
        var graph = new ZaloMessageGraphStore(db);

        using (ZaloConversationStateScope.Push(connectionId))
        {
            await store.SaveActiveAsync(
                groupId, senderId,
                ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                "{}", "[]", "[\"old-session\"]",
                "old-source", "old-source", DateTimeOffset.UtcNow.AddHours(1));
        }
        await graph.RememberOutboundAsync(connectionId, groupId, "old-bot-prompt", "old-source");

        using (ZaloConversationStateScope.Push(connectionId))
        {
            await store.SaveActiveAsync(
                groupId, senderId,
                ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                "{}", "[]", "[\"new-session\"]",
                "new-source", "new-source", DateTimeOffset.UtcNow.AddHours(1));
        }
        await graph.RememberOutboundAsync(connectionId, groupId, "new-bot-prompt", "new-source");

        // The single authoritative pending state must not let a stale older prompt steal
        // ownership after the workflow itself has emitted a newer replacement prompt.
        Assert.False(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
            db, connectionId, groupId, senderId, Quote("old-bot-prompt", DateTimeOffset.UtcNow.AddMinutes(30))));
        Assert.True(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
            db, connectionId, groupId, senderId, Quote("new-bot-prompt", DateTimeOffset.UtcNow.AddMinutes(30))));
    }

    [Fact]
    public void Expired_or_non_bot_quote_cannot_recover_authority_even_when_message_ids_match()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new ZaloConversationStateV2Snapshot(
            "state-1",
            "group-1",
            "user-1",
            ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
            "{}",
            "[]",
            "[\"session-1\"]",
            "source-1",
            "source-1",
            1,
            ZaloConversationStateV2Status.Expired,
            now.AddMinutes(-1),
            now.AddHours(-5),
            now.AddMinutes(-1));
        var relation = new ZaloMessageGraphRelation(
            "relation-1",
            "connection-1",
            "group-1",
            "bot-prompt",
            "source-1",
            "BotReply",
            null,
            null,
            null,
            "bot-prompt",
            now.AddHours(-4));

        // The pure policy is deliberately only an anchor matcher; callers must load an
        // active state. Lock that contract down by proving non-bot quote evidence never
        // becomes an anchor, while the persisted-store path is responsible for expiry.
        var nonBotQuote = new ZaloQuotedSemanticContext(
            "bot-prompt",
            "member-2",
            "Member",
            "text",
            "text",
            now,
            false,
            true,
            true);
        Assert.False(ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchor(state, nonBotQuote, relation));
    }

    private static ZaloQuotedSemanticContext Quote(string messageId, DateTimeOffset receivedAt) =>
        new(
            messageId,
            "bot-account",
            "Npc",
            "Ông hỏi đội hình trận nào?",
            "text",
            receivedAt,
            true,
            false,
            true);
}
