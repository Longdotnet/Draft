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

public sealed class ZaloGroundedReadOnlySemanticPlannerTests
{
    [Fact]
    public void Generic_gate_does_not_require_domain_keywords()
    {
        var incoming = Message(
            "generic-q",
            "user-binh",
            "Bình",
            "vậy người bên cạnh có được không?");
        var ambient = new ZaloAmbientSettings(true, false, 60, 5, 40, 2, 8);
        var settings = new ZaloReadOnlySemanticSettings(true, .85, 12, 20, 100);

        Assert.True(ZaloReadOnlySemanticGate.IsEligible(incoming, ambient, settings));
        Assert.False(ZaloAmbientDomainIntentResolver.LooksLikeCandidate(incoming));
    }

    [Fact]
    public async Task Planner_receives_ranked_context_and_snapshot_for_keyword_free_question()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            Id = "stored-context-1",
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            MessageId = "context-1",
            SenderId = "user-long",
            SenderName = "Long",
            Content = "chắc tối nay tui nghỉ",
            SentAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        });
        await fixture.Db.SaveChangesAsync();

        var incoming = Message(
            "generic-ai",
            "user-binh",
            "Bình",
            "vậy người đó có được không?");
        var context = await ZaloReadOnlyConversationContextLoader.LoadAsync(
            fixture.Db,
            "conn-1",
            "g1",
            incoming,
            ["context-1"],
            12);
        var snapshot = await new ZaloReadOnlyGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-binh");
        var handler = new CapturingAiHandler(
            "{\"route\":\"ReadOnlyQuestion\",\"factKind\":\"SessionSchedule\",\"confidence\":0.96,\"sessionId\":\"session-t6\",\"subjectMemberId\":null,\"subjectIsCurrentSender\":false,\"referencedMemberId\":null,\"sourceMessageId\":\"context-1\",\"openOfferId\":null,\"needsClarification\":false,\"reason\":\"context_resolved\"}");
        using var client = new HttpClient(handler);
        var settings = new ZaloReadOnlySemanticSettings(true, .85, 12, 20, 100);

        var plan = await new ZaloReadOnlySemanticPlanner(
                AiConfiguration(),
                NullLogger<ZaloOverbookService>.Instance,
                client)
            .PlanAsync("conn-1", "g1", incoming, context, snapshot, settings);

        Assert.Equal(ZaloReadOnlySemanticRoute.ReadOnlyQuestion, plan.Route);
        Assert.Equal(ZaloReadOnlyFactKind.SessionSchedule, plan.FactKind);
        Assert.Equal("session-t6", plan.SessionId);
        using var requestDocument = JsonDocument.Parse(handler.LastRequestBody);
        var modelInput = requestDocument.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString() ?? string.Empty;
        using var modelInputDocument = JsonDocument.Parse(modelInput);
        var modelRoot = modelInputDocument.RootElement;
        var conversationContext = modelRoot.GetProperty("ConversationContext");
        Assert.Single(conversationContext.EnumerateArray());
        Assert.Equal(
            "chắc tối nay tui nghỉ",
            conversationContext[0].GetProperty("Content").GetString());
        var groundingSessions = modelRoot
            .GetProperty("GroundingSnapshot")
            .GetProperty("Sessions");
        Assert.Contains(
            groundingSessions.EnumerateArray(),
            item => item.GetProperty("SessionId").GetString() == "session-t6");
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Mutation_request_is_rejected_by_readonly_validator()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await BuildSnapshotAsync(fixture);
        var plan = Plan(
            ZaloReadOnlySemanticRoute.MutationRequest,
            ZaloReadOnlyFactKind.CanMemberTakeSlot,
            sessionId: "session-t6",
            subjectMemberId: "player-nam",
            referencedMemberId: "player-long",
            reason: "imperative_add_member");

        var result = ZaloReadOnlySemanticPlanValidator.Validate(
            plan,
            Message("mutation", "user-binh", "Bình", "cho Nam vô đi"),
            EmptyContext(),
            snapshot,
            Settings());

        Assert.False(result.Accepted);
        Assert.Equal("semantic_mutation_request", result.Reason);
    }

    [Fact]
    public async Task Fabricated_session_id_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await BuildSnapshotAsync(fixture);
        var plan = Plan(
            ZaloReadOnlySemanticRoute.ReadOnlyQuestion,
            ZaloReadOnlyFactKind.SessionSchedule,
            sessionId: "session-made-up");

        var result = ZaloReadOnlySemanticPlanValidator.Validate(
            plan,
            Message("fake-session", "user-binh", "Bình", "bữa đó mấy giờ?"),
            EmptyContext(),
            snapshot,
            Settings());

        Assert.False(result.Accepted);
        Assert.Equal("semantic_invalid_entity", result.Reason);
    }

    [Fact]
    public async Task Fabricated_member_id_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await BuildSnapshotAsync(fixture);
        var plan = Plan(
            ZaloReadOnlySemanticRoute.ReadOnlyQuestion,
            ZaloReadOnlyFactKind.MemberTeam,
            sessionId: "session-t6",
            subjectMemberId: "player-made-up");

        var result = ZaloReadOnlySemanticPlanValidator.Validate(
            plan,
            Message("fake-member", "user-binh", "Bình", "người đó bên nào?"),
            EmptyContext(),
            snapshot,
            Settings());

        Assert.False(result.Accepted);
        Assert.Equal("semantic_invalid_entity", result.Reason);
    }

    [Fact]
    public async Task Equal_session_candidates_without_grounded_session_fail_closed()
    {
        await using var fixture = await Fixture.CreateAsync(addSecondSession: true);
        var snapshot = await BuildSnapshotAsync(fixture);
        var plan = Plan(
            ZaloReadOnlySemanticRoute.ReadOnlyQuestion,
            ZaloReadOnlyFactKind.MemberTeam,
            subjectIsCurrentSender: true);

        var result = ZaloReadOnlySemanticPlanValidator.Validate(
            plan,
            Message("ambiguous", "user-binh", "Bình", "còn tui?"),
            EmptyContext(),
            snapshot,
            Settings());

        Assert.False(result.Accepted);
        Assert.Equal("semantic_ambiguous_session", result.Reason);
    }

    [Fact]
    public void Malformed_json_fails_closed()
    {
        var plan = ZaloReadOnlySemanticPlanner.ParsePlan("not-json-at-all");

        Assert.Equal(ZaloReadOnlySemanticRoute.None, plan.Route);
        Assert.Equal("semantic_malformed_json", plan.Reason);
    }

    [Fact]
    public async Task Planner_timeout_fails_closed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await BuildSnapshotAsync(fixture);
        using var client = new HttpClient(new ThrowingAiHandler());
        var incoming = Message("timeout", "user-timeout", "Timeout User", "vậy người đó sao?");

        var plan = await new ZaloReadOnlySemanticPlanner(
                AiConfiguration(),
                NullLogger<ZaloOverbookService>.Instance,
                client)
            .PlanAsync("conn-1", "g1", incoming, EmptyContext(), snapshot, Settings());

        Assert.Equal(ZaloReadOnlySemanticRoute.None, plan.Route);
        Assert.Equal("semantic_ai_error", plan.Reason);
    }

    [Fact]
    public async Task Can_member_take_slot_without_real_offer_does_not_mutate_state()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.Db.SessionPlayers
            .AsNoTracking()
            .Where(player => player.SessionId == "session-t6")
            .Select(player => new { player.Id, player.IsPresent })
            .OrderBy(player => player.Id)
            .ToListAsync();
        var plan = Plan(
            ZaloReadOnlySemanticRoute.ReadOnlyQuestion,
            ZaloReadOnlyFactKind.CanMemberTakeSlot,
            sessionId: "session-t6",
            subjectMemberId: "player-nam",
            referencedMemberId: "player-long",
            reason: "asks_if_nam_can_take_long_reference");

        var reply = await new ZaloReadOnlyGroundedFactResolver(fixture.Db).TryBuildAsync(
            "bot-account",
            "conn-1",
            "g1",
            Message("readonly-no-offer", "user-binh", "Bình", "vậy Nam vô đó được không?"),
            AmbientDecision(),
            plan,
            await BuildSnapshotAsync(fixture));

        var after = await fixture.Db.SessionPlayers
            .AsNoTracking()
            .Where(player => player.SessionId == "session-t6")
            .Select(player => new { player.Id, player.IsPresent })
            .OrderBy(player => player.Id)
            .ToListAsync();
        var offers = await new ZaloOpenSlotOfferStore(fixture.Db)
            .ListClaimableAsync("conn-1", "g1", "user-nam");

        Assert.NotNull(reply);
        Assert.Contains("chưa được pass/mở", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, after);
        Assert.Empty(offers);
    }

    [Fact]
    public async Task Can_member_take_slot_uses_real_open_offer_and_leaves_it_open()
    {
        await using var fixture = await Fixture.CreateAsync();
        var offer = await new ZaloOpenSlotOfferStore(fixture.Db).OpenAsync(
            "conn-1",
            "g1",
            "user-long",
            "Long",
            "session-t6",
            "T6",
            "long-pass",
            DateTimeOffset.UtcNow.AddHours(2),
            DateTimeOffset.UtcNow.AddMinutes(45));
        var plan = Plan(
            ZaloReadOnlySemanticRoute.ReadOnlyQuestion,
            ZaloReadOnlyFactKind.CanMemberTakeSlot,
            sessionId: "session-t6",
            subjectMemberId: "player-nam",
            referencedMemberId: "player-long",
            openOfferId: offer.Id,
            sourceMessageId: "long-pass",
            reason: "real_open_offer");

        var reply = await new ZaloReadOnlyGroundedFactResolver(fixture.Db).TryBuildAsync(
            "bot-account",
            "conn-1",
            "g1",
            Message("readonly-offer", "user-binh", "Bình", "Nam vô đó được không?"),
            AmbientDecision(),
            plan,
            await BuildSnapshotAsync(fixture));
        var stillOpen = await new ZaloOpenSlotOfferStore(fixture.Db)
            .ListClaimableAsync("conn-1", "g1", "user-nam");

        Assert.NotNull(reply);
        Assert.Contains("có thể nhận", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(stillOpen, item => item.Id == offer.Id && item.Status == ZaloOpenSlotOfferStatus.Open);
        Assert.False((await fixture.Db.SessionPlayers.AsNoTracking().SingleAsync(player => player.Id == "player-nam")).IsPresent);
    }

    [Fact]
    public async Task Member_team_resolves_specific_member_from_authoritative_draft_state()
    {
        await using var fixture = await Fixture.CreateAsync(withTeam: true);
        var plan = Plan(
            ZaloReadOnlySemanticRoute.ReadOnlyQuestion,
            ZaloReadOnlyFactKind.MemberTeam,
            sessionId: "session-t6",
            subjectMemberId: "player-long",
            sourceMessageId: "quoted-long",
            reason: "quoted_member_team");
        var context = new ZaloReadOnlyConversationContext([], ["quoted-long"]);
        var validation = ZaloReadOnlySemanticPlanValidator.Validate(
            plan,
            Message("member-team", "user-binh", "Bình", "người đó bên nào?"),
            context,
            await BuildSnapshotAsync(fixture),
            Settings());
        Assert.True(validation.Accepted);

        var reply = await new ZaloReadOnlyGroundedFactResolver(fixture.Db).TryBuildAsync(
            "bot-account",
            "conn-1",
            "g1",
            Message("member-team", "user-binh", "Bình", "người đó bên nào?"),
            AmbientDecision(),
            validation.Plan,
            await BuildSnapshotAsync(fixture));

        Assert.NotNull(reply);
        Assert.Contains("Long", reply!.Text, StringComparison.Ordinal);
        Assert.Contains("Team A", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_sender_team_continuation_resolves_previous_team_topic()
    {
        await using var fixture = await Fixture.CreateAsync(withTeam: true);
        var plan = Plan(
            ZaloReadOnlySemanticRoute.ReadOnlyQuestion,
            ZaloReadOnlyFactKind.MemberTeam,
            sessionId: "session-t6",
            subjectIsCurrentSender: true,
            reason: "team_continuation");

        var reply = await new ZaloReadOnlyGroundedFactResolver(fixture.Db).TryBuildAsync(
            "bot-account",
            "conn-1",
            "g1",
            Message("current-team", "user-long", "Long", "còn tui?"),
            AmbientDecision(),
            plan,
            await BuildSnapshotAsync(fixture));

        Assert.NotNull(reply);
        Assert.Contains("Long", reply!.Text, StringComparison.Ordinal);
        Assert.Contains("Team A", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_sender_continuation_can_resolve_membership_without_phrase_mapping()
    {
        await using var fixture = await Fixture.CreateAsync();
        var plan = Plan(
            ZaloReadOnlySemanticRoute.ReadOnlyQuestion,
            ZaloReadOnlyFactKind.MemberMembership,
            sessionId: "session-t6",
            subjectIsCurrentSender: true,
            reason: "roster_continuation");

        var reply = await new ZaloReadOnlyGroundedFactResolver(fixture.Db).TryBuildAsync(
            "bot-account",
            "conn-1",
            "g1",
            Message("current-membership", "user-long", "Long", "còn tui?"),
            AmbientDecision(),
            plan,
            await BuildSnapshotAsync(fixture));

        Assert.NotNull(reply);
        Assert.Contains("Long đang có tên", reply!.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("team T6 xong chưa?", ZaloBotIntent.TeamLineup)]
    [InlineData("waitlist T6 hiện sao?", ZaloBotIntent.WaitlistStatus)]
    [InlineData("lịch nhắc T6 hiện sao?", ZaloBotIntent.ReminderStatus)]
    public void Existing_natural_readonly_fast_paths_remain_supported(string content, ZaloBotIntent expected)
    {
        Assert.True(ZaloAmbientReadOnlyNaturalIntentResolver.TryResolve(content, out var intent));
        Assert.Equal(expected, intent);
    }

    private static async Task<ZaloReadOnlyGroundingSnapshot> BuildSnapshotAsync(Fixture fixture) =>
        await new ZaloReadOnlyGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-binh");

    private static ZaloReadOnlyConversationContext EmptyContext() => new([], []);

    private static ZaloReadOnlySemanticSettings Settings() => new(true, .85, 12, 20, 100);

    private static ZaloReadOnlySemanticPlan Plan(
        ZaloReadOnlySemanticRoute route,
        ZaloReadOnlyFactKind factKind,
        double confidence = .96,
        string? sessionId = null,
        string? subjectMemberId = null,
        bool subjectIsCurrentSender = false,
        string? referencedMemberId = null,
        string? sourceMessageId = null,
        string? openOfferId = null,
        bool needsClarification = false,
        string reason = "test") => new(
            route,
            factKind,
            confidence,
            sessionId,
            subjectMemberId,
            subjectIsCurrentSender,
            referencedMemberId,
            sourceMessageId,
            openOfferId,
            needsClarification,
            reason);

    private static ZaloAmbientParticipationDecision AmbientDecision() => new(
        true,
        100,
        ZaloAmbientParticipationKind.Fact,
        ZaloBotIntent.Unknown.ToString(),
        1,
        [],
        new ZaloAmbientGroupSituation(0, 0, 0, 0, null, []));

    private static IConfiguration AiConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Endpoint"] = "https://ai.test/v1/chat/completions",
            ["Ai:ApiKey"] = "test-key",
            ["Ai:Model"] = "test-model",
            ["ZaloBot:AiPerUserPerMinute"] = "20",
            ["ZaloBot:AiPerGroupPerMinute"] = "100"
        })
        .Build();

    private static ZaloIncomingMessageEvent Message(
        string messageId,
        string senderId,
        string senderName,
        string content,
        ZaloBridgeMessageQuote? quote = null) => new(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: messageId,
            senderId: senderId,
            senderName: senderName,
            content: content,
            mentions: [],
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

    private sealed class ThrowingAiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new TaskCanceledException("test timeout");
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
            bool addSecondSession = false,
            bool withTeam = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-readonly",
                DisplayName = "Admin",
                Email = $"readonly-{Guid.NewGuid():n}@example.test",
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
            var longProfile = new PlayerProfile
            {
                Id = "profile-long",
                ZaloUserId = "user-long",
                DisplayName = "Long"
            };
            var namProfile = new PlayerProfile
            {
                Id = "profile-nam",
                ZaloUserId = "user-nam",
                DisplayName = "Nam"
            };
            var longPlayer = new SessionPlayer
            {
                Id = "player-long",
                PlayerProfileId = longProfile.Id,
                PlayerProfile = longProfile,
                DisplayName = "Long",
                IsPresent = true
            };
            var namPlayer = new SessionPlayer
            {
                Id = "player-nam",
                PlayerProfileId = namProfile.Id,
                PlayerProfile = namProfile,
                DisplayName = "Nam",
                IsPresent = false
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
            longPlayer.SessionId = session.Id;
            longPlayer.Session = session;
            namPlayer.SessionId = session.Id;
            namPlayer.Session = session;
            session.Players.Add(longPlayer);
            session.Players.Add(namPlayer);

            if (withTeam)
            {
                var team = new Team
                {
                    Id = "team-a",
                    SessionId = session.Id,
                    Session = session,
                    Name = "Team A"
                };
                var slot = new DraftSlot
                {
                    Id = "draft-slot-long",
                    SessionId = session.Id,
                    Session = session,
                    DisplayName = "Long",
                    AssignedTeamId = team.Id,
                    AssignedTeam = team
                };
                var slotPlayer = new DraftSlotPlayer
                {
                    Id = "draft-slot-player-long",
                    DraftSlotId = slot.Id,
                    DraftSlot = slot,
                    SessionPlayerId = longPlayer.Id,
                    SessionPlayer = longPlayer
                };
                slot.Players.Add(slotPlayer);
                team.AssignedSlots.Add(slot);
                session.Teams.Add(team);
                session.DraftSlots.Add(slot);
            }

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.AddRange(longProfile, namProfile);
            db.MatchSessions.Add(session);

            if (addSecondSession)
            {
                db.MatchSessions.Add(new MatchSession
                {
                    Id = "session-cn",
                    Name = "CN",
                    AdminUserId = admin.Id,
                    AdminUser = admin,
                    ZaloConnectionId = zalo.Id,
                    ZaloConnection = zalo,
                    ZaloGroupId = "g1",
                    BotEnabled = true,
                    Status = SessionStatus.Setup,
                    StartTime = DateTimeOffset.UtcNow.AddDays(2),
                    TeamCount = 3,
                    TeamSize = 6
                });
            }

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
