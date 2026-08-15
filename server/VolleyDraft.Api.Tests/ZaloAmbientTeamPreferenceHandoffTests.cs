using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientTeamPreferenceHandoffTests
{
    [Fact]
    public async Task Exact_reply_confirmation_promotes_ready_proposal_into_legacy_confirm_envelope()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync("user-long", "proposal-source", "provider-proposal");

        var incoming = Confirmation(
            senderId: "user-long",
            messageId: "confirm-1",
            quotedMessageId: "provider-proposal");

        var result = await new ZaloMemoryV2Service(fixture.Db)
            .ProcessAsync("g1", incoming, incoming.Content);

        Assert.False(result.Handled);
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm.ToString(), pending.PendingIntent);
        Assert.StartsWith("TeamPreference:ExactReply:confirm-1", pending.PreviousCommand, StringComparison.Ordinal);
        Assert.InRange(pending.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(31));

        using var payload = JsonDocument.Parse(pending.PendingPayloadJson);
        Assert.Equal("session-t6", payload.RootElement.GetProperty("SessionId").GetString());
        Assert.True(payload.RootElement.GetProperty("SelfService").GetBoolean());
        var names = payload.RootElement.GetProperty("Plan").GetProperty("PlayerNames")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("Long", names);
        Assert.Contains("To An", names);

        Assert.Null(await new ZaloConversationStateV2Store(fixture.Db)
            .LoadActiveAsync("g1", "user-long"));
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Confirmation_replying_to_another_bot_message_does_not_promote()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync("user-long", "proposal-source", "provider-proposal");

        var promoted = await new ZaloAmbientTeamPreferenceHandoff(fixture.Db)
            .TryPromoteExactReplyConfirmationAsync(Confirmation(
                senderId: "user-long",
                messageId: "confirm-wrong",
                quotedMessageId: "provider-other"));

        Assert.False(promoted);
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.NotNull(await new ZaloConversationStateV2Store(fixture.Db)
            .LoadActiveAsync("g1", "user-long"));
    }

    [Fact]
    public async Task Unquoted_confirmation_does_not_promote()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync("user-long", "proposal-source", "provider-proposal");

        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "confirm-unquoted",
            senderId: "user-long",
            senderName: "Long",
            content: "xác nhận",
            mentions: [new ZaloBridgeMention("bot-account", -1, 0)],
            mentionedBot: true,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.False(await new ZaloAmbientTeamPreferenceHandoff(fixture.Db)
            .TryPromoteExactReplyConfirmationAsync(incoming));
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Another_sender_cannot_consume_someone_elses_proposal()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync("user-long", "proposal-source", "provider-proposal");

        Assert.False(await new ZaloAmbientTeamPreferenceHandoff(fixture.Db)
            .TryPromoteExactReplyConfirmationAsync(Confirmation(
                senderId: "user-other",
                messageId: "confirm-other",
                quotedMessageId: "provider-proposal")));
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.NotNull(await new ZaloConversationStateV2Store(fixture.Db)
            .LoadActiveAsync("g1", "user-long"));
    }

    [Fact]
    public async Task Roster_change_before_confirmation_prevents_handoff()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync("user-long", "proposal-source", "provider-proposal");

        var partner = await fixture.Db.SessionPlayers.SingleAsync(item => item.Id == "session-t6-toan");
        partner.IsPresent = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        Assert.False(await new ZaloAmbientTeamPreferenceHandoff(fixture.Db)
            .TryPromoteExactReplyConfirmationAsync(Confirmation(
                senderId: "user-long",
                messageId: "confirm-roster-changed",
                quotedMessageId: "provider-proposal")));
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Live_unrelated_legacy_pending_is_not_overwritten()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveReadyProposalAsync("user-long", "proposal-source", "provider-proposal");
        fixture.Db.ZaloBotConversationStates.Add(new ZaloBotConversationState
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            SenderZaloUserId = "user-long",
            PendingIntent = ZaloBotIntent.ShareSlotConfirm.ToString(),
            PendingPayloadJson = "{}",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
        });
        await fixture.Db.SaveChangesAsync();

        Assert.False(await new ZaloAmbientTeamPreferenceHandoff(fixture.Db)
            .TryPromoteExactReplyConfirmationAsync(Confirmation(
                senderId: "user-long",
                messageId: "confirm-conflict",
                quotedMessageId: "provider-proposal")));
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloBotIntent.ShareSlotConfirm.ToString(), pending.PendingIntent);
    }

    private static ZaloIncomingMessageEvent Confirmation(
        string senderId,
        string messageId,
        string quotedMessageId) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: senderId,
        senderName: senderId == "user-long" ? "Long" : "Other",
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
                Email = $"handoff-{Guid.NewGuid():n}@example.test",
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

        public async Task SaveReadyProposalAsync(
            string senderId,
            string sourceMessageId,
            string providerReplyMessageId)
        {
            var collected = JsonSerializer.Serialize(new
            {
                requesterZaloUserId = senderId,
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
                senderId,
                ZaloAmbientTeamPreferenceHandoff.ProposalIntent,
                collected,
                "[]",
                "[]",
                sourceMessageId,
                sourceMessageId,
                DateTimeOffset.UtcNow.AddMinutes(5));
            await new ZaloMessageGraphStore(Db).RememberOutboundAsync(
                "conn-1",
                "g1",
                providerReplyMessageId,
                sourceMessageId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
