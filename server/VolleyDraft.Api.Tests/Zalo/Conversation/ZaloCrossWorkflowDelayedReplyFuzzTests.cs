using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using VolleyDraft.Api.Services.Zalo.Conversation;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Conversation;

public sealed class ZaloCrossWorkflowDelayedReplyFuzzTests
{
    [Fact]
    public async Task Exact_old_draft_quote_cannot_steal_authority_after_another_v2_workflow_supersedes_it()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"volleydraft-cross-workflow-reply-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var replacementIntents = new[]
            {
                "TeamPreferenceProposal",
                "GuestClarification",
                "MemberConceptClarification",
                "ReminderClarification"
            };

            for (var seed = 0; seed < 128; seed++)
            {
                var random = new Random(unchecked(seed * 104729 + 97));
                var connectionId = $"connection-{seed}";
                var groupId = $"group-{seed}";
                var senderId = $"user-{seed}";
                var oldSourceId = $"draft-source-{seed}";
                var oldPromptId = $"draft-prompt-{seed}";
                var replacementSourceId = $"replacement-source-{seed}";
                var replacementPromptId = $"replacement-prompt-{seed}";
                var replacementIntent = replacementIntents[random.Next(replacementIntents.Length)];

                await using (var firstProcess = new VolleyDraftDbContext(options))
                {
                    await firstProcess.Database.EnsureCreatedAsync();
                    var store = new ZaloConversationStateV2Store(firstProcess);
                    var graph = new ZaloMessageGraphStore(firstProcess);

                    using (ZaloConversationStateScope.Push(connectionId))
                    {
                        await store.SaveActiveAsync(
                            groupId,
                            senderId,
                            ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                            "{}",
                            "[]",
                            "[\"session-a\",\"session-b\"]",
                            oldSourceId,
                            oldSourceId,
                            DateTimeOffset.UtcNow.AddHours(2));
                    }
                    await graph.RememberOutboundAsync(connectionId, groupId, oldPromptId, oldSourceId);

                    // Mutate realistic intervening bot traffic before another durable V2
                    // workflow becomes authoritative for the same connection/group/user.
                    foreach (var noiseIndex in Enumerable.Range(0, random.Next(0, 7)))
                    {
                        await graph.RememberOutboundAsync(
                            connectionId,
                            groupId,
                            $"noise-{seed}-{noiseIndex}",
                            $"noise-source-{seed}-{noiseIndex}");
                    }

                    using (ZaloConversationStateScope.Push(connectionId))
                    {
                        await store.SaveActiveAsync(
                            groupId,
                            senderId,
                            replacementIntent,
                            "{}",
                            "[]",
                            "[]",
                            replacementSourceId,
                            replacementSourceId,
                            DateTimeOffset.UtcNow.AddHours(2));
                    }
                    await graph.RememberOutboundAsync(connectionId, groupId, replacementPromptId, replacementSourceId);
                }

                // Fresh context models deploy/restart. An exact quote to the old draft
                // prompt is strong addressing evidence, but it must not resurrect a
                // superseded task after another V2 workflow owns the authoritative row.
                await using var restartedProcess = new VolleyDraftDbContext(options);
                var delayedOldQuote = Quote(oldPromptId, DateTimeOffset.UtcNow.AddMinutes(random.Next(30, 181)));
                Assert.False(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
                    restartedProcess,
                    connectionId,
                    groupId,
                    senderId,
                    delayedOldQuote));

                // The replacement prompt also cannot be misclassified as a draft anchor.
                Assert.False(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
                    restartedProcess,
                    connectionId,
                    groupId,
                    senderId,
                    Quote(replacementPromptId, DateTimeOffset.UtcNow.AddMinutes(random.Next(1, 30)))));

                using (ZaloConversationStateScope.Push(connectionId))
                {
                    var current = await new ZaloConversationStateV2Store(restartedProcess)
                        .LoadActiveAsync(groupId, senderId);
                    Assert.NotNull(current);
                    Assert.Equal(replacementIntent, current.Intent);
                    Assert.Equal(replacementSourceId, current.SourceMessageId);
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
    public async Task Cross_sender_and_cross_group_quotes_never_recover_another_users_draft_task()
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
        const string senderId = "user-a";
        const string sourceId = "source-a";
        const string promptId = "prompt-a";

        using (ZaloConversationStateScope.Push(connectionId))
        {
            await new ZaloConversationStateV2Store(db).SaveActiveAsync(
                groupId,
                senderId,
                ZaloConversationLifetimePolicy.DraftReadinessSessionChoiceIntent,
                "{}",
                "[]",
                "[\"session-a\"]",
                sourceId,
                sourceId,
                DateTimeOffset.UtcNow.AddHours(1));
        }
        await new ZaloMessageGraphStore(db).RememberOutboundAsync(connectionId, groupId, promptId, sourceId);

        var quote = Quote(promptId, DateTimeOffset.UtcNow.AddMinutes(45));
        Assert.False(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
            db, connectionId, groupId, "user-b", quote));
        Assert.False(await ZaloQuotedTaskRecoveryPolicy.IsExactDraftSessionChoiceAnchorAsync(
            db, connectionId, "group-2", senderId, quote));
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
