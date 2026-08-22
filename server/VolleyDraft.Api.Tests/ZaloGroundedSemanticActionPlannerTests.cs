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

public sealed class ZaloGroundedSemanticActionPlannerTests
{
    [Fact]
    public void Generic_action_gate_does_not_require_legacy_keywords()
    {
        var incoming = Message(
            "generic-action",
            "user-huy",
            "Huy",
            "bữa đó phần của mình để người khác chơi nha");
        var ambient = new ZaloAmbientSettings(true, false, 60, 5, 40, 2, 8);
        var settings = Settings();

        Assert.True(ZaloSemanticActionGate.IsEligible(incoming, ambient, settings));
        Assert.False(ZaloAmbientDomainIntentResolver.LooksLikeCandidate(incoming));
    }

    [Fact]
    public void Parser_keeps_multi_target_apply_exclude_and_uncertain_semantics()
    {
        var plan = ZaloSemanticActionPlanner.ParsePlan("""
            {
              "route":"MutationRequest",
              "action":"PassOwnSlot",
              "confidence":0.98,
              "actorKind":"CurrentSender",
              "actorMemberId":"zalo:user-huy",
              "targets":[
                {"referenceText":"T6","resolvedDate":"2026-08-21","sessionId":"session-t6","referencedMemberId":null,"openOfferId":null,"disposition":"Apply","confidence":0.99},
                {"referenceText":"CN","resolvedDate":"2026-08-23","sessionId":"session-cn","referencedMemberId":null,"openOfferId":null,"disposition":"Exclude","confidence":0.98},
                {"referenceText":"kèo sau","resolvedDate":null,"sessionId":null,"referencedMemberId":null,"openOfferId":null,"disposition":"Uncertain","confidence":0.91}
              ],
              "needsClarification":false,
              "reason":"multi_target"
            }
            """);

        Assert.Equal(ZaloSemanticActionRoute.MutationRequest, plan.Route);
        Assert.Equal(ZaloSemanticActionKind.PassOwnSlot, plan.Action);
        Assert.Equal(3, plan.Targets.Count);
        Assert.Equal(ZaloSemanticActionTargetDisposition.Apply, plan.Targets[0].Disposition);
        Assert.Equal(ZaloSemanticActionTargetDisposition.Exclude, plan.Targets[1].Disposition);
        Assert.Equal(ZaloSemanticActionTargetDisposition.Uncertain, plan.Targets[2].Disposition);
    }

    [Fact]
    public async Task Planner_receives_shared_context_local_time_and_bounded_grounding_snapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Message("planner-action", "user-huy", "Huy", "ừ phần đó để người khác nha");
        var context = new ZaloReadOnlyConversationContext(
            [new ZaloAiMessage(
                "user",
                "user-huy",
                "Huy",
                "bữa đó chắc tui nghỉ",
                DateTimeOffset.UtcNow.AddSeconds(-10))],
            ["previous-action-turn"]);
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-huy");
        var session = snapshot.Sessions.Single();
        var answer = $$"""
            {"route":"MutationRequest","action":"PassOwnSlot","confidence":0.97,"actorKind":"CurrentSender","actorMemberId":"{{snapshot.CurrentSender.MemberId}}","targets":[{"referenceText":"phần đó","resolvedDate":"{{session.LocalDate}}","sessionId":"{{session.SessionId}}","referencedMemberId":null,"openOfferId":null,"disposition":"Apply","confidence":0.98}],"needsClarification":false,"reason":"context_resolved"}
            """;
        var handler = new CapturingAiHandler(answer);
        using var client = new HttpClient(handler);

        var plan = await new ZaloSemanticActionPlanner(
                AiConfiguration(),
                NullLogger<ZaloOverbookService>.Instance,
                client)
            .PlanAsync("conn-1", "g1", incoming, context, snapshot, Settings());

        Assert.Equal(ZaloSemanticActionKind.PassOwnSlot, plan.Action);
        Assert.Single(plan.Targets);
        Assert.Contains("bữa đó chắc tui nghỉ", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("CurrentLocalDateTime", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("Asia/Ho_Chi_Minh", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains(session.SessionId, handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Core_reproduction_executes_existing_target_and_reports_unconfigured_target_without_fabrication()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incomingText = "em pass slot hôm nay v cn ạ @All";
        var allPos = incomingText.IndexOf("@All", StringComparison.Ordinal);
        var incoming = Message(
            "core-repro",
            "user-huy",
            "Huy",
            incomingText,
            [new ZaloBridgeMention("broadcast-all", allPos, 4)]);
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-huy");
        var today = snapshot.Sessions.Single();
        var plan = MutationPlan(
            ZaloSemanticActionKind.PassOwnSlot,
            snapshot,
            new ZaloSemanticActionTarget("hôm nay", today.LocalDate, today.SessionId, null, null, ZaloSemanticActionTargetDisposition.Apply, .99),
            new ZaloSemanticActionTarget("CN", "2099-08-23", null, null, null, ZaloSemanticActionTargetDisposition.Apply, .98));

        var validation = ZaloSemanticActionPlanValidator.Validate(plan, incoming, snapshot, Settings());
        var execution = await new ZaloSemanticActionExecutor(fixture.Db).ExecuteAsync(
            "conn-1", "g1", incoming, validation, snapshot);
        var reply = ZaloGroundedActionResultComposer.Compose(execution);
        var ownedOffers = await new ZaloOpenSlotOfferStore(fixture.Db)
            .ListOwnedActiveAsync("conn-1", "g1", "user-huy");

        Assert.True(validation.Accepted);
        Assert.Equal("Ready", validation.Targets[0].Code);
        Assert.Equal("SessionNotConfigured", validation.Targets[1].Code);
        Assert.Equal(ZaloSemanticActionExecutionStatus.Success, execution.Results[0].Status);
        Assert.Equal("OfferOpened", execution.Results[0].Code);
        Assert.Equal(ZaloSemanticActionExecutionStatus.Rejected, execution.Results[1].Status);
        Assert.Equal("SessionNotConfigured", execution.Results[1].Code);
        Assert.Single(ownedOffers);
        Assert.Equal(today.SessionId, ownedOffers[0].SessionId);
        Assert.Equal(1, await fixture.Db.MatchSessions.CountAsync());
        Assert.Contains("pass slot", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CN", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chưa thấy kèo", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_single_target_pass_still_reenters_member_assist_flow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Message("single-pass", "user-huy", "Huy", "em pass slot hôm nay aaaaa @All");
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-huy");
        var session = snapshot.Sessions.Single();
        var plan = MutationPlan(
            ZaloSemanticActionKind.PassOwnSlot,
            snapshot,
            new ZaloSemanticActionTarget("hôm nay", session.LocalDate, session.SessionId, null, null, ZaloSemanticActionTargetDisposition.Apply, .99));
        var validation = ZaloSemanticActionPlanValidator.Validate(plan, incoming, snapshot, Settings());

        var execution = await new ZaloSemanticActionExecutor(fixture.Db).ExecuteAsync(
            "conn-1", "g1", incoming, validation, snapshot);

        Assert.True(validation.Accepted);
        Assert.Single(execution.Results);
        Assert.Equal("OfferOpened", execution.Results[0].Code);
        Assert.Single(await new ZaloOpenSlotOfferStore(fixture.Db)
            .ListOwnedActiveAsync("conn-1", "g1", "user-huy"));
    }

    [Fact]
    public async Task Apply_and_explicit_exclude_mutates_only_apply_target()
    {
        await using var fixture = await Fixture.CreateAsync(addSecondSession: true);
        var incoming = Message("exclude-cn", "user-huy", "Huy", "T6 tui nghỉ còn CN vẫn đánh");
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-huy");
        var sessions = snapshot.Sessions.OrderBy(item => item.StartTime).ToArray();
        var plan = MutationPlan(
            ZaloSemanticActionKind.PassOwnSlot,
            snapshot,
            new ZaloSemanticActionTarget("T6", sessions[0].LocalDate, sessions[0].SessionId, null, null, ZaloSemanticActionTargetDisposition.Apply, .99),
            new ZaloSemanticActionTarget("CN", sessions[1].LocalDate, sessions[1].SessionId, null, null, ZaloSemanticActionTargetDisposition.Exclude, .99));
        var validation = ZaloSemanticActionPlanValidator.Validate(plan, incoming, snapshot, Settings());

        var execution = await new ZaloSemanticActionExecutor(fixture.Db).ExecuteAsync(
            "conn-1", "g1", incoming, validation, snapshot);
        var offers = await new ZaloOpenSlotOfferStore(fixture.Db)
            .ListOwnedActiveAsync("conn-1", "g1", "user-huy");

        Assert.Equal(ZaloSemanticActionExecutionStatus.Success, execution.Results[0].Status);
        Assert.Equal(ZaloSemanticActionExecutionStatus.Skipped, execution.Results[1].Status);
        Assert.Equal("ExplicitExclude", execution.Results[1].Code);
        Assert.Single(offers);
        Assert.Equal(sessions[0].SessionId, offers[0].SessionId);
    }

    [Fact]
    public async Task Two_grounded_targets_execute_independently()
    {
        await using var fixture = await Fixture.CreateAsync(addSecondSession: true);
        var incoming = Message("two-pass", "user-huy", "Huy", "tui nghỉ hai kèo tuần này");
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-huy");
        var sessions = snapshot.Sessions.OrderBy(item => item.StartTime).ToArray();
        var plan = MutationPlan(
            ZaloSemanticActionKind.PassOwnSlot,
            snapshot,
            sessions.Select(session => new ZaloSemanticActionTarget(
                session.Name,
                session.LocalDate,
                session.SessionId,
                null,
                null,
                ZaloSemanticActionTargetDisposition.Apply,
                .99)).ToArray());
        var validation = ZaloSemanticActionPlanValidator.Validate(plan, incoming, snapshot, Settings());

        var execution = await new ZaloSemanticActionExecutor(fixture.Db).ExecuteAsync(
            "conn-1", "g1", incoming, validation, snapshot);
        var offers = await new ZaloOpenSlotOfferStore(fixture.Db)
            .ListOwnedActiveAsync("conn-1", "g1", "user-huy");

        Assert.All(execution.Results, result => Assert.Equal(ZaloSemanticActionExecutionStatus.Success, result.Status));
        Assert.Equal(2, offers.Count);
    }

    [Fact]
    public async Task Uncertain_target_never_mutates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Message("uncertain", "user-huy", "Huy", "CN tui chưa biết");
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-huy");
        var session = snapshot.Sessions.Single();
        var plan = MutationPlan(
            ZaloSemanticActionKind.PassOwnSlot,
            snapshot,
            new ZaloSemanticActionTarget("CN", session.LocalDate, session.SessionId, null, null, ZaloSemanticActionTargetDisposition.Uncertain, .99));
        var validation = ZaloSemanticActionPlanValidator.Validate(plan, incoming, snapshot, Settings());

        var execution = await new ZaloSemanticActionExecutor(fixture.Db).ExecuteAsync(
            "conn-1", "g1", incoming, validation, snapshot);

        Assert.Equal("Uncertain", validation.Targets[0].Code);
        Assert.Equal(ZaloSemanticActionExecutionStatus.Skipped, execution.Results[0].Status);
        Assert.Empty(await new ZaloOpenSlotOfferStore(fixture.Db)
            .ListOwnedActiveAsync("conn-1", "g1", "user-huy"));
    }

    [Fact]
    public async Task Claim_without_real_offer_is_grounded_failure_and_does_not_fabricate_offer()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Message("claim-missing", "user-khang", "Khang", "CN tui lấy nha");
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-khang");
        var session = snapshot.Sessions.Single();
        var plan = MutationPlan(
            ZaloSemanticActionKind.ClaimOpenSlot,
            snapshot,
            new ZaloSemanticActionTarget("CN", session.LocalDate, session.SessionId, null, null, ZaloSemanticActionTargetDisposition.Apply, .99));
        var validation = ZaloSemanticActionPlanValidator.Validate(plan, incoming, snapshot, Settings());

        var execution = await new ZaloSemanticActionExecutor(fixture.Db).ExecuteAsync(
            "conn-1", "g1", incoming, validation, snapshot);
        var reply = ZaloGroundedActionResultComposer.Compose(execution);

        Assert.True(validation.Accepted);
        Assert.Equal("NoGroundedOpenOffer", validation.Targets[0].Code);
        Assert.Equal(ZaloSemanticActionExecutionStatus.Rejected, execution.Results[0].Status);
        Assert.Null(await new ZaloOpenSlotOfferStore(fixture.Db)
            .LoadPendingClaimAsync("conn-1", "g1", "user-khang"));
        Assert.Contains("chưa có slot pass thật", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Keyword_free_claim_uses_real_grounded_offer_and_existing_claim_service()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = await fixture.Db.MatchSessions.AsNoTracking().SingleAsync();
        var offer = await new ZaloOpenSlotOfferStore(fixture.Db).OpenAsync(
            "conn-1",
            "g1",
            "user-huy",
            "Huy",
            session.Id,
            session.Name,
            "pass-source",
            DateTimeOffset.UtcNow.AddHours(2),
            DateTimeOffset.UtcNow.AddMinutes(45));
        var incoming = Message("claim-real", "user-khang", "Khang", "bữa nay tui lấy phần của Huy nha");
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-khang");
        var groundedOffer = snapshot.OpenSlotOffers.Single(item => item.OfferId == offer.Id);
        var plan = MutationPlan(
            ZaloSemanticActionKind.ClaimOpenSlot,
            snapshot,
            new ZaloSemanticActionTarget("phần của Huy", snapshot.Sessions.Single().LocalDate, session.Id, null, groundedOffer.OfferId, ZaloSemanticActionTargetDisposition.Apply, .99));
        var validation = ZaloSemanticActionPlanValidator.Validate(plan, incoming, snapshot, Settings());

        var execution = await new ZaloSemanticActionExecutor(fixture.Db).ExecuteAsync(
            "conn-1", "g1", incoming, validation, snapshot);
        var pending = await new ZaloOpenSlotOfferStore(fixture.Db)
            .LoadPendingClaimAsync("conn-1", "g1", "user-khang");

        Assert.True(validation.Accepted);
        Assert.Equal("Ready", validation.Targets[0].Code);
        Assert.Equal("ClaimPending", execution.Results[0].Code);
        Assert.NotNull(pending);
        Assert.Equal(offer.Id, pending!.Id);
    }

    [Fact]
    public async Task Fabricated_session_or_offer_is_rejected_before_execution()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Message("fake-grounding", "user-huy", "Huy", "tui pass bữa đó");
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-huy");
        var fakeSession = MutationPlan(
            ZaloSemanticActionKind.PassOwnSlot,
            snapshot,
            new ZaloSemanticActionTarget("bữa đó", null, "session-made-up", null, null, ZaloSemanticActionTargetDisposition.Apply, .99));
        var fakeOffer = MutationPlan(
            ZaloSemanticActionKind.ClaimOpenSlot,
            snapshot,
            new ZaloSemanticActionTarget("slot đó", null, snapshot.Sessions.Single().SessionId, null, "offer-made-up", ZaloSemanticActionTargetDisposition.Apply, .99));

        var sessionValidation = ZaloSemanticActionPlanValidator.Validate(fakeSession, incoming, snapshot, Settings());
        var offerValidation = ZaloSemanticActionPlanValidator.Validate(fakeOffer, incoming, snapshot, Settings());

        Assert.False(sessionValidation.Accepted);
        Assert.Equal("semantic_action_invalid_session", sessionValidation.Reason);
        Assert.False(offerValidation.Accepted);
        Assert.Equal("semantic_action_invalid_offer", offerValidation.Reason);
        Assert.Empty(await new ZaloOpenSlotOfferStore(fixture.Db)
            .ListOwnedActiveAsync("conn-1", "g1", "user-huy"));
    }

    [Fact]
    public void Malformed_json_fails_closed()
    {
        var plan = ZaloSemanticActionPlanner.ParsePlan("not-json-at-all");

        Assert.Equal(ZaloSemanticActionRoute.None, plan.Route);
        Assert.Equal("semantic_action_malformed_json", plan.Reason);
    }

    [Fact]
    public async Task Planner_timeout_fails_closed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-huy");
        using var client = new HttpClient(new ThrowingAiHandler());
        var incoming = Message("timeout-action", "user-huy", "Huy", "phần tui bữa đó để người khác nha");

        var plan = await new ZaloSemanticActionPlanner(
                AiConfiguration(),
                NullLogger<ZaloOverbookService>.Instance,
                client)
            .PlanAsync(
                "conn-1",
                "g1",
                incoming,
                new ZaloReadOnlyConversationContext([], []),
                snapshot,
                Settings());

        Assert.Equal(ZaloSemanticActionRoute.None, plan.Route);
        Assert.Equal("semantic_action_ai_error", plan.Reason);
        Assert.Empty(await new ZaloOpenSlotOfferStore(fixture.Db)
            .ListOwnedActiveAsync("conn-1", "g1", "user-huy"));
    }

    [Fact]
    public async Task Readonly_route_never_enters_action_execution()
    {
        await using var fixture = await Fixture.CreateAsync();
        var incoming = Message("readonly-action", "user-khang", "Khang", "Nam vô slot Long được không?");
        var snapshot = await new ZaloActionGroundingSnapshotBuilder(fixture.Db)
            .BuildAsync("conn-1", "g1", "user-khang");
        var plan = new ZaloSemanticActionPlan(
            ZaloSemanticActionRoute.ReadOnlyQuestion,
            ZaloSemanticActionKind.None,
            .99,
            ZaloSemanticActionActorKind.None,
            null,
            [],
            false,
            "asks_capability");

        var validation = ZaloSemanticActionPlanValidator.Validate(plan, incoming, snapshot, Settings());

        Assert.False(validation.Accepted);
        Assert.Equal("semantic_action_readonly_question", validation.Reason);
        Assert.Null(await new ZaloOpenSlotOfferStore(fixture.Db)
            .LoadPendingClaimAsync("conn-1", "g1", "user-khang"));
    }

    private static ZaloSemanticActionPlan MutationPlan(
        ZaloSemanticActionKind action,
        ZaloActionGroundingSnapshot snapshot,
        params ZaloSemanticActionTarget[] targets) =>
        new(
            ZaloSemanticActionRoute.MutationRequest,
            action,
            .99,
            ZaloSemanticActionActorKind.CurrentSender,
            snapshot.CurrentSender.MemberId,
            targets,
            false,
            "test");

    private static ZaloSemanticActionSettings Settings() => new(true, .85, 12, 20, 100);

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

    private sealed class ThrowingAiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new TaskCanceledException("timeout");
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

        public static async Task<Fixture> CreateAsync(bool addSecondSession = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-action",
                DisplayName = "Admin",
                Email = $"action-{Guid.NewGuid():n}@example.test",
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
                Id = "profile-huy",
                ZaloUserId = "user-huy",
                DisplayName = "Huy"
            };
            var first = BuildSession(
                "session-t7",
                "T7",
                DateTimeOffset.UtcNow.AddDays(1),
                admin,
                zalo,
                profile,
                "player-huy-t7");

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.Add(profile);
            db.MatchSessions.Add(first);
            if (addSecondSession)
            {
                db.MatchSessions.Add(BuildSession(
                    "session-cn",
                    "CN",
                    DateTimeOffset.UtcNow.AddDays(2),
                    admin,
                    zalo,
                    profile,
                    "player-huy-cn"));
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        private static MatchSession BuildSession(
            string id,
            string name,
            DateTimeOffset startTime,
            User admin,
            ZaloConnection zalo,
            PlayerProfile profile,
            string playerId)
        {
            var session = new MatchSession
            {
                Id = id,
                Name = name,
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                BotEnabled = true,
                Status = SessionStatus.Setup,
                StartTime = startTime,
                TeamCount = 3,
                TeamSize = 6
            };
            session.Players.Add(new SessionPlayer
            {
                Id = playerId,
                SessionId = session.Id,
                PlayerProfileId = profile.Id,
                PlayerProfile = profile,
                DisplayName = "Huy",
                IsPresent = true
            });
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
