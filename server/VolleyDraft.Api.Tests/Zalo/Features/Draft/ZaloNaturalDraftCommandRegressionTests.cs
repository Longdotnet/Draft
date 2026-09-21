using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloNaturalDraftCommandRegressionTests
{
    [Fact]
    public async Task Bare_draft_di_without_pending_executes_the_only_ready_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sessionId = await fixture.SeedReadySessionAsync(
            "T6",
            DateTimeOffset.UtcNow.AddHours(4));

        var handled = await fixture.Service.TryHandleZaloConfirmationAsync(
            fixture.Incoming("draft-bare", "draft đi"));

        Assert.True(handled);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            SessionStatus.Finished,
            await fixture.Db.MatchSessions
                .Where(item => item.Id == sessionId)
                .Select(item => item.Status)
                .SingleAsync());
        var action = Assert.Single(await fixture.Db.ZaloBotActionHistory
            .AsNoTracking()
            .Where(item => item.SessionId == sessionId && item.ActionType == "AutoDraft")
            .ToListAsync());
        Assert.Equal(Fixture.LeaderZaloId, action.ActorZaloUserId);
        Assert.Empty(await fixture.Db.ZaloBotConversationStates
            .AsNoTracking()
            .Where(item =>
                item.ZaloConnectionId == Fixture.ConnectionId &&
                item.GroupId == Fixture.GroupId &&
                item.SenderZaloUserId == Fixture.LeaderZaloId)
            .ToListAsync());
        Assert.Single(fixture.Bridge.GroupMessages);
        Assert.Contains("Đã tự draft xong", fixture.Bridge.GroupMessages[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_draft_di_other_session_preserves_pending_approval_and_drafts_exact_target()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pendingSessionId = await fixture.SeedReadySessionAsync(
            "T6",
            ZaloTestDates.Next(DayOfWeek.Friday));
        var targetSessionId = await fixture.SeedReadySessionAsync(
            "T7",
            ZaloTestDates.Next(DayOfWeek.Saturday));
        var pendingRequest = await fixture.SeedPendingApprovalAsync(pendingSessionId);

        var handled = await fixture.Service.TryHandleZaloConfirmationAsync(
            fixture.Incoming("draft-explicit-other", "draft đi T7"));

        Assert.True(handled);
        fixture.Db.ChangeTracker.Clear();

        var statuses = await fixture.Db.MatchSessions
            .AsNoTracking()
            .Where(item => item.Id == pendingSessionId || item.Id == targetSessionId)
            .ToDictionaryAsync(item => item.Id, item => item.Status);
        Assert.NotEqual(SessionStatus.Finished, statuses[pendingSessionId]);
        Assert.Equal(SessionStatus.Finished, statuses[targetSessionId]);

        var storedPending = await new ZaloDraftEscalationStore(fixture.Db)
            .LoadForSessionAsync(Fixture.ConnectionId, Fixture.GroupId, pendingSessionId);
        Assert.NotNull(storedPending);
        Assert.Equal(pendingRequest.Id, storedPending!.Id);
        Assert.Equal(ZaloDraftEscalationState.ApproverTagged, storedPending.State);
        Assert.Equal(Fixture.LeaderZaloId, storedPending.PrimaryApproverId);

        Assert.Empty(await fixture.Db.ZaloBotActionHistory
            .AsNoTracking()
            .Where(item => item.SessionId == pendingSessionId && item.ActionType == "AutoDraft")
            .ToListAsync());
        Assert.Single(await fixture.Db.ZaloBotActionHistory
            .AsNoTracking()
            .Where(item => item.SessionId == targetSessionId && item.ActionType == "AutoDraft")
            .ToListAsync());
    }

    [Fact]
    public async Task Explicit_other_session_ignores_stale_quote_from_pending_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pendingSessionId = await fixture.SeedReadySessionAsync(
            "T6",
            ZaloTestDates.Next(DayOfWeek.Friday));
        var targetSessionId = await fixture.SeedReadySessionAsync(
            "T7",
            ZaloTestDates.Next(DayOfWeek.Saturday));
        await fixture.SeedPendingApprovalAsync(pendingSessionId);

        var handled = await fixture.Service.TryHandleZaloConfirmationAsync(
            fixture.Incoming(
                "draft-explicit-stale-quote",
                "draft đi T7",
                new ZaloBridgeMessageQuote(
                    "pending-approval-prompt",
                    "bot-account",
                    "Npc",
                    "draft đi",
                    "text",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    null)));

        Assert.True(handled);
        fixture.Db.ChangeTracker.Clear();
        var statuses = await fixture.Db.MatchSessions
            .AsNoTracking()
            .Where(item => item.Id == pendingSessionId || item.Id == targetSessionId)
            .ToDictionaryAsync(item => item.Id, item => item.Status);
        Assert.NotEqual(SessionStatus.Finished, statuses[pendingSessionId]);
        Assert.Equal(SessionStatus.Finished, statuses[targetSessionId]);
        Assert.Single(await fixture.Db.ZaloBotActionHistory
            .AsNoTracking()
            .Where(item => item.SessionId == targetSessionId && item.ActionType == "AutoDraft")
            .ToListAsync());
    }

    [Fact]
    public async Task Production_ingress_bare_draft_hands_off_lease_and_executes_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sessionId = await fixture.SeedReadySessionAsync(
            "T6",
            DateTimeOffset.UtcNow.AddHours(4));
        var incoming = fixture.Incoming("draft-ingress", "draft đi");

        var result = await fixture.Coordinator.HandleAsync(incoming);

        Assert.True(result.Accepted);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            SessionStatus.Finished,
            await fixture.Db.MatchSessions
                .AsNoTracking()
                .Where(item => item.Id == sessionId)
                .Select(item => item.Status)
                .SingleAsync());
        Assert.Single(await fixture.Db.ZaloBotActionHistory
            .AsNoTracking()
            .Where(item => item.SessionId == sessionId && item.ActionType == "AutoDraft")
            .ToListAsync());
        var storedMessage = await fixture.Db.ZaloGroupMessages
            .AsNoTracking()
            .SingleAsync(item => item.ZaloConnectionId == Fixture.ConnectionId && item.MessageId == incoming.MessageId);
        Assert.NotEqual("pre_route_handled", storedMessage.ReplyOutcome);
        Assert.NotNull(storedMessage.BotReplySentAt);
    }

    [Fact]
    public async Task Explicit_unavailable_session_never_falls_back_to_only_ready_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        var unavailableSessionId = await fixture.SeedReadySessionAsync(
            "T4",
            ZaloTestDates.Next(DayOfWeek.Wednesday));
        var readySessionId = await fixture.SeedReadySessionAsync(
            "T6",
            ZaloTestDates.Next(DayOfWeek.Friday));
        await fixture.Db.MatchSessions
            .Where(item => item.Id == unavailableSessionId)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.Status, SessionStatus.Cancelled));
        fixture.Db.ChangeTracker.Clear();

        var handled = await fixture.Service.TryHandleZaloConfirmationAsync(
            fixture.Incoming("draft-explicit-unavailable", "draft đi T4"));

        Assert.True(handled);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            SessionStatus.Cancelled,
            await fixture.Db.MatchSessions
                .AsNoTracking()
                .Where(item => item.Id == unavailableSessionId)
                .Select(item => item.Status)
                .SingleAsync());
        Assert.NotEqual(
            SessionStatus.Finished,
            await fixture.Db.MatchSessions
                .AsNoTracking()
                .Where(item => item.Id == readySessionId)
                .Select(item => item.Status)
                .SingleAsync());
        Assert.Empty(await fixture.Db.ZaloBotActionHistory
            .AsNoTracking()
            .Where(item => item.SessionId == readySessionId && item.ActionType == "AutoDraft")
            .ToListAsync());
        var reply = Assert.Single(fixture.Bridge.GroupMessages);
        Assert.Contains("không còn ở phạm vi", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nested_post_mutation_result_send_failure_emits_visible_no_redraft_recovery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sessionId = await fixture.SeedReadySessionAsync(
            "T6",
            DateTimeOffset.UtcNow.AddHours(4));
        fixture.Bridge.FailNextGroupMessageSends = 1;

        var handled = await fixture.Service.TryHandleZaloConfirmationAsync(
            fixture.Incoming("draft-send-failure", "draft đi"));

        Assert.True(handled);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            SessionStatus.Finished,
            await fixture.Db.MatchSessions
                .AsNoTracking()
                .Where(item => item.Id == sessionId)
                .Select(item => item.Status)
                .SingleAsync());

        Assert.Equal(2, fixture.Bridge.GroupMessageAttempts);
        Assert.Equal(2, fixture.Bridge.GroupMessages.Count);
        var recovery = fixture.Bridge.GroupMessages[1];
        Assert.Contains("@Npc 10", recovery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không cần draft lại", recovery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("draft đi", recovery, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string ConnectionId = "conn-1";
        public const string GroupId = "g1";
        public const string LeaderZaloId = "leader-zalo";

        private readonly SqliteConnection connection;
        private readonly HttpClient bridgeHttpClient;
        private readonly HttpClient aiHttpClient;
        private readonly HttpClient avatarHttpClient;
        private readonly MemoryCache memoryCache;
        private readonly ZaloCredentialProtector protector;
        private int sessionSequence;

        private Fixture(
            SqliteConnection connection,
            VolleyDraftDbContext db,
            RecordingBridgeHandler bridge,
            ZaloOverbookService service,
            ZaloInboundCoordinator coordinator,
            HttpClient bridgeHttpClient,
            HttpClient aiHttpClient,
            HttpClient avatarHttpClient,
            MemoryCache memoryCache,
            ZaloCredentialProtector protector)
        {
            this.connection = connection;
            Db = db;
            Bridge = bridge;
            Service = service;
            Coordinator = coordinator;
            this.bridgeHttpClient = bridgeHttpClient;
            this.aiHttpClient = aiHttpClient;
            this.avatarHttpClient = avatarHttpClient;
            this.memoryCache = memoryCache;
            this.protector = protector;
        }

        public VolleyDraftDbContext Db { get; }
        public RecordingBridgeHandler Bridge { get; }
        public ZaloOverbookService Service { get; }
        public ZaloInboundCoordinator Coordinator { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(
                new DbContextOptionsBuilder<VolleyDraftDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(db);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zalo:CredentialEncryptionKey"] = "natural-draft-regression-key",
                    ["ZaloBot:Ambient:Enabled"] = "false",
                    ["ZaloBot:AiStyleEnabled"] = "false",
                    ["ZaloBot:ExactCommandCooldownSeconds"] = "0",
                    ["ZaloBot:DraftAutopilot:Enabled"] = "true",
                    ["ZaloBot:DraftAutopilot:NaturalReadinessEnabled"] = "true"
                })
                .Build();
            var protector = new ZaloCredentialProtector(configuration);
            var bridgeHandler = new RecordingBridgeHandler();
            var bridgeHttpClient = new HttpClient(bridgeHandler)
            {
                BaseAddress = new Uri("https://bridge.test/")
            };
            var bridge = new ZaloBridgeClient(bridgeHttpClient);

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"natural-draft-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var zalo = new ZaloConnection
            {
                Id = ConnectionId,
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = protector.Protect("{}"),
                Status = ZaloConnectionStatus.Connected
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            await db.SaveChangesAsync();

            var integration = new ZaloIntegrationService(
                db,
                bridge,
                protector,
                null!,
                null!,
                null!);

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

            var avatarHttpClient = new HttpClient(new NoExternalHttpHandler());
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var teamCards = new ZaloTeamCardService(
                db,
                integration,
                configuration,
                new StubHttpClientFactory(avatarHttpClient),
                memoryCache,
                NullLogger<ZaloTeamCardService>.Instance);

            var bot = new ZaloBotService(
                db,
                bridge,
                ai,
                integration,
                draft,
                teamCards,
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
                protector,
                integration,
                ai,
                configuration,
                NullLogger<ZaloOverbookService>.Instance,
                bot);
            var coordinator = new ZaloInboundCoordinator(
                db,
                service,
                bot,
                NullLogger<ZaloInboundCoordinator>.Instance);

            return new Fixture(
                connection,
                db,
                bridgeHandler,
                service,
                coordinator,
                bridgeHttpClient,
                aiHttpClient,
                avatarHttpClient,
                memoryCache,
                protector);
        }

        public async Task<string> SeedReadySessionAsync(string name, DateTimeOffset startTime)
        {
            var suffix = Interlocked.Increment(ref sessionSequence);
            var pollId = $"poll-{suffix}";
            var optionId = $"{pollId}-option";
            var draft = new SessionDraftService(Db);
            var created = await draft.CreateSessionAsync(
                "admin-1",
                new CreateSessionRequest(name, 3, 2));
            Assert.True(created.IsSuccess, created.Error);
            var sessionId = created.Value!.Id;

            var session = await Db.MatchSessions.SingleAsync(item => item.Id == sessionId);
            session.ZaloConnectionId = ConnectionId;
            session.ZaloGroupId = GroupId;
            session.BotEnabled = true;
            session.StartTime = startTime;
            await Db.SaveChangesAsync();

            var playerIds = new List<string>();
            for (var index = 1; index <= 6; index += 1)
            {
                var added = await draft.AddPlayerAsync(
                    "admin-1",
                    sessionId,
                    new AddPlayerRequest(
                        $"{name}-P{index}",
                        PlayerRole.New,
                        PlayerLevel.New,
                        PlayerGender.Male));
                Assert.True(added.IsSuccess, added.Error);
                playerIds.Add(added.Value!.Id);
            }

            var pollMembers = new List<BridgeMember>();
            for (var index = 0; index < playerIds.Count; index += 1)
            {
                var zaloUserId = $"zalo-{suffix}-{index + 1}";
                var displayName = $"{name}-P{index + 1}";
                var profile = new PlayerProfile
                {
                    Id = $"profile-{suffix}-{index + 1}",
                    ZaloUserId = zaloUserId,
                    DisplayName = displayName,
                    Gender = PlayerGender.Male,
                    DefaultRole = PlayerRole.New,
                    DefaultLevel = PlayerLevel.New
                };
                Db.PlayerProfiles.Add(profile);
                var player = await Db.SessionPlayers.SingleAsync(item => item.Id == playerIds[index]);
                player.PlayerProfileId = profile.Id;
                player.PlayerProfile = profile;
                player.SourcePollId = pollId;
                pollMembers.Add(new BridgeMember(zaloUserId, displayName, displayName, null));
            }
            await Db.SaveChangesAsync();

            var captains = await draft.SetManualCaptainsAsync(
                "admin-1",
                sessionId,
                new ManualCaptainsRequest(playerIds.Take(3).ToList()));
            Assert.True(captains.IsSuccess, captains.Error);

            Bridge.RegisterPoll(pollId, optionId, pollMembers);
            Db.PollImports.Add(new PollImport
            {
                Id = $"poll-import-{suffix}",
                SessionId = sessionId,
                ImportedByUserId = "admin-1",
                ZaloGroupId = GroupId,
                PollId = pollId,
                PollQuestion = name,
                SelectedOptionIdsJson = JsonSerializer.Serialize(new[] { optionId }),
                ImportedPlayerCount = pollMembers.Count
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return sessionId;
        }

        public async Task<ZaloDraftEscalationSnapshot> SeedPendingApprovalAsync(string sessionId)
        {
            var readiness = await new ZaloDraftReadinessService(Db)
                .BuildAsync(sessionId, DateTimeOffset.UtcNow);
            Assert.NotNull(readiness);
            Assert.True(readiness!.CanEscalate, readiness.ReasonCode);

            var store = new ZaloDraftEscalationStore(Db);
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(20);
            var created = await store.CreateOrReuseAsync(
                ConnectionId,
                GroupId,
                sessionId,
                "Member",
                "requester-zalo",
                "Requester",
                "request-message",
                readiness.Fingerprint,
                ZaloDraftEscalationState.AwaitingRequesterConsent,
                expiresAt);
            await store.SetPrimaryApproverAsync(
                created.Id,
                LeaderZaloId,
                "pending-approval-prompt",
                DateTimeOffset.UtcNow,
                expiresAt);
            return (await store.LoadForSessionAsync(ConnectionId, GroupId, sessionId))!;
        }

        public ZaloIncomingMessageEvent Incoming(
            string messageId,
            string content,
            ZaloBridgeMessageQuote? quote = null) => new(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: GroupId,
            messageId: messageId,
            senderId: LeaderZaloId,
            senderName: "Leader",
            content: content,
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            quote: quote);

        public async ValueTask DisposeAsync()
        {
            memoryCache.Dispose();
            avatarHttpClient.Dispose();
            aiHttpClient.Dispose();
            bridgeHttpClient.Dispose();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RecordingBridgeHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, BridgePoll> polls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BridgeMember> members = new(StringComparer.Ordinal);

        public int FailNextGroupMessageSends { get; set; }
        public int GroupMessageAttempts { get; private set; }
        public List<string> GroupMessages { get; } = [];

        public void RegisterPoll(
            string pollId,
            string optionId,
            IReadOnlyList<BridgeMember> pollMembers)
        {
            foreach (var member in pollMembers)
                members[member.ZaloUserId] = member;
            polls[pollId] = new BridgePoll(
                pollId,
                $"Poll {pollId}",
                Fixture.LeaderZaloId,
                [new BridgePollOption(
                    optionId,
                    "Play",
                    pollMembers.Count,
                    pollMembers.Select(item => item.ZaloUserId).ToList())],
                false,
                false,
                false,
                false,
                pollMembers.Count,
                DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                0);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Post &&
                path.Equals($"/v1/groups/{Fixture.GroupId}/roles", StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.OK,
                    new BridgeGroupRoles(Fixture.GroupId, Fixture.LeaderZaloId, []));
            }

            if (request.Method == HttpMethod.Post && path.StartsWith("/v1/polls/", StringComparison.Ordinal))
            {
                var pollId = Uri.UnescapeDataString(path["/v1/polls/".Length..]);
                if (!polls.TryGetValue(pollId, out var poll))
                    return Json(HttpStatusCode.NotFound, new { error = $"unknown poll {pollId}" });
                return Json(
                    HttpStatusCode.OK,
                    poll);
            }

            if (request.Method == HttpMethod.Post &&
                path.Equals($"/v1/groups/{Fixture.GroupId}/polls", StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.OK,
                    new BridgePollsResponse(polls.Values.ToList()));
            }

            if (request.Method == HttpMethod.Post &&
                path.Equals("/v1/group-members", StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.OK,
                    new BridgeMembersResponse(members.Values.ToList()));
            }

            if (request.Method == HttpMethod.Post &&
                path.Equals("/v1/group-messages", StringComparison.Ordinal))
            {
                GroupMessageAttempts += 1;
                var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var body = JsonDocument.Parse(raw);
                GroupMessages.Add(body.RootElement.GetProperty("message").GetString() ?? string.Empty);

                if (FailNextGroupMessageSends > 0)
                {
                    FailNextGroupMessageSends -= 1;
                    return Json(
                        HttpStatusCode.InternalServerError,
                        new { error = "simulated post-mutation send failure" });
                }

                return Json(
                    HttpStatusCode.OK,
                    new
                    {
                        sent = true,
                        mock = false,
                        messageId = $"provider-draft-{GroupMessageAttempts}"
                    });
            }

            return Json(HttpStatusCode.NotFound, new { error = $"unexpected test request {path}" });
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, object payload) => new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

    }

    private sealed class NoAiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("AI must not be called by natural draft routing.");
    }

    private sealed class NoExternalHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("External avatar HTTP must not be called by this fixture.");
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
