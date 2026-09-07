using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientTeamPreferenceAddressedProvenanceTests
{
    [Fact]
    public async Task Addressed_confirmation_promotes_when_proposal_is_latest_successful_bot_turn()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveProposalAsync("proposal-source", DateTimeOffset.UtcNow.AddSeconds(-10));

        var promoted = await new ZaloAmbientTeamPreferenceHandoff(fixture.Db)
            .TryPromoteExactReplyConfirmationAsync(AddressedConfirmation("confirm-latest"));

        Assert.True(promoted);
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm.ToString(), pending.PendingIntent);
        Assert.StartsWith("TeamPreference:Addressed:confirm-latest", pending.PreviousCommand, StringComparison.Ordinal);
        Assert.Null(await new ZaloConversationStateV2Store(fixture.Db)
            .LoadActiveAsync("g1", "user-long"));
    }

    [Fact]
    public async Task Addressed_confirmation_cannot_revive_proposal_after_newer_unrelated_bot_turn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await fixture.SaveProposalAsync("proposal-source", now.AddSeconds(-20));
        await fixture.SaveSuccessfulBotTurnAsync("newer-question", now.AddSeconds(-2));

        var promoted = await new ZaloAmbientTeamPreferenceHandoff(fixture.Db)
            .TryPromoteExactReplyConfirmationAsync(AddressedConfirmation("confirm-stale"));

        Assert.False(promoted);
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.NotNull(await new ZaloConversationStateV2Store(fixture.Db)
            .LoadActiveAsync("g1", "user-long"));
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Exact_provider_reply_can_confirm_old_proposal_even_after_newer_bot_turn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await fixture.SaveProposalStateAsync("proposal-source");
        await new ZaloMessageGraphStore(fixture.Db).RememberOutboundAsync(
            "conn-1",
            "g1",
            "provider-proposal",
            "proposal-source");
        await fixture.SaveSuccessfulBotTurnAsync("newer-question", now.AddSeconds(-2));

        var promoted = await new ZaloAmbientTeamPreferenceHandoff(fixture.Db)
            .TryPromoteExactReplyConfirmationAsync(ExactReplyConfirmation(
                "confirm-exact-old",
                "provider-proposal"));

        Assert.True(promoted);
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm.ToString(), pending.PendingIntent);
        Assert.StartsWith("TeamPreference:ExactReply:confirm-exact-old", pending.PreviousCommand, StringComparison.Ordinal);
    }

    private static ZaloIncomingMessageEvent AddressedConfirmation(string messageId) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Long",
        content: "xác nhận",
        mentions: [new ZaloBridgeMention("bot-account", -1, 0)],
        mentionedBot: true,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static ZaloIncomingMessageEvent ExactReplyConfirmation(
        string messageId,
        string quotedMessageId) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Long",
        content: "xác nhận",
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        quote: new ZaloBridgeMessageQuote(
            quotedMessageId,
            "bot-account",
            "Volley Bot",
            "Long + To An ở T6. Reply tin này và xác nhận để áp dụng.",
            "text",
            DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(),
            null));

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
                Email = $"handoff-provenance-{Guid.NewGuid():n}@example.test",
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
                Name = "T6",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddDays(1),
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
                IsPresent = true
            });
            session.Players.Add(new SessionPlayer
            {
                Id = "session-t6-toan",
                SessionId = session.Id,
                PlayerProfileId = toAnProfile.Id,
                PlayerProfile = toAnProfile,
                DisplayName = "To An",
                IsPresent = true
            });

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.AddRange(longProfile, toAnProfile);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async Task SaveProposalAsync(string sourceMessageId, DateTimeOffset botReplySentAt)
        {
            await SaveProposalStateAsync(sourceMessageId);
            await SaveSuccessfulBotTurnAsync(sourceMessageId, botReplySentAt);
        }

        public async Task SaveSuccessfulBotTurnAsync(string messageId, DateTimeOffset botReplySentAt)
        {
            Db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                ZaloConnectionId = "conn-1",
                GroupId = "g1",
                MessageId = messageId,
                SenderId = "user-long",
                SenderName = "Long",
                Content = messageId == "proposal-source" ? "muốn chung team với To An" : "lịch trận sao rồi",
                MessageType = "chat",
                ObservationSource = "Realtime",
                IsFromBot = false,
                SentAt = botReplySentAt.AddSeconds(-1),
                ReceivedAt = botReplySentAt.AddSeconds(-1),
                FirstObservedAt = botReplySentAt.AddSeconds(-1),
                LastObservedAt = botReplySentAt.AddSeconds(-1),
                BotReplySentAt = botReplySentAt,
                BotReplyText = "bot reply"
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task SaveProposalStateAsync(string sourceMessageId)
        {
            var collected = JsonSerializer.Serialize(new
            {
                requesterZaloUserId = "user-long",
                requesterDisplayName = "Long",
                partnerZaloUserId = "user-toan",
                partnerDisplayName = "To An",
                sessionId = "session-t6",
                sessionName = "T6",
                metadata = new
                {
                    speechAct = "proposal",
                    writeAuthorized = false,
                    domain = "TeamPreference"
                }
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
