using System.Net;
using System.Text;
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

public sealed class ZaloMissingProfilePreRouteTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exact_prompt_reply_updates_quoted_match_before_generic_bot_even_with_multiple_active_prompts(bool mentionedBot)
    {
        await using var fixture = await Fixture.CreateAsync(includeSecondPlayerContext: true);
        var promptedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await fixture.CreatePromptAsync("session-t6", "player-hiep-t6", "profile-prompt-t6", promptedAt);
        await fixture.CreatePromptAsync("session-t7", "player-hiep-t7", "profile-prompt-t7", promptedAt.AddSeconds(1));
        var incoming = fixture.Incoming(
            "reply-profile-1",
            "@Npc nam, vị trí nào cũng đc, trình độ trung bình",
            mentionedBot,
            new ZaloBridgeMessageQuote(
                "profile-prompt-t6",
                "bot-account",
                "Npc",
                "Kèo T6 04/09 17:30 gần chốt draft rồi, còn thiếu chút hồ sơ",
                "chat",
                promptedAt.ToUnixTimeMilliseconds(),
                null));

        var handled = await fixture.Service.TryHandleZaloPreRouteAsync(incoming);

        Assert.True(handled);
        Assert.Equal(1, fixture.Bridge.SendCount);
        Assert.DoesNotContain("bạn đang hỏi trận nào", fixture.Bridge.LastBody, StringComparison.OrdinalIgnoreCase);

        fixture.Db.ChangeTracker.Clear();
        var profile = await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-hiep");
        var t6Player = await fixture.Db.SessionPlayers.AsNoTracking().SingleAsync(item => item.Id == "player-hiep-t6");
        var t7Player = await fixture.Db.SessionPlayers.AsNoTracking().SingleAsync(item => item.Id == "player-hiep-t7");
        Assert.Equal(PlayerGender.Male, profile.Gender);
        Assert.Equal(PlayerRole.FullStack, profile.DefaultRole);
        Assert.Equal(PlayerLevel.Average, profile.DefaultLevel);
        Assert.Equal(PlayerGender.Male, t6Player.Gender);
        Assert.Equal(PlayerRole.FullStack, t6Player.Role);
        Assert.Equal(PlayerLevel.Average, t6Player.Level);
        Assert.Equal(PlayerGender.Unknown, t7Player.Gender);
        Assert.Equal(PlayerRole.Attack, t7Player.Role);
        Assert.Equal(PlayerLevel.New, t7Player.Level);

        var active = await new ZaloMissingProfilePromptStore(fixture.Db).GetActiveAsync(DateTimeOffset.UtcNow);
        var remaining = Assert.Single(active);
        Assert.Equal("session-t7", remaining.SessionId);
    }

    [Fact]
    public async Task Single_active_prompt_accepts_natural_answer_without_quote_or_bot_mention()
    {
        await using var fixture = await Fixture.CreateAsync();
        var promptedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.CreatePromptAsync("session-t6", "player-hiep-t6", "profile-prompt-t6", promptedAt);
        var incoming = fixture.Incoming(
            "reply-profile-plain",
            "nam, thủ, trung bình",
            mentionedBot: false,
            quote: null);

        var handled = await fixture.Service.TryHandleZaloPreRouteAsync(incoming);

        Assert.True(handled);
        fixture.Db.ChangeTracker.Clear();
        var profile = await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-hiep");
        Assert.Equal(PlayerGender.Male, profile.Gender);
        Assert.Equal(PlayerRole.Defense, profile.DefaultRole);
        Assert.Equal(PlayerLevel.Average, profile.DefaultLevel);
    }

    [Fact]
    public async Task Partial_gender_reply_does_not_silently_confirm_existing_session_role_or_level()
    {
        await using var fixture = await Fixture.CreateAsync();
        var promptedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.CreatePromptAsync("session-t6", "player-hiep-t6", "profile-prompt-t6", promptedAt);
        var incoming = fixture.Incoming(
            "reply-gender-only",
            "nam",
            mentionedBot: false,
            quote: null);

        var handled = await fixture.Service.TryHandleZaloPreRouteAsync(incoming);

        Assert.True(handled);
        Assert.Equal(1, fixture.Bridge.SendCount);
        fixture.Db.ChangeTracker.Clear();
        var profile = await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-hiep");
        var player = await fixture.Db.SessionPlayers.AsNoTracking().SingleAsync(item => item.Id == "player-hiep-t6");
        Assert.Equal(PlayerGender.Male, profile.Gender);
        Assert.Null(profile.DefaultRole);
        Assert.Null(profile.DefaultLevel);
        Assert.Equal(PlayerGender.Male, player.Gender);
        Assert.Equal(PlayerRole.Attack, player.Role);
        Assert.Equal(PlayerLevel.New, player.Level);

        var active = await new ZaloMissingProfilePromptStore(fixture.Db).GetActiveAsync(DateTimeOffset.UtcNow);
        var remaining = Assert.Single(active);
        Assert.False(remaining.MissingGender);
        Assert.True(remaining.MissingRole);
        Assert.True(remaining.MissingLevel);
    }

    [Fact]
    public async Task Unrelated_command_is_not_stolen_while_profile_prompt_is_active()
    {
        await using var fixture = await Fixture.CreateAsync();
        var promptedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.CreatePromptAsync("session-t6", "player-hiep-t6", "profile-prompt-t6", promptedAt);
        var incoming = fixture.Incoming(
            "unrelated-command",
            "@Npc T6 còn bao nhiêu slot?",
            mentionedBot: true,
            quote: null);

        var handled = await fixture.Service.TryHandleTargetedMissingProfileReplyAsync(incoming);

        Assert.False(handled);
        Assert.Equal(0, fixture.Bridge.SendCount);
        fixture.Db.ChangeTracker.Clear();
        var profile = await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-hiep");
        Assert.Null(profile.Gender);
        Assert.Null(profile.DefaultRole);
        Assert.Null(profile.DefaultLevel);
    }

    [Fact]
    public async Task Multiple_active_prompts_without_exact_quote_fail_closed()
    {
        await using var fixture = await Fixture.CreateAsync(includeSecondPlayerContext: true);
        var promptedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.CreatePromptAsync("session-t6", "player-hiep-t6", "profile-prompt-t6", promptedAt);
        await fixture.CreatePromptAsync("session-t7", "player-hiep-t7", "profile-prompt-t7", promptedAt.AddSeconds(1));
        var incoming = fixture.Incoming(
            "ambiguous-profile",
            "nam, công, trung bình",
            mentionedBot: false,
            quote: null);

        var handled = await fixture.Service.TryHandleTargetedMissingProfileReplyAsync(incoming);

        Assert.False(handled);
        Assert.Equal(0, fixture.Bridge.SendCount);
        fixture.Db.ChangeTracker.Clear();
        var profile = await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync(item => item.Id == "profile-hiep");
        Assert.Null(profile.Gender);
        Assert.Null(profile.DefaultRole);
        Assert.Null(profile.DefaultLevel);
    }

    [Fact]
    public void Screenshot_phrase_parses_all_three_profile_fields_despite_punctuation()
    {
        var parsed = ZaloOverbookService.ParseTargetedProfileReply(
            "@Npc nam, vị trí nào cũng đc, trình độ trung bình",
            (Gender: true, Role: true, Level: true));

        Assert.False(parsed.HasConflict);
        Assert.True(parsed.LooksLikeProfileAnswer);
        Assert.Equal(PlayerGender.Male, parsed.Gender);
        Assert.Equal(PlayerRole.FullStack, parsed.Role);
        Assert.Equal(PlayerLevel.Average, parsed.Level);
    }

    [Theory]
    [InlineData("vị trí nào cũng đc", true)]
    [InlineData("vị trí nào cũng đc, trình độ trung bình", true)]
    [InlineData("vị trí gì cũng được", true)]
    [InlineData("vị trí nào cũng ok", true)]
    [InlineData("cái nào cũng được", false)]
    [InlineData("vị trí nào chưa được cập nhật?", false)]
    public void Flexible_position_semantics_require_explicit_position_scope(string text, bool expected)
    {
        Assert.Equal(expected, ZaloFlexibleProfileReplySemantics.AcceptsAnyPosition(text));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteConnection connection,
            VolleyDraftDbContext db,
            RecordingBridgeHandler bridge,
            ZaloOverbookService service)
        {
            Connection = connection;
            Db = db;
            Bridge = bridge;
            Service = service;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public RecordingBridgeHandler Bridge { get; }
        public ZaloOverbookService Service { get; }

        public static async Task<Fixture> CreateAsync(bool includeSecondPlayerContext = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(
                new DbContextOptionsBuilder<VolleyDraftDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"profile-pre-route-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var connectionRow = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test"
            };
            var profile = new PlayerProfile
            {
                Id = "profile-hiep",
                ZaloUserId = "uid-hiep",
                DisplayName = "Hiệp Hoàng Phạm",
                Gender = null,
                DefaultRole = null,
                DefaultLevel = null
            };
            var t6 = Session("session-t6", "T6 04/09 17:30", connectionRow, admin.Id, DayOfWeek.Friday);
            t6.Players.Add(Player("player-hiep-t6", t6.Id, profile));

            db.Users.Add(admin);
            db.ZaloConnections.Add(connectionRow);
            db.PlayerProfiles.Add(profile);
            db.MatchSessions.Add(t6);

            if (includeSecondPlayerContext)
            {
                var t7 = Session("session-t7", "T7 05/09 17:30", connectionRow, admin.Id, DayOfWeek.Saturday);
                t7.Players.Add(Player("player-hiep-t7", t7.Id, profile));
                db.MatchSessions.Add(t7);
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zalo:CredentialEncryptionKey"] = "profile-pre-route-test-key",
                    ["ZaloBot:Ambient:Enabled"] = "false"
                })
                .Build();
            var bridgeHandler = new RecordingBridgeHandler();
            var bridge = new ZaloBridgeClient(new HttpClient(bridgeHandler)
            {
                BaseAddress = new Uri("https://bridge.test/")
            });
            var service = new ZaloOverbookService(
                db,
                bridge,
                new ZaloCredentialProtector(configuration),
                null!,
                null!,
                configuration,
                NullLogger<ZaloOverbookService>.Instance);
            return new Fixture(connection, db, bridgeHandler, service);
        }

        public async Task CreatePromptAsync(
            string sessionId,
            string playerId,
            string promptMessageId,
            DateTimeOffset promptedAt)
        {
            var store = new ZaloMissingProfilePromptStore(Db);
            await store.UpsertAsync(
                "conn-1",
                "g1",
                sessionId,
                playerId,
                "uid-hiep",
                "Hiệp Hoàng Phạm",
                missingGender: true,
                missingRole: true,
                missingLevel: true,
                promptMessageId,
                promptedAt,
                promptedAt.AddMinutes(30));
        }

        public ZaloIncomingMessageEvent Incoming(
            string messageId,
            string content,
            bool mentionedBot,
            ZaloBridgeMessageQuote? quote) => new(
                "bot-account",
                "bot-account",
                "g1",
                messageId,
                "uid-hiep",
                "Hiệp Hoàng Phạm",
                content,
                [],
                mentionedBot,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                quote);

        private static MatchSession Session(
            string id,
            string name,
            ZaloConnection connection,
            string adminUserId,
            DayOfWeek day) => new()
            {
                Id = id,
                AdminUserId = adminUserId,
                ZaloConnectionId = connection.Id,
                ZaloConnection = connection,
                ZaloGroupId = "g1",
                Name = name,
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = ZaloTestDates.Next(day),
                TeamCount = 3,
                TeamSize = 6
            };

        private static SessionPlayer Player(string id, string sessionId, PlayerProfile profile) => new()
        {
            Id = id,
            SessionId = sessionId,
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            DisplayName = profile.DisplayName,
            Gender = PlayerGender.Unknown,
            Role = PlayerRole.Attack,
            Level = PlayerLevel.New,
            IsPresent = true
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class RecordingBridgeHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/v1/group-messages", StringComparison.Ordinal) == true)
            {
                SendCount += 1;
                LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(HttpStatusCode.OK,
                    $"{{\"sent\":true,\"mock\":false,\"messageId\":\"provider-profile-reply-{SendCount}\"}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":\"unexpected test request\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
