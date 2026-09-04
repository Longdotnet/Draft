using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloNaturalConfirmationAndAiGuardTests
{
    [Theory]
    [InlineData("__NO_RE")]
    [InlineData("__NO_REPLY")]
    [InlineData("__NO_REPLY__")]
    [InlineData("__no_re")]
    public void Truncated_no_reply_sentinels_are_never_user_visible(string candidate)
    {
        Assert.False(ZaloAmbientSocialResponder.IsSafeCandidate(candidate, 180));
        Assert.True(ZaloAmbientSocialResponder.LooksLikeNoReplySentinel(candidate));
    }

    [Fact]
    public async Task Capability_question_is_deterministic_and_complete_without_ai_configuration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var responder = new ZaloAmbientSocialResponder(
            db,
            new ConfigurationBuilder().Build(),
            NullLogger<ZaloOverbookService>.Instance);
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "cap-1",
            senderId: "user-long",
            senderName: "Long",
            content: "Bot đang có khả năng gì",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var situation = new ZaloAmbientGroupSituation(
            1, 1, 1, 0, null, ["cap-1"]);
        var decision = new ZaloAmbientParticipationDecision(
            WouldReply: true,
            Score: 95,
            Kind: ZaloAmbientParticipationKind.Social,
            Intent: ZaloBotIntent.Unknown.ToString(),
            IntentConfidence: .98,
            Signals: [],
            Situation: situation);
        var settings = new ZaloAmbientSocialPilotSettings(
            Enabled: true,
            SendEnabled: true,
            MinimumScore: 90,
            MaxContextMessages: 8,
            MaxReplyChars: 180);

        var reply = await responder.TryBuildAsync(
            "conn-1", "g1", incoming, decision, settings);

        Assert.NotNull(reply);
        Assert.Equal("deterministic_capability_overview", reply!.AddressReason);
        Assert.Contains("vote poll", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xác nhận", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(reply.Text.Length <= 180);
        Assert.DoesNotContain("đăng ký chơi", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Strong_plain_confirmation_can_promote_only_the_latest_recent_team_proposal()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync("proposal-source");

        var continuation = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "Xác nhận nha");

        Assert.NotNull(continuation);
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm, continuation!.PendingIntent);
        Assert.False(continuation.IsCancellation);

        fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            Id = "later-message-row",
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            MessageId = "later-message",
            SenderId = "user-long",
            SenderName = "Long",
            Content = "trong gì cơ",
            IsFromBot = false,
            SentAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            BotReplySentAt = DateTimeOffset.UtcNow.AddSeconds(1),
            ReplyOutcome = "ambient_social_sent"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var stale = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", "xác nhận");

        Assert.Null(stale);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("được")]
    [InlineData("chốt")]
    public async Task Generic_ack_still_cannot_promote_team_proposal(string content)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync("proposal-source");

        var continuation = await new ZaloAmbientLeasePendingContinuationPolicy(fixture.Db)
            .TryResolveAsync("conn-1", "g1", "user-long", content);

        Assert.Null(continuation);
    }

    [Fact]
    public async Task Promoted_address_confirmation_revalidates_and_creates_one_shot_legacy_envelope()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync("proposal-source");
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "confirm-natural",
            senderId: "user-long",
            senderName: "Long",
            content: "Xác nhận nha",
            mentions: [new ZaloBridgeMention("bot-account", 0, 0)],
            mentionedBot: true,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var promoted = await new ZaloAmbientTeamPreferenceHandoff(fixture.Db)
            .TryPromoteExactReplyConfirmationAsync(incoming);

        Assert.True(promoted);
        var legacy = await fixture.Db.ZaloBotConversationStates
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm.ToString(), legacy.PendingIntent);
        Assert.Contains("session-t6", legacy.PendingPayloadJson);
        Assert.Null(await new ZaloConversationStateV2Store(fixture.Db)
            .LoadActiveAsync("g1", "user-long"));
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
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
                Email = $"natural-confirm-{Guid.NewGuid():n}@example.test",
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
            var session = new MatchSession
            {
                Id = "session-t6",
                AdminUserId = admin.Id,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                Name = "CN",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = ZaloTestDates.Next(DayOfWeek.Sunday),
                TeamCount = 3,
                TeamSize = 6
            };
            session.Players.Add(new SessionPlayer
            {
                Id = "session-t6-long",
                SessionId = session.Id,
                PlayerProfileId = longProfile.Id,
                PlayerProfile = longProfile,
                DisplayName = "Long",
                Role = PlayerRole.Attack,
                Level = PlayerLevel.Average,
                Gender = PlayerGender.Male,
                Score = 2,
                IsPresent = true,
                IsCaptainEligible = true
            });
            session.Players.Add(new SessionPlayer
            {
                Id = "session-t6-toan",
                SessionId = session.Id,
                PlayerProfileId = toAnProfile.Id,
                PlayerProfile = toAnProfile,
                DisplayName = "To An",
                Role = PlayerRole.Defense,
                Level = PlayerLevel.Average,
                Gender = PlayerGender.Female,
                Score = 2,
                IsPresent = true,
                IsCaptainEligible = true
            });

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.AddRange(longProfile, toAnProfile);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async Task SaveReadyProposalAsync(string sourceMessageId)
        {
            var now = DateTimeOffset.UtcNow;
            Db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                Id = "proposal-source-row",
                ZaloConnectionId = "conn-1",
                GroupId = "g1",
                MessageId = sourceMessageId,
                SenderId = "user-long",
                SenderName = "Long",
                Content = "xếp Thanh Long chung team To An đc ko",
                IsFromBot = false,
                SentAt = now.AddSeconds(-5),
                ReceivedAt = now.AddSeconds(-5),
                BotReplySentAt = now.AddSeconds(-4),
                ReplyOutcome = "ambient_sent",
                SelectedIntent = ZaloBotIntent.TeamPreference.ToString()
            });
            await Db.SaveChangesAsync();

            var collected = JsonSerializer.Serialize(new
            {
                requesterZaloUserId = "user-long",
                requesterDisplayName = "Long",
                partnerZaloUserId = "user-toan",
                partnerDisplayName = "To An",
                sessionId = "session-t6",
                sessionName = "CN"
            });
            await new ZaloConversationStateV2Store(Db).SaveActiveAsync(
                "g1",
                "user-long",
                ZaloAmbientTeamPreferenceHandoff.ProposalIntent,
                collected,
                "[]",
                "[]",
                sourceMessageId,
                sourceMessageId,
                DateTimeOffset.UtcNow.AddMinutes(5));
            Db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
