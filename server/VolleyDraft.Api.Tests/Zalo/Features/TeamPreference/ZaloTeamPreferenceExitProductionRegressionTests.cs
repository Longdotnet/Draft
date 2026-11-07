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

public sealed class ZaloTeamPreferenceExitProductionRegressionTests
{
    [Fact]
    public async Task Positive_team_preference_remembers_session_choice_and_today_resumes_without_AI()
    {
        await using var fixture = await Fixture.CreateAsync(twoSessions: true, withExistingPreference: false);
        var request = fixture.Incoming(
            "positive-needs-session",
            "@Bott tui muốn chung team với @To An",
            mentionToAn: true);

        await fixture.Bot.HandleIncomingAsync(request);

        fixture.Db.ChangeTracker.Clear();
        var selection = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloBotIntent.TeamPreference.ToString(), selection.PendingIntent);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());

        var followUp = fixture.Incoming(
            "positive-today",
            "@Bott hôm nay",
            mentionToAn: false);

        await fixture.Bot.HandleIncomingAsync(followUp);

        fixture.Db.ChangeTracker.Clear();
        var confirmation = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm.ToString(), confirmation.PendingIntent);
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
        var storedFollowUp = await fixture.Db.ZaloGroupMessages.AsNoTracking()
            .SingleAsync(item => item.MessageId == "positive-today");
        Assert.False(storedFollowUp.AiCalled);
        Assert.Equal(ZaloBotIntent.TeamPreference.ToString(), storedFollowUp.SelectedIntent);
    }

    [Fact]
    public async Task Positive_team_preference_session_prompt_does_not_swallow_fresh_draft_command_with_today()
    {
        await using var fixture = await Fixture.CreateAsync(twoSessions: true, withExistingPreference: false);
        await fixture.Bot.HandleIncomingAsync(fixture.Incoming(
            "positive-before-switch",
            "@Bott tui muốn chung team với @To An",
            mentionToAn: true));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            ZaloBotIntent.TeamPreference.ToString(),
            (await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync()).PendingIntent);

        await fixture.Bot.HandleIncomingAsync(fixture.Incoming(
            "fresh-draft-today",
            "@Bott 8 hôm nay",
            mentionToAn: false));

        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.ZaloBotConversationStates.AsNoTracking()
            .AnyAsync(item => item.PendingIntent == ZaloBotIntent.TeamPreference.ToString()));
        var stored = await fixture.Db.ZaloGroupMessages.AsNoTracking()
            .SingleAsync(item => item.MessageId == "fresh-draft-today");
        Assert.NotEqual(ZaloBotIntent.TeamPreference.ToString(), stored.SelectedIntent);
    }

    [Fact]
    public async Task Negated_existing_preference_remembers_session_choice_and_today_resumes_without_AI()
    {
        await using var fixture = await Fixture.CreateAsync(twoSessions: true);
        var request = fixture.Incoming(
            "exit-needs-session",
            "@Bott tui không muốn chung team với @To An nữa",
            mentionToAn: true);

        var handled = await fixture.Service.TryHandleLegacyPendingContinuationPreRouteAsync(request);

        Assert.True(handled);
        Assert.Equal(1, fixture.Bridge.SendCount);
        Assert.Contains("nhiều buổi", fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var selection = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal("TeamPreferenceExitSessionChoice", selection.PendingIntent);
        Assert.Equal(2, await fixture.Db.TeamPreferenceGroups.AsNoTracking().CountAsync());
        var storedRequest = await fixture.Db.ZaloGroupMessages.AsNoTracking()
            .SingleAsync(item => item.MessageId == "exit-needs-session");
        Assert.False(storedRequest.AiCalled);

        var followUp = fixture.Incoming(
            "exit-today",
            "@Bott hôm nay",
            mentionToAn: false);

        handled = await fixture.Service.TryHandleLegacyPendingContinuationPreRouteAsync(followUp);

        Assert.True(handled);
        Assert.Equal(2, fixture.Bridge.SendCount);
        Assert.Contains("xác nhận", fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.TodaySessionName, fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var confirmation = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal("TeamPreferenceExitConfirm", confirmation.PendingIntent);
        Assert.Equal(2, await fixture.Db.TeamPreferenceGroups.AsNoTracking().CountAsync());
        var storedFollowUp = await fixture.Db.ZaloGroupMessages.AsNoTracking()
            .SingleAsync(item => item.MessageId == "exit-today");
        Assert.False(storedFollowUp.AiCalled);
        Assert.Equal("TeamPreferenceExitConfirm", storedFollowUp.SelectedIntent);
    }

    [Fact]
    public async Task Exit_session_clarification_duplicate_delivery_is_idempotent()
    {
        await using var fixture = await Fixture.CreateAsync(twoSessions: true);
        var request = fixture.Incoming(
            "exit-session-choice-duplicate",
            "@Bott tui không muốn chung team với @To An nữa",
            mentionToAn: true);

        Assert.True(await fixture.Service.TryHandleLegacyPendingContinuationPreRouteAsync(request));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            "TeamPreferenceExitSessionChoice",
            (await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync()).PendingIntent);
        Assert.Equal(1, fixture.Bridge.SendCount);

        Assert.True(await fixture.Service.TryHandleLegacyPendingContinuationPreRouteAsync(request));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            "TeamPreferenceExitSessionChoice",
            (await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync()).PendingIntent);
        Assert.Equal(1, fixture.Bridge.SendCount);
    }

    [Fact]
    public async Task Exit_session_prompt_does_not_swallow_fresh_draft_command_with_today()
    {
        await using var fixture = await Fixture.CreateAsync(twoSessions: true);
        Assert.True(await fixture.Service.TryHandleLegacyPendingContinuationPreRouteAsync(fixture.Incoming(
            "exit-before-switch",
            "@Bott tui không muốn chung team với @To An nữa",
            mentionToAn: true)));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            "TeamPreferenceExitSessionChoice",
            (await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync()).PendingIntent);

        var handled = await fixture.Service.TryHandleLegacyPendingContinuationPreRouteAsync(fixture.Incoming(
            "exit-fresh-draft",
            "@Bott 8 hôm nay",
            mentionToAn: false));

        Assert.False(handled);
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.Equal(1, fixture.Bridge.SendCount);
    }

    [Fact]
    public async Task Explicit_leave_same_team_group_with_date_is_deterministic_preview_not_GeneralChat()
    {
        await using var fixture = await Fixture.CreateAsync(twoSessions: false);
        var localStart = fixture.TodayStart.ToOffset(TimeSpan.FromHours(7));
        var request = fixture.Incoming(
            "exit-self-explicit",
            $"@Bott tui muốn rời nhóm chung team trận {localStart:dd/MM}",
            mentionToAn: false);

        var handled = await fixture.Service.TryHandleLegacyPendingContinuationPreRouteAsync(request);

        Assert.True(handled);
        Assert.Equal(1, fixture.Bridge.SendCount);
        Assert.Contains("không muốn tiếp tục ràng buộc chung team", fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xác nhận", fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal("TeamPreferenceExitConfirm", pending.PendingIntent);
        Assert.Single(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
        var stored = await fixture.Db.ZaloGroupMessages.AsNoTracking()
            .SingleAsync(item => item.MessageId == "exit-self-explicit");
        Assert.False(stored.AiCalled);
        Assert.Equal("TeamPreferenceExitConfirm", stored.SelectedIntent);
        Assert.NotEqual(ZaloBotIntent.GeneralChat.ToString(), stored.SelectedIntent);
    }

    [Fact]
    public async Task Explicit_leave_preview_accepts_addressed_confirmation_without_AI()
    {
        await using var fixture = await Fixture.CreateAsync(twoSessions: false);
        var localStart = fixture.TodayStart.ToOffset(TimeSpan.FromHours(7));
        Assert.True(await fixture.Service.TryHandleLegacyPendingContinuationPreRouteAsync(fixture.Incoming(
            "exit-self-before-confirm",
            $"@Bott tui muốn rời nhóm chung team trận {localStart:dd/MM}",
            mentionToAn: false)));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            "TeamPreferenceExitConfirm",
            (await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync()).PendingIntent);
        Assert.Single(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());

        var confirm = fixture.Incoming(
            "exit-self-confirm",
            "@Bott xác nhận",
            mentionToAn: false);
        var handled = await fixture.Service.TryHandleLegacyPendingContinuationPreRouteAsync(confirm);

        Assert.True(handled);
        Assert.Equal(2, fixture.Bridge.SendCount);
        Assert.Contains("Đã tách Thanh Long", fixture.Bridge.LastMessage, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.TeamPreferenceGroups.AsNoTracking().ToListAsync());
        Assert.Single(await fixture.Db.ZaloBotActionHistory.AsNoTracking()
            .Where(item => item.ActionType == "TeamPreferenceExit")
            .ToListAsync());
        var storedConfirm = await fixture.Db.ZaloGroupMessages.AsNoTracking()
            .SingleAsync(item => item.MessageId == "exit-self-confirm");
        Assert.False(storedConfirm.AiCalled);
        Assert.Equal("TeamPreferenceExitApplied", storedConfirm.SelectedIntent);
        Assert.Equal("team_preference_exit_applied", storedConfirm.ReplyOutcome);
        Assert.NotEqual(ZaloBotIntent.GeneralChat.ToString(), storedConfirm.SelectedIntent);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string ConnectionId = "conn-team-pref-exit-regression";
        private const string GroupId = "g-team-pref-exit-regression";
        private const string LongUid = "user-long";
        private const string ToAnUid = "user-toan";

        private readonly SqliteConnection sqlite;
        private readonly HttpClient bridgeHttpClient;
        private readonly HttpClient aiHttpClient;
        private long incomingTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private Fixture(
            SqliteConnection sqlite,
            VolleyDraftDbContext db,
            RecordingBridgeHandler bridge,
            ZaloOverbookService service,
            ZaloBotService bot,
            HttpClient bridgeHttpClient,
            HttpClient aiHttpClient,
            DateTimeOffset todayStart,
            string todaySessionName)
        {
            this.sqlite = sqlite;
            this.bridgeHttpClient = bridgeHttpClient;
            this.aiHttpClient = aiHttpClient;
            Db = db;
            Bridge = bridge;
            Service = service;
            Bot = bot;
            TodayStart = todayStart;
            TodaySessionName = todaySessionName;
        }

        public VolleyDraftDbContext Db { get; }
        public RecordingBridgeHandler Bridge { get; }
        public ZaloOverbookService Service { get; }
        public ZaloBotService Bot { get; }
        public DateTimeOffset TodayStart { get; }
        public string TodaySessionName { get; }

        public static async Task<Fixture> CreateAsync(
            bool twoSessions,
            bool withExistingPreference = true)
        {
            var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync();
            var db = new VolleyDraftDbContext(
                new DbContextOptionsBuilder<VolleyDraftDbContext>()
                    .UseSqlite(sqlite)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zalo:CredentialEncryptionKey"] = "team-pref-exit-regression-key",
                    ["ZaloBot:Ambient:Enabled"] = "false",
                    ["ZaloBot:ExactCommandCooldownSeconds"] = "0",
                    ["ZaloBot:TeamPreferenceExit:AiEnabled"] = "false"
                })
                .Build();

            var admin = new User
            {
                Id = "admin-team-pref-exit-regression",
                DisplayName = "Admin",
                Email = $"team-pref-exit-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var connection = new ZaloConnection
            {
                Id = ConnectionId,
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Bott",
                EncryptedCredentials = "test",
                Status = ZaloConnectionStatus.Connected
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(connection);
            var longProfile = new PlayerProfile
            {
                Id = "profile-long",
                ZaloUserId = LongUid,
                DisplayName = "Thanh Long"
            };
            var toAnProfile = new PlayerProfile
            {
                Id = "profile-toan",
                ZaloUserId = ToAnUid,
                DisplayName = "To An"
            };
            db.PlayerProfiles.AddRange(longProfile, toAnProfile);

            var vietnamOffset = TimeSpan.FromHours(7);
            var localNow = DateTimeOffset.UtcNow.ToOffset(vietnamOffset);
            var endOfToday = new DateTimeOffset(
                localNow.Year,
                localNow.Month,
                localNow.Day,
                23,
                59,
                59,
                vietnamOffset);
            var todayStart = endOfToday.ToUniversalTime();
            var todayName = $"Hôm nay {localNow:dd/MM} 23:59";
            AddPreferenceSession(
                db,
                admin,
                connection,
                longProfile,
                toAnProfile,
                "session-today",
                todayName,
                todayStart,
                "today",
                withExistingPreference);

            if (twoSessions)
            {
                var tomorrowLocal = localNow.Date.AddDays(1).AddHours(18).AddMinutes(30);
                var tomorrowStart = new DateTimeOffset(tomorrowLocal, vietnamOffset).ToUniversalTime();
                AddPreferenceSession(
                    db,
                    admin,
                    connection,
                    longProfile,
                    toAnProfile,
                    "session-tomorrow",
                    $"Ngày mai {tomorrowStart.ToOffset(vietnamOffset):dd/MM} 18:30",
                    tomorrowStart,
                    "tomorrow",
                    withExistingPreference);
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var bridgeHandler = new RecordingBridgeHandler();
            var bridgeHttpClient = new HttpClient(bridgeHandler)
            {
                BaseAddress = new Uri("https://bridge.test/")
            };
            var bridge = new ZaloBridgeClient(bridgeHttpClient);
            var aiHttpClient = new HttpClient(new NoAiHandler())
            {
                BaseAddress = new Uri("https://ai.test/")
            };
            var ai = new AiAssistantService(
                aiHttpClient,
                configuration,
                NullLogger<AiAssistantService>.Instance,
                db);
            var draft = new SessionDraftService(db);
            var actionHistory = new ZaloBotActionHistoryService(
                db,
                NullLogger<ZaloBotActionHistoryService>.Instance);
            var memberIntelligence = new ZaloMemberIntelligenceBotService(
                db,
                null!,
                null!,
                null!,
                ai,
                NullLogger<ZaloMemberIntelligenceBotService>.Instance);
            var bot = new ZaloBotService(
                db,
                bridge,
                ai,
                null!,
                draft,
                null!,
                null!,
                null!,
                actionHistory,
                memberIntelligence,
                null!,
                null!,
                configuration,
                NullLogger<ZaloBotService>.Instance);
            var service = new ZaloOverbookService(
                db,
                bridge,
                new ZaloCredentialProtector(configuration),
                null!,
                null!,
                configuration,
                NullLogger<ZaloOverbookService>.Instance);
            return new Fixture(
                sqlite,
                db,
                bridgeHandler,
                service,
                bot,
                bridgeHttpClient,
                aiHttpClient,
                todayStart,
                todayName);
        }

        public ZaloIncomingMessageEvent Incoming(
            string messageId,
            string content,
            bool mentionToAn)
        {
            var mentions = new List<ZaloBridgeMention>
            {
                new("bot-account", 0, "@Bott".Length)
            };
            if (mentionToAn)
            {
                mentions.Add(new ZaloBridgeMention(
                    ToAnUid,
                    content.IndexOf("@To An", StringComparison.Ordinal),
                    "@To An".Length));
            }

            return new ZaloIncomingMessageEvent(
                "bot-account",
                "bot-account",
                GroupId,
                messageId,
                LongUid,
                "Thanh Long",
                content,
                mentions,
                true,
                Interlocked.Increment(ref incomingTimestamp));
        }

        private static void AddPreferenceSession(
            VolleyDraftDbContext db,
            User admin,
            ZaloConnection connection,
            PlayerProfile longProfile,
            PlayerProfile toAnProfile,
            string sessionId,
            string sessionName,
            DateTimeOffset startTime,
            string suffix,
            bool withExistingPreference)
        {
            var session = new MatchSession
            {
                Id = sessionId,
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = connection.Id,
                ZaloConnection = connection,
                ZaloGroupId = GroupId,
                Name = sessionName,
                BotEnabled = true,
                Status = SessionStatus.Setup,
                StartTime = startTime,
                TeamCount = 3,
                TeamSize = 6
            };
            var longPlayer = Player($"sp-long-{suffix}", session.Id, longProfile);
            var toAnPlayer = Player($"sp-toan-{suffix}", session.Id, toAnProfile);
            session.Players.Add(longPlayer);
            session.Players.Add(toAnPlayer);

            if (withExistingPreference)
            {
                var preference = new TeamPreferenceGroup
                {
                    Id = $"pref-{suffix}",
                    SessionId = session.Id,
                    Session = session
                };
                preference.Players.Add(new TeamPreferenceGroupPlayer
                {
                    TeamPreferenceGroupId = preference.Id,
                    TeamPreferenceGroup = preference,
                    SessionPlayerId = longPlayer.Id,
                    SessionPlayer = longPlayer,
                    RotationOrder = 0
                });
                preference.Players.Add(new TeamPreferenceGroupPlayer
                {
                    TeamPreferenceGroupId = preference.Id,
                    TeamPreferenceGroup = preference,
                    SessionPlayerId = toAnPlayer.Id,
                    SessionPlayer = toAnPlayer,
                    RotationOrder = 1
                });
                db.TeamPreferenceGroups.Add(preference);
            }

            db.MatchSessions.Add(session);
        }

        private static SessionPlayer Player(string id, string sessionId, PlayerProfile profile) => new()
        {
            Id = id,
            SessionId = sessionId,
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            DisplayName = profile.DisplayName,
            IsPresent = true,
            Role = PlayerRole.FullStack,
            Level = PlayerLevel.Average,
            Gender = PlayerGender.Unknown,
            Score = 2
        };

        public async ValueTask DisposeAsync()
        {
            bridgeHttpClient.Dispose();
            aiHttpClient.Dispose();
            await Db.DisposeAsync();
            await sqlite.DisposeAsync();
        }
    }

    private sealed class RecordingBridgeHandler : HttpMessageHandler
    {
        private int sendCount;

        public int SendCount => sendCount;
        public string LastMessage { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.EndsWith("/v1/group-messages", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            sendCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            LastMessage = document.RootElement.GetProperty("message").GetString() ?? string.Empty;
            var payload = JsonSerializer.Serialize(new
            {
                sent = true,
                messageId = $"provider-team-pref-exit-{sendCount}"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class NoAiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("AI must not be called by deterministic TeamPreference regression.");
    }
}
