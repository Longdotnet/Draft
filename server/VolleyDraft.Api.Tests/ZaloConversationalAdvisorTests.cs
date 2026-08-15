using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloConversationalAdvisorTests
{
    private static readonly ZaloAmbientSettings Settings = new(
        Enabled: true,
        ShadowMode: false,
        WouldReplyThreshold: 65,
        RecentWindowMinutes: 5,
        MaxRecentMessages: 40,
        BotCooldownSeconds: 20,
        BusyGroupMessagesPerTwoMinutes: 8);

    [Fact]
    public void Natural_question_about_the_bot_becomes_capability_fact()
    {
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("capability", "con bot này sài sao vậy mn"),
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.Equal(ZaloBotIntent.Help.ToString(), decision.Intent);
        Assert.True(decision.Score >= 85);
        Assert.Contains("bot_capability_inquiry", decision.Signals);
    }

    [Fact]
    public void Exact_team_feasibility_shorthand_is_a_read_only_advisor_turn()
    {
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("advisor", "tui muốn choi chung với To An thì bạn xếp đc ko"),
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloAmbientParticipationKind.Fact, decision.Kind);
        Assert.Equal(ZaloBotIntent.TeamPreference.ToString(), decision.Intent);
        Assert.True(decision.Score >= 85);
        Assert.Contains("team_preference_bot_question_shorthand", decision.Signals);
    }

    [Fact]
    public void Conversation_addressed_to_another_member_does_not_wake_the_bot()
    {
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("human-thread", "Nam ơi bạn chơi chung với To An không"),
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        Assert.False(decision.WouldReply);
        Assert.NotEqual(ZaloBotIntent.TeamPreference.ToString(), decision.Intent);
    }

    [Fact]
    public void Untagged_imperative_team_mutation_stays_blocked()
    {
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            Incoming("mutation", "xếp tui với To An chung team đi"),
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        Assert.Equal(ZaloAmbientParticipationKind.Action, decision.Kind);
        Assert.False(decision.WouldReply);
        Assert.Contains("action_requires_address", decision.Signals);
    }

    [Fact]
    public async Task Capability_responder_explains_real_bot_scope_without_domain_write()
    {
        await using var fixture = await Fixture.CreateAsync(withSessions: false);
        var incoming = Incoming("capability-live", "con bot này sài sao vậy mn");
        var decision = ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        var reply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, minimumScore: 85);

        Assert.NotNull(reply);
        Assert.Equal(ZaloBotIntent.Help, reply!.Intent);
        Assert.Contains("hỗ trợ kèo", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chơi chung team", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Team_feasibility_resolves_people_then_keeps_session_followup_as_read_only_proposal()
    {
        await using var fixture = await Fixture.CreateAsync(withSessions: true);
        var first = Incoming("team-first", "tui muốn choi chung với To An thì bạn xếp đc ko");
        var firstDecision = ZaloAmbientParticipationEngine.Evaluate(
            first,
            QuietSituation(),
            Settings,
            DateTimeOffset.UtcNow);

        var firstReply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", first, firstDecision, minimumScore: 85);

        Assert.NotNull(firstReply);
        Assert.Contains("Long", firstReply!.Text);
        Assert.Contains("To An", firstReply.Text);
        Assert.Contains("T6", firstReply.Text);
        Assert.Contains("CN", firstReply.Text);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());

        var stateStore = new ZaloConversationStateV2Store(fixture.Db);
        var pending = await stateStore.LoadActiveAsync("g1", "user-long");
        Assert.NotNull(pending);
        Assert.Equal("AmbientTeamPreferenceProposal", pending!.Intent);
        Assert.Contains("sessionReference", pending.MissingArgumentsJson);

        var followUp = Incoming("team-follow-up", "T6");
        var followDecision = ZaloAmbientParticipationEngine.Evaluate(
            followUp,
            QuietSituation(lastBotMessageAt: DateTimeOffset.UtcNow.AddSeconds(-2)),
            Settings,
            DateTimeOffset.UtcNow,
            hasActiveProposal: true);

        Assert.True(followDecision.WouldReply);
        Assert.Equal(ZaloBotIntent.TeamPreference.ToString(), followDecision.Intent);

        var followReply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", followUp, followDecision, minimumScore: 85);

        Assert.NotNull(followReply);
        Assert.Equal("session-t6", followReply!.SessionId);
        Assert.Contains("Long", followReply.Text);
        Assert.Contains("To An", followReply.Text);
        Assert.Contains("T6", followReply.Text);
        Assert.Contains("xác nhận", followReply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());

        var updated = await stateStore.LoadActiveAsync("g1", "user-long");
        Assert.NotNull(updated);
        Assert.DoesNotContain("sessionReference", updated!.MissingArgumentsJson);
        Assert.Contains("session-t6", updated.CollectedArgumentsJson);
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

    private static ZaloAmbientGroupSituation QuietSituation(DateTimeOffset? lastBotMessageAt = null) => new(
        RecentMessageCount: 1,
        RecentTwoMinuteMessageCount: 1,
        DistinctParticipantCount: 1,
        RecentBotMessageCount: lastBotMessageAt is null ? 0 : 1,
        LastBotMessageAt: lastBotMessageAt,
        RecentMessageIds: ["context-1"]);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync(bool withSessions)
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
                Email = $"advisor-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Volley Bot",
                EncryptedCredentials = "test"
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);

            if (withSessions)
            {
                var longProfile = new PlayerProfile
                {
                    Id = "profile-long",
                    ZaloUserId = "user-long",
                    DisplayName = "Long"
                };
                var toAnProfile = new PlayerProfile
                {
                    Id = "profile-toan",
                    ZaloUserId = "user-toan",
                    DisplayName = "To An"
                };
                db.PlayerProfiles.AddRange(longProfile, toAnProfile);
                db.ZaloGroupMembers.AddRange(
                    new ZaloGroupMember
                    {
                        Id = "member-long",
                        ZaloConnectionId = zalo.Id,
                        ZaloConnection = zalo,
                        GroupId = "g1",
                        ZaloUserId = "user-long",
                        DisplayName = "Long",
                        IsCurrentMember = true
                    },
                    new ZaloGroupMember
                    {
                        Id = "member-toan",
                        ZaloConnectionId = zalo.Id,
                        ZaloConnection = zalo,
                        GroupId = "g1",
                        ZaloUserId = "user-toan",
                        DisplayName = "To An",
                        IsCurrentMember = true
                    });

                db.MatchSessions.AddRange(
                    Session("session-t6", "T6", DateTimeOffset.UtcNow.AddDays(1), zalo, longProfile, toAnProfile),
                    Session("session-cn", "CN", DateTimeOffset.UtcNow.AddDays(2), zalo, longProfile, toAnProfile));
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        private static MatchSession Session(
            string id,
            string name,
            DateTimeOffset start,
            ZaloConnection connection,
            PlayerProfile longProfile,
            PlayerProfile toAnProfile)
        {
            var session = new MatchSession
            {
                Id = id,
                AdminUserId = "admin-1",
                ZaloConnectionId = connection.Id,
                ZaloConnection = connection,
                ZaloGroupId = "g1",
                Name = name,
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = start,
                TeamCount = 3,
                TeamSize = 6
            };
            session.Players.Add(new SessionPlayer
            {
                Id = $"{id}-long",
                SessionId = id,
                PlayerProfileId = longProfile.Id,
                PlayerProfile = longProfile,
                DisplayName = "Long",
                IsPresent = true
            });
            session.Players.Add(new SessionPlayer
            {
                Id = $"{id}-toan",
                SessionId = id,
                PlayerProfileId = toAnProfile.Id,
                PlayerProfile = toAnProfile,
                DisplayName = "To An",
                IsPresent = true
            });
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
