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

public sealed class ZaloAmbientSocialPilotTests
{
    [Fact]
    public void Social_pilot_is_disabled_for_generation_and_send_by_default()
    {
        var settings = ZaloAmbientSocialPilotSettings.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.False(settings.Enabled);
        Assert.False(settings.SendEnabled);
        Assert.Equal(90, settings.MinimumScore);
        Assert.Equal(8, settings.MaxContextMessages);
        Assert.Equal(180, settings.MaxReplyChars);
    }

    [Theory]
    [InlineData("Đùa tí thôi, nay bot cũng có mood nha 😄", true)]
    [InlineData("__NO_REPLY__", false)]
    [InlineData("Tui đã thêm bạn vào roster rồi nha", false)]
    [InlineData("Tui vừa ghi nhớ chuyện này rồi", false)]
    [InlineData("The user is asking me to joke", false)]
    [InlineData("Xem nè https://example.test", false)]
    [InlineData("@all vô đây coi bot nè", false)]
    public void Candidate_safety_filter_blocks_authority_reasoning_and_spam(
        string candidate,
        bool expected)
    {
        Assert.Equal(expected, ZaloAmbientSocialResponder.IsSafeCandidate(candidate, 180));
    }

    [Fact]
    public async Task Direct_bot_banter_can_generate_short_social_candidate_without_domain_write()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var httpClient = new HttpClient(new StaticAiHandler("Nay tui khởi động miệng trước cho nóng sân thôi 😄"));
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);
        var incoming = Message("m1", "user-long", "Long", "con bot nay gay du vay?");

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            incoming,
            SocialDecision("m1"),
            EnabledSettings(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Nay tui khởi động miệng trước cho nóng sân thôi 😄", result!.Text);
        Assert.True(result.EffectiveScore >= 90);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Human_vocative_about_bot_is_suppressed_before_ai_call()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StaticAiHandler("không được gọi");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);
        var incoming = Message("m2", "user-long", "Long", "Nam oi con bot nay gay du vay?");

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            incoming,
            SocialDecision("m2"),
            EnabledSettings(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("ack_or_emoji_only")]
    [InlineData("bot_cooldown")]
    [InlineData("busy_group")]
    public async Task Ambient_hard_suppression_beats_high_confidence_bot_address(string signal)
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StaticAiHandler("không được gọi");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);
        var decision = SocialDecision("m-suppressed") with
        {
            Signals = [signal],
            Score = 100
        };

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("m-suppressed", "user-long", "Long", "con bot haha"),
            decision,
            EnabledSettings(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Action_decision_never_enters_social_ai()
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new StaticAiHandler("không được gọi");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);
        var incoming = Message("m3", "user-long", "Long", "con bot draft lai di");
        var actionDecision = SocialDecision("m3") with
        {
            Kind = ZaloAmbientParticipationKind.Action,
            Intent = ZaloBotIntent.Redraft.ToString(),
            Score = 100
        };

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            incoming,
            actionDecision,
            EnabledSettings(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Unsafe_ai_claim_is_dropped_after_generation()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var httpClient = new HttpClient(new StaticAiHandler("Tui đã thêm bạn vào roster rồi nha"));
        var responder = new ZaloAmbientSocialResponder(
            fixture.Db,
            EnabledConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var result = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            Message("m4", "user-long", "Long", "con bot nay nay sao roi?"),
            SocialDecision("m4"),
            EnabledSettings(),
            CancellationToken.None);

        Assert.Null(result);
    }

    private static IConfiguration EnabledConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/v1/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();

    private static ZaloAmbientSocialPilotSettings EnabledSettings() =>
        new(true, false, 90, 8, 180);

    private static ZaloAmbientParticipationDecision SocialDecision(string messageId) => new(
        WouldReply: false,
        Score: 30,
        Kind: ZaloAmbientParticipationKind.Social,
        Intent: ZaloBotIntent.GeneralChat.ToString(),
        IntentConfidence: .5,
        Signals: ["question"],
        Situation: new ZaloAmbientGroupSituation(
            RecentMessageCount: 1,
            RecentTwoMinuteMessageCount: 1,
            DistinctParticipantCount: 1,
            RecentBotMessageCount: 0,
            LastBotMessageAt: null,
            RecentMessageIds: [messageId]));

    private static ZaloIncomingMessageEvent Message(
        string messageId,
        string senderId,
        string senderName,
        string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: senderId,
        senderName: senderName,
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class StaticAiHandler(string answer) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var payload = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new { content = answer }
                    }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
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
                Id = "admin-social",
                DisplayName = "Admin",
                Email = $"social-{Guid.NewGuid():n}@example.test",
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
            await db.SaveChangesAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
