using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientFactPilotIntegrationTests
{
    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    public async Task Ambient_fact_send_requires_both_rollout_gates(
        bool shadowMode,
        bool pilotEnabled,
        int expectedSends)
    {
        await using var fixture = await Fixture.CreateAsync(shadowMode, pilotEnabled);
        var incoming = fixture.Incoming("gate-message");

        var handled = await fixture.Service.TryHandleZaloConfirmationAsync(incoming);

        Assert.False(handled); // Ambient participation never claims legacy confirmation routing.
        var stored = await fixture.Db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item => item.ZaloConnectionId == "conn-1" && item.MessageId == incoming.MessageId);
        var diagnostics = await fixture.DescribeAsync(incoming.MessageId, stored);
        Assert.True(
            fixture.Bridge.SendCount == expectedSends,
            $"Expected {expectedSends} ambient bridge send(s), got {fixture.Bridge.SendCount}. {diagnostics}");
        if (expectedSends == 0)
        {
            Assert.Null(stored.BotReplySentAt);
            Assert.NotEqual("ambient_sent", stored.ReplyOutcome);
        }
        else
        {
            Assert.NotNull(stored.BotReplySentAt);
            Assert.Equal("ambient_sent", stored.ReplyOutcome);
            Assert.Equal(ZaloBotIntent.MissingSlots.ToString(), stored.SelectedIntent);
            Assert.False(stored.AiCalled);
        }
    }

    [Fact]
    public async Task Duplicate_webhook_sends_one_ambient_fact_and_keeps_provider_reply_identity()
    {
        await using var fixture = await Fixture.CreateAsync(shadowMode: false, pilotEnabled: true);
        var incoming = fixture.Incoming("duplicate-message");

        await fixture.Service.TryHandleZaloConfirmationAsync(incoming);
        await fixture.Service.TryHandleZaloConfirmationAsync(incoming);

        var inbound = await fixture.Db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item => item.ZaloConnectionId == "conn-1" && item.MessageId == "duplicate-message");
        var diagnostics = await fixture.DescribeAsync(incoming.MessageId, inbound);
        Assert.True(
            fixture.Bridge.SendCount == 1,
            $"Expected one ambient bridge send across duplicate webhook delivery, got {fixture.Bridge.SendCount}. {diagnostics}");
        Assert.Single(fixture.Bridge.RequestBodies);
        Assert.Contains("ambient-fact:bot-account:duplicate-message", fixture.Bridge.RequestBodies[0]);

        Assert.NotNull(inbound.BotReplySentAt);
        Assert.Equal("ambient_sent", inbound.ReplyOutcome);
        Assert.Equal(1, inbound.ReplyAttemptCount);

        var outbound = await fixture.Db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item => item.ZaloConnectionId == "conn-1" && item.MessageId == "provider-ambient-1");
        Assert.True(outbound.IsFromBot);
        Assert.Equal("sent", outbound.ReplyOutcome);

        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM "ZaloBotTraces"
            WHERE "MessageId" = 'duplicate-message'
              AND "IntentSource" = 'AmbientFactPilot'
              AND "AddressReason" = 'AmbientFactSent';
            """;
        var traceCount = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Equal(1L, traceCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteConnection connection,
            VolleyDraftDbContext db,
            RecordingBridgeHandler bridge,
            RecordingLogger<ZaloOverbookService> logger,
            ZaloOverbookService service)
        {
            Connection = connection;
            Db = db;
            Bridge = bridge;
            Logger = logger;
            Service = service;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public RecordingBridgeHandler Bridge { get; }
        public RecordingLogger<ZaloOverbookService> Logger { get; }
        public ZaloOverbookService Service { get; }

        public static async Task<Fixture> CreateAsync(bool shadowMode, bool pilotEnabled)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();

            db.Users.Add(new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"ambient-pilot-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            });
            var zaloConnection = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = "admin-1",
                AccountZaloId = "bot-account",
                DisplayName = "Volley Bot",
                EncryptedCredentials = "test"
            };
            var session = new MatchSession
            {
                Id = "session-t6",
                AdminUserId = "admin-1",
                ZaloConnectionId = zaloConnection.Id,
                ZaloConnection = zaloConnection,
                ZaloGroupId = "g1",
                Name = "T6",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddDays(1),
                TeamCount = 1,
                TeamSize = 6
            };
            session.Players.Add(new SessionPlayer
            {
                Id = "player-1",
                SessionId = session.Id,
                DisplayName = "Long",
                IsPresent = true
            });
            session.Players.Add(new SessionPlayer
            {
                Id = "player-2",
                SessionId = session.Id,
                DisplayName = "Nam",
                IsPresent = true
            });
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zalo:CredentialEncryptionKey"] = "ambient-fact-pilot-test-key",
                    ["ZaloBot:Ambient:Enabled"] = "true",
                    ["ZaloBot:Ambient:ShadowMode"] = shadowMode.ToString(),
                    ["ZaloBot:Ambient:WouldReplyThreshold"] = "65",
                    ["ZaloBot:Ambient:BotCooldownSeconds"] = "20",
                    ["ZaloBot:Ambient:FactPilot:Enabled"] = pilotEnabled.ToString(),
                    ["ZaloBot:Ambient:FactPilot:MinimumScore"] = "85"
                })
                .Build();
            var bridgeHandler = new RecordingBridgeHandler();
            var httpClient = new HttpClient(bridgeHandler)
            {
                BaseAddress = new Uri("https://bridge.test/")
            };
            var bridge = new ZaloBridgeClient(httpClient);
            var logger = new RecordingLogger<ZaloOverbookService>();
            var service = new ZaloOverbookService(
                db,
                bridge,
                new ZaloCredentialProtector(configuration),
                null!,
                null!,
                configuration,
                logger);
            return new Fixture(connection, db, bridgeHandler, logger, service);
        }

        public ZaloIncomingMessageEvent Incoming(string messageId) => new(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: messageId,
            senderId: "user-1",
            senderName: "Long",
            content: "T6 còn bao nhiêu slot?",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public async Task<string> DescribeAsync(string messageId, ZaloGroupMessage stored)
        {
            var traces = new List<string>();
            await using var command = Connection.CreateCommand();
            command.CommandText = """
                SELECT "IntentSource", "AddressReason", "Intent", "Confidence", "FallbackReason"
                FROM "ZaloBotTraces"
                WHERE "MessageId" = @messageId
                ORDER BY "CreatedAt";
                """;
            command.Parameters.AddWithValue("@messageId", messageId);
            try
            {
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    traces.Add(
                        $"source={Nullable(reader, 0)},address={Nullable(reader, 1)},intent={Nullable(reader, 2)},confidence={Nullable(reader, 3)},meta={Nullable(reader, 4)}");
                }
            }
            catch (SqliteException exception)
            {
                traces.Add($"trace-read-failed:{exception.Message}");
            }

            return $"Inbound[outcome={stored.ReplyOutcome ?? "null"},attempts={stored.ReplyAttemptCount},token={stored.ProcessingToken ?? "null"},botReplyAt={(stored.BotReplySentAt?.ToString("O") ?? "null")}]; " +
                   $"Traces=[{string.Join("; ", traces)}]; Logs=[{string.Join(" || ", Logger.Entries)}]";
        }

        private static string Nullable(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? "null" : Convert.ToString(reader.GetValue(ordinal)) ?? "null";

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class RecordingBridgeHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/v1/group-messages", StringComparison.Ordinal) == true)
            {
                SendCount += 1;
                RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(HttpStatusCode.OK, "{\"sent\":true,\"mock\":false,\"messageId\":\"provider-ambient-1\"}");
            }
            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Add(exception is null
                ? $"{logLevel}:{message}"
                : $"{logLevel}:{message} :: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
