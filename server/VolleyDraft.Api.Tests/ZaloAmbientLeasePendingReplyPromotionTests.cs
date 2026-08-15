using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientLeasePendingReplyPromotionTests
{
    [Fact]
    public async Task Exact_reply_to_bound_preview_promotes_explicit_confirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = fixture.Incoming("confirm-1", "xác nhận", "provider-preview-1", "user-long");

        Assert.True(ZaloAmbientLeasePendingReplyPromotion.IsExplicitPendingReply(incoming.Content));
        var quote = ZaloQuotedContextResolver.Resolve(incoming, incoming.Content);
        Assert.True(quote.RepliesToBot);
        Assert.Equal("provider-preview-1", quote.MessageId);

        var pending = await fixture.Db.ZaloBotConversationStates
            .AsNoTracking()
            .SingleAsync(item => item.Id == "pending-1");
        Assert.Equal("user-long", pending.SenderZaloUserId);
        Assert.Equal(ZaloBotIntent.AutoDraftConfirm.ToString(), pending.PendingIntent);
        Assert.True(pending.ExpiresAt > DateTimeOffset.UtcNow);

        var source = await fixture.Db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item => item.Id == "source-row-1");
        Assert.Equal(ZaloBotIntent.AutoDraft.ToString(), source.SelectedIntent);
        Assert.NotNull(source.BotReplySentAt);

        var providerReplyId = await new ZaloMessageGraphQuery(fixture.Db)
            .LoadBotReplyMessageIdAsync("conn-1", "g1", "draft-request-1");
        Assert.Equal("provider-preview-1", providerReplyId);

        var promoted = await new ZaloAmbientLeasePendingReplyPromotion(fixture.Db)
            .TryPromoteAsync("conn-1", "g1", incoming);

        Assert.NotNull(promoted);
        Assert.True(promoted!.MentionedBot);
        Assert.Contains(promoted.Mentions, item => item.Uid == "bot-account");
        Assert.Equal("xác nhận", promoted.Content);
        Assert.Equal("user-long", promoted.SenderId);
    }

    [Fact]
    public async Task Wrong_bot_reply_id_is_not_authority()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = fixture.Incoming("confirm-2", "xác nhận", "some-other-bot-message", "user-long");

        var promoted = await new ZaloAmbientLeasePendingReplyPromotion(fixture.Db)
            .TryPromoteAsync("conn-1", "g1", incoming);

        Assert.Null(promoted);
    }

    [Fact]
    public async Task Another_sender_cannot_confirm_someone_elses_pending_preview()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = fixture.Incoming("confirm-3", "xác nhận", "provider-preview-1", "user-nam");

        var promoted = await new ZaloAmbientLeasePendingReplyPromotion(fixture.Db)
            .TryPromoteAsync("conn-1", "g1", incoming);

        Assert.Null(promoted);
    }

    [Fact]
    public async Task Bare_ok_is_not_promoted_even_when_replying_to_preview()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = fixture.Incoming("confirm-4", "ok", "provider-preview-1", "user-long");

        var promoted = await new ZaloAmbientLeasePendingReplyPromotion(fixture.Db)
            .TryPromoteAsync("conn-1", "g1", incoming);

        Assert.Null(promoted);
    }

    [Fact]
    public async Task Missing_quote_is_not_promoted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "confirm-5",
            senderId: "user-long",
            senderName: "Long",
            content: "xác nhận",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var promoted = await new ZaloAmbientLeasePendingReplyPromotion(fixture.Db)
            .TryPromoteAsync("conn-1", "g1", incoming);

        Assert.Null(promoted);
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
                Email = $"pending-reply-{Guid.NewGuid():n}@example.test",
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
            var now = DateTimeOffset.UtcNow;
            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.ZaloBotConversationStates.Add(new ZaloBotConversationState
            {
                Id = "pending-1",
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                GroupId = "g1",
                SenderZaloUserId = "user-long",
                PendingIntent = ZaloBotIntent.AutoDraftConfirm.ToString(),
                PendingPayloadJson = "[\"session-t6\"]",
                PreviousCommand = ZaloBotIntent.AutoDraft.ToString(),
                ExpiresAt = now.AddMinutes(5),
                CreatedAt = now,
                UpdatedAt = now
            });
            db.ZaloGroupMessages.Add(new ZaloGroupMessage
            {
                Id = "source-row-1",
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                GroupId = "g1",
                MessageId = "draft-request-1",
                SenderId = "user-long",
                SenderName = "Long",
                Content = "xếp team T6",
                SentAt = now,
                BotReplySentAt = now.AddSeconds(1),
                SelectedIntent = ZaloBotIntent.AutoDraft.ToString(),
                ReplyOutcome = "sent",
                IsFromBot = false
            });
            await db.SaveChangesAsync();
            await new ZaloMessageGraphStore(db).RememberOutboundAsync(
                zalo.Id,
                "g1",
                "provider-preview-1",
                "draft-request-1");
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public ZaloIncomingMessageEvent Incoming(
            string messageId,
            string content,
            string quotedMessageId,
            string senderId)
        {
            var quote = new ZaloBridgeMessageQuote(
                quotedMessageId,
                "bot-account",
                "Npc",
                "⚠️ Tự draft sẽ thay đổi đội hình T6.",
                "chat",
                DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(),
                null);
            return new ZaloIncomingMessageEvent(
                accountId: "bot-account",
                botId: "bot-account",
                groupId: "g1",
                messageId: messageId,
                senderId: senderId,
                senderName: senderId == "user-long" ? "Long" : "Nam",
                content: content,
                mentions: [],
                mentionedBot: false,
                sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                quote: quote);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
