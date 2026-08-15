using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientConversationLeaseTests
{
    private static readonly ZaloAmbientSettings Settings = new(
        Enabled: true,
        ShadowMode: false,
        WouldReplyThreshold: 60,
        RecentWindowMinutes: 5,
        MaxRecentMessages: 40,
        BotCooldownSeconds: 2,
        BusyGroupMessagesPerTwoMinutes: 8);

    [Fact]
    public async Task Lease_is_scoped_to_same_connection_group_and_sender()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = new ZaloAmbientConversationLeaseResolver(fixture.Db);

        Assert.True(await resolver.IsActiveAsync("conn-1", "g1", "user-long", 180));
        Assert.False(await resolver.IsActiveAsync("conn-1", "g1", "user-nam", 180));
        Assert.False(await resolver.IsActiveAsync("conn-1", "g2", "user-long", 180));
    }

    [Fact]
    public async Task Social_ai_reply_opens_the_same_sender_conversation_lease()
    {
        await using var fixture = await Fixture.CreateAsync(replyOutcome: "ambient_social_sent");
        var resolver = new ZaloAmbientConversationLeaseResolver(fixture.Db);

        Assert.True(await resolver.IsActiveAsync("conn-1", "g1", "user-long", 180));
        Assert.False(await resolver.IsActiveAsync("conn-1", "g1", "user-nam", 180));
    }

    [Fact]
    public async Task Expired_reply_does_not_keep_the_lease_open()
    {
        await using var fixture = await Fixture.CreateAsync(replyAge: TimeSpan.FromMinutes(4));
        var resolver = new ZaloAmbientConversationLeaseResolver(fixture.Db);

        Assert.False(await resolver.IsActiveAsync("conn-1", "g1", "user-long", 180));
    }

    [Fact]
    public void Same_sender_lease_understands_elliptical_slot_followup_and_bypasses_cooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("follow-slot", "CN còn nhiều slot"),
            Situation(now.AddSeconds(-1)),
            Settings,
            now,
            hasActiveLease: true);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.Equal(ZaloBotIntent.MissingSlots.ToString(), decision.Intent);
        Assert.Contains("active_conversation_lease", decision.Signals);
        Assert.Contains("lease_inferred_fact_intent", decision.Signals);
    }

    [Fact]
    public void Same_sender_lease_turns_plain_social_followup_into_ai_candidate_without_bot_keyword()
    {
        var now = DateTimeOffset.UtcNow;
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("follow-social", "nói chuyện tí coi"),
            Situation(now.AddSeconds(-1)),
            Settings,
            now,
            hasActiveLease: true);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Social, decision.Kind);
        Assert.Contains(decision.Intent, new[]
        {
            ZaloBotIntent.Unknown.ToString(),
            ZaloBotIntent.GeneralChat.ToString()
        });
        Assert.Contains("active_conversation_lease", decision.Signals);
        Assert.Contains("lease_social_followup", decision.Signals);
        Assert.Contains("bot_cooldown", decision.Signals);
        Assert.True(decision.Score >= 90);
    }

    [Fact]
    public void Same_sender_lease_does_not_steal_a_human_vocative_thread()
    {
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("human-follow", "Nam ơi đi ăn không"),
            Situation(DateTimeOffset.UtcNow.AddSeconds(-1)),
            Settings,
            DateTimeOffset.UtcNow,
            hasActiveLease: true);

        Assert.False(decision.WouldReply);
        Assert.DoesNotContain("lease_social_followup", decision.Signals);
    }

    [Fact]
    public void Lease_can_turn_team_preference_into_read_only_advisor_but_not_generic_mutation()
    {
        var preference = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("follow-pref", "xếp tui với To An chung team đi"),
            Situation(),
            Settings,
            DateTimeOffset.UtcNow,
            hasActiveLease: true);
        var mutation = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("follow-draft", "draft lại team T6 đi"),
            Situation(),
            Settings,
            DateTimeOffset.UtcNow,
            hasActiveLease: true);

        Assert.True(preference.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Fact, preference.Kind);
        Assert.Equal(ZaloBotIntent.TeamPreference.ToString(), preference.Intent);
        Assert.Contains("active_conversation_lease", preference.Signals);

        Assert.False(mutation.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Action, mutation.Kind);
        Assert.Contains("action_requires_address", mutation.Signals);
    }

    private static ZaloIncomingMessageEvent Incoming(string messageId, string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Long",
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static ZaloAmbientGroupSituation Situation(DateTimeOffset? lastBot = null) => new(
        RecentMessageCount: 3,
        RecentTwoMinuteMessageCount: 3,
        DistinctParticipantCount: 1,
        RecentBotMessageCount: lastBot is null ? 0 : 1,
        LastBotMessageAt: lastBot,
        RecentMessageIds: ["wake", "provider-wake", "current"]);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync(
            TimeSpan? replyAge = null,
            string replyOutcome = "ambient_sent")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"lease-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test"
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                Id = "wake-row",
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                GroupId = "g1",
                MessageId = "wake-message",
                SenderId = "user-long",
                SenderName = "Long",
                Content = "Bot ơi",
                SentAt = DateTimeOffset.UtcNow.Subtract(replyAge ?? TimeSpan.FromSeconds(10)),
                BotReplySentAt = DateTimeOffset.UtcNow.Subtract(replyAge ?? TimeSpan.FromSeconds(10)),
                ReplyOutcome = replyOutcome,
                IsFromBot = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
