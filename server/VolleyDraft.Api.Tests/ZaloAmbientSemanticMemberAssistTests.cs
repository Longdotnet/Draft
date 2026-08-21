using System.Net;
using System.Text;
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

public sealed class ZaloAmbientSemanticMemberAssistTests
{
    [Fact]
    public async Task Bare_pass_then_short_quoted_claim_reenters_safe_existing_flow()
    {
        await using var fixture = await Fixture.CreateAsync();

        var pass = Message(
            "pass-1",
            "user-hoang",
            "Hoàng Nguyễn",
            "Nay có ai mún đánh hong ạ, em pass nè 🥺");
        var promotedPass = ZaloAmbientDomainIntentPromotion.Promote(
            pass,
            new ZaloAmbientDomainIntentDecision(
                ZaloAmbientDomainIntentKind.PassOwnSlot,
                .97,
                "self_pass"));

        Assert.NotNull(promotedPass);
        var passReply = await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", promotedPass!);

        Assert.NotNull(passReply);
        Assert.Equal(ZaloMemberAssistKind.PassSlotHelp, passReply!.Kind);
        Assert.Contains("pass slot T6", passReply.Text, StringComparison.OrdinalIgnoreCase);

        var claim = Message(
            "claim-1",
            "user-nhan",
            "Nguyễn Trí Nhân",
            "A xin",
            quote: new ZaloBridgeMessageQuote(
                "pass-1",
                "user-hoang",
                "Hoàng Nguyễn",
                pass.Content,
                "chat",
                DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds(),
                null));
        var promotedClaim = ZaloAmbientDomainIntentPromotion.Promote(
            claim,
            new ZaloAmbientDomainIntentDecision(
                ZaloAmbientDomainIntentKind.ClaimOpenSlot,
                .98,
                "reply_to_pass"));

        Assert.NotNull(promotedClaim);
        Assert.Contains("nhận của Hoàng Nguyễn", promotedClaim!.Content, StringComparison.OrdinalIgnoreCase);

        var claimReply = await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", promotedClaim);

        Assert.NotNull(claimReply);
        Assert.Equal(ZaloMemberAssistKind.OpenSlotClaim, claimReply!.Kind);
        Assert.Contains("Nhân", claimReply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hoàng", claimReply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Broadcast_all_is_removed_before_deterministic_self_pass_validation()
    {
        await using var fixture = await Fixture.CreateAsync(ownerId: "user-pin", ownerName: "Pin");
        const string content = "Nay co viec hk di dx. Em xin pass slot T6 hom nay a. @All";
        var allPos = content.IndexOf("@All", StringComparison.Ordinal);
        var incoming = Message(
            "pass-all",
            "user-pin",
            "Pin",
            content,
            mentions: [new ZaloBridgeMention("broadcast-all", allPos, 4)]);

        var promoted = ZaloAmbientDomainIntentPromotion.Promote(
            incoming,
            new ZaloAmbientDomainIntentDecision(
                ZaloAmbientDomainIntentKind.PassOwnSlot,
                .99,
                "self_pass_broadcast"));

        Assert.NotNull(promoted);
        Assert.Empty(promoted!.Mentions);
        var reply = await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", promoted);

        Assert.NotNull(reply);
        Assert.Equal(ZaloMemberAssistKind.PassSlotHelp, reply!.Kind);
    }

    [Fact]
    public async Task Human_mention_is_preserved_and_still_blocks_self_pass_assumption()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string content = "@To An pass slot T6 nha";
        var incoming = Message(
            "pass-human",
            "user-hoang",
            "Hoàng Nguyễn",
            content,
            mentions: [new ZaloBridgeMention("user-toan", 0, "@To An".Length)]);

        var promoted = ZaloAmbientDomainIntentPromotion.Promote(
            incoming,
            new ZaloAmbientDomainIntentDecision(
                ZaloAmbientDomainIntentKind.PassOwnSlot,
                .90,
                "model_must_still_fail_closed"));

        Assert.NotNull(promoted);
        Assert.Single(promoted!.Mentions);
        var reply = await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", promoted);

        Assert.Null(reply);
    }

    [Fact]
    public async Task Semantic_resolver_reads_quote_and_returns_structured_claim_only()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pass = Message("pass-ai", "user-hoang", "Hoàng Nguyễn", "Nay có ai mún đánh hong ạ, em pass nè 🥺");
        var promotedPass = ZaloAmbientDomainIntentPromotion.Promote(
            pass,
            new ZaloAmbientDomainIntentDecision(ZaloAmbientDomainIntentKind.PassOwnSlot, .99, "seed"));
        await new ZaloMemberAssistService(fixture.Db)
            .TryBuildAsync("conn-1", "g1", promotedPass!);

        var handler = new CapturingAiHandler(
            "{\"kind\":\"ClaimOpenSlot\",\"confidence\":0.97,\"reason\":\"reply_to_pass\"}");
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/v1/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model",
                ["ZaloBot:AiPerUserPerMinute"] = "20",
                ["ZaloBot:AiPerGroupPerMinute"] = "100"
            })
            .Build();
        var incoming = Message(
            "claim-ai",
            "user-nhan-ai",
            "Nguyễn Trí Nhân",
            "A xin",
            quote: new ZaloBridgeMessageQuote(
                "pass-ai",
                "user-hoang",
                "Hoàng Nguyễn",
                pass.Content,
                "chat",
                DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(),
                null));
        var settings = ZaloAmbientDomainIntentSettings.FromConfiguration(configuration);

        var decision = await new ZaloAmbientDomainIntentResolver(
                fixture.Db,
                configuration,
                NullLogger<ZaloOverbookService>.Instance,
                httpClient)
            .ResolveAsync("conn-1", "g1", incoming, [], settings);

        Assert.Equal(ZaloAmbientDomainIntentKind.ClaimOpenSlot, decision.Kind);
        Assert.Equal(.97, decision.Confidence, 2);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("A xin", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("Nay có ai mún đánh hong", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("OpenOffers", handler.LastRequestBody, StringComparison.Ordinal);
    }

    private static ZaloIncomingMessageEvent Message(
        string messageId,
        string senderId,
        string senderName,
        string content,
        IReadOnlyList<ZaloBridgeMention>? mentions = null,
        ZaloBridgeMessageQuote? quote = null) => new(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: messageId,
            senderId: senderId,
            senderName: senderName,
            content: content,
            mentions: mentions ?? [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: quote);

    private sealed class CapturingAiHandler(string answer) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { content = answer } }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
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

        public static async Task<Fixture> CreateAsync(
            string ownerId = "user-hoang",
            string ownerName = "Hoàng Nguyễn")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-semantic",
                DisplayName = "Admin",
                Email = $"semantic-{Guid.NewGuid():n}@example.test",
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
            var profile = new PlayerProfile
            {
                Id = "profile-owner",
                ZaloUserId = ownerId,
                DisplayName = ownerName
            };
            var session = new MatchSession
            {
                Id = "session-t6",
                Name = "T6",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                BotEnabled = true,
                Status = SessionStatus.Setup,
                StartTime = DateTimeOffset.UtcNow.AddDays(1),
                TeamCount = 3,
                TeamSize = 6
            };
            session.Players.Add(new SessionPlayer
            {
                Id = "player-owner",
                SessionId = session.Id,
                PlayerProfileId = profile.Id,
                PlayerProfile = profile,
                DisplayName = ownerName,
                IsPresent = true
            });

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.Add(profile);
            db.MatchSessions.Add(session);
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
