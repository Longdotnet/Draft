using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientWakeAiTests
{
    [Fact]
    public async Task Plain_text_wake_uses_social_ai_even_when_deterministic_kind_is_help_fact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var configuration = AiConfiguration();
        var handler = new StaticAiHandler("Có tui đây 😄, Long gọi gì nè?");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            db,
            configuration,
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);

        var incoming = Message("wake-ai-1", "Bot ơi bot");
        var decision = new ZaloAmbientParticipationDecision(
            WouldReply: true,
            Score: 95,
            Kind: ZaloAmbientParticipationKind.Fact,
            Intent: ZaloBotIntent.Help.ToString(),
            IntentConfidence: .98,
            Signals: ["bot_plain_text_wake", "bot_cooldown"],
            Situation: Situation("wake-ai-1"));

        var reply = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            incoming,
            decision,
            new ZaloAmbientSocialPilotSettings(true, true, 90, 8, 180));

        Assert.NotNull(reply);
        Assert.Equal("Có tui đây 😄, Long gọi gì nè?", reply!.Text);
        Assert.Equal("plain_text_wake_ai", reply.AddressReason);
        Assert.True(reply.EffectiveScore >= 90);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Same_sender_lease_continues_social_ai_without_bot_keyword_or_mention()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var handler = new StaticAiHandler("Nói chứ, tui đang nghe nè 😄");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            db,
            AiConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);
        var incoming = Message("lease-ai-1", "nói chuyện tí coi");
        var decision = new ZaloAmbientParticipationDecision(
            WouldReply: true,
            Score: 90,
            Kind: ZaloAmbientParticipationKind.Social,
            Intent: ZaloBotIntent.Unknown.ToString(),
            IntentConfidence: .96,
            Signals: ["active_conversation_lease", "lease_social_followup", "bot_cooldown"],
            Situation: Situation("lease-ai-1"));

        var reply = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            incoming,
            decision,
            new ZaloAmbientSocialPilotSettings(true, true, 90, 8, 180));

        Assert.NotNull(reply);
        Assert.Equal("Nói chứ, tui đang nghe nè 😄", reply!.Text);
        Assert.Equal("active_conversation_lease_ai", reply.AddressReason);
        Assert.True(reply.EffectiveScore >= 90);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Real_domain_fact_never_uses_social_ai()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var handler = new StaticAiHandler("không được gọi");
        using var httpClient = new HttpClient(handler);
        var responder = new ZaloAmbientSocialResponder(
            db,
            AiConfiguration(),
            NullLogger<ZaloOverbookService>.Instance,
            httpClient);
        var incoming = Message("fact-ai-guard", "bot T6 còn bao nhiêu slot?");
        var decision = new ZaloAmbientParticipationDecision(
            WouldReply: true,
            Score: 100,
            Kind: ZaloAmbientParticipationKind.Fact,
            Intent: ZaloBotIntent.MissingSlots.ToString(),
            IntentConfidence: 1,
            Signals: ["fact_intent", "session_reference"],
            Situation: Situation("fact-ai-guard"));

        var reply = await responder.TryBuildAsync(
            "conn-1",
            "g1",
            incoming,
            decision,
            new ZaloAmbientSocialPilotSettings(true, true, 90, 8, 180));

        Assert.Null(reply);
        Assert.Equal(0, handler.CallCount);
    }

    private static IConfiguration AiConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Endpoint"] = "https://ai.test/v1/chat/completions",
                ["Ai:ApiKey"] = "test-key",
                ["Ai:Model"] = "test-model"
            })
            .Build();

    private static ZaloIncomingMessageEvent Message(string messageId, string content) => new(
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

    private static ZaloAmbientGroupSituation Situation(string messageId) => new(
        RecentMessageCount: 2,
        RecentTwoMinuteMessageCount: 2,
        DistinctParticipantCount: 1,
        RecentBotMessageCount: 1,
        LastBotMessageAt: DateTimeOffset.UtcNow.AddSeconds(-1),
        RecentMessageIds: [messageId]);

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
                    new { message = new { content = answer } }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
