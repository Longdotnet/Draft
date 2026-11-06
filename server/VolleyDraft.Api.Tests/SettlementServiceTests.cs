using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

public sealed class SettlementServiceTests
{
    [Fact]
    public void Settlement_deep_link_prefers_same_origin_api_host()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Public:BaseUrl"] = "https://api.example.test",
                ["AdminWeb:BaseUrl"] = "https://legacy-web.example.test",
                ["Cors:Origins:0"] = "https://another.example.test"
            })
            .Build();

        var link = new ZaloSettlementDeepLinkBuilder(configuration).Build("session 1");

        Assert.Equal(
            "https://api.example.test/app?focus=match-settlement&sessionId=session%201#match-settlement",
            link);
    }

    [Theory]
    [InlineData("@bot sổ tiền 60k/người", 60000)]
    [InlineData("@bot thu tiền 45.5k/người", 45500)]
    [InlineData("@bot thu tien 60000/nguoi", 60000)]
    public void Settlement_command_parser_reads_deterministic_amount(string content, decimal expected)
    {
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot",
            botId: "bot",
            groupId: "group",
            messageId: "m1",
            senderId: "admin",
            senderName: "Admin",
            content: content,
            mentions: [],
            mentionedBot: true,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var parsed = ZaloSettlementCommandParser.TryParse(incoming);

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.AmountPerFullSlot);
    }

    [Fact]
    public void Settlement_command_parser_ignores_unaddressed_ambient_payment_message()
    {
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot",
            botId: "bot",
            groupId: "group",
            messageId: "m1",
            senderId: "admin",
            senderName: "Admin",
            content: "thu tiền 60k/người",
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Null(ZaloSettlementCommandParser.TryParse(incoming));
    }

    [Fact]
    public void Settlement_access_verification_parser_requires_bot_mention()
    {
        var addressed = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "group",
            messageId: "verify-1",
            senderId: "leader",
            senderName: "Leader",
            content: "@bot xác minh AB23CD",
            mentions: [],
            mentionedBot: true,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var ambient = addressed with { MentionedBot = false };

        Assert.Equal("AB23CD", ZaloSettlementCommandParser.TryParseVerification(addressed));
        Assert.Null(ZaloSettlementCommandParser.TryParseVerification(ambient));
    }

    [Fact]
    public async Task Settlement_access_pairs_authorized_zalo_role_and_scopes_token_to_group()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m));
        var access = new SettlementAccessService(
            fixture.Db,
            (_, _, _) => Task.FromResult(ServiceResult<ZaloGroupRoleAuthorization>.Success(new(true, false))));

        var challenge = await access.CreateChallengeAsync("session");
        Assert.True(challenge.IsSuccess);

        var wrongGroup = await access.VerifyFromZaloAsync(
            "bot-account", "somewhere-else", "leader", "Leader", challenge.Value!.Code);
        Assert.False(wrongGroup.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, wrongGroup.StatusCode);

        var verified = await access.VerifyFromZaloAsync(
            "bot-account", "group", "leader", "Leader", challenge.Value.Code);
        Assert.True(verified.IsSuccess);
        Assert.True(verified.Value!.Approved);

        var replay = await access.VerifyFromZaloAsync(
            "bot-account", "group", "leader", "Leader", challenge.Value.Code);
        Assert.False(replay.IsSuccess);
        Assert.Equal(StatusCodes.Status409Conflict, replay.StatusCode);

        var wrongBrowser = await access.RedeemChallengeAsync(challenge.Value.ChallengeId, "not-the-browser-secret");
        Assert.False(wrongBrowser.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, wrongBrowser.StatusCode);

        var redeemed = await access.RedeemChallengeAsync(challenge.Value.ChallengeId, challenge.Value.RedemptionSecret);
        Assert.True(redeemed.IsSuccess);
        Assert.True(redeemed.Value!.Response.Authorized);
        Assert.False(string.IsNullOrWhiteSpace(redeemed.Value.AccessToken));

        var redeemedAgain = await access.RedeemChallengeAsync(challenge.Value.ChallengeId, challenge.Value.RedemptionSecret);
        Assert.True(redeemedAgain.IsSuccess);
        Assert.Equal(redeemed.Value.AccessToken, redeemedAgain.Value!.AccessToken);
        Assert.Equal(1, await fixture.Db.SettlementAccessSessions.CountAsync());

        var authorized = await access.AuthorizeAsync(redeemed.Value.AccessToken, "session");
        Assert.True(authorized.IsSuccess);
        Assert.Equal("leader", authorized.Value!.ZaloUserId);

        fixture.Db.MatchSessions.Add(new MatchSession
        {
            Id = "other-session",
            Name = "Other",
            AdminUserId = "admin",
            ZaloConnectionId = "connection",
            ZaloGroupId = "other-group"
        });
        await fixture.Db.SaveChangesAsync();

        var crossGroup = await access.AuthorizeAsync(redeemed.Value.AccessToken, "other-session");
        Assert.False(crossGroup.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, crossGroup.StatusCode);
    }

    [Fact]
    public async Task Settlement_access_rejects_expired_challenge()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m));
        var access = new SettlementAccessService(
            fixture.Db,
            (_, _, _) => Task.FromResult(ServiceResult<ZaloGroupRoleAuthorization>.Success(new(true, false))));
        var challenge = (await access.CreateChallengeAsync("session")).Value!;
        var stored = await fixture.Db.SettlementAccessChallenges.SingleAsync(item => item.Id == challenge.ChallengeId);
        stored.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await fixture.Db.SaveChangesAsync();

        var verified = await access.VerifyFromZaloAsync(
            "bot-account", "group", "leader", "Leader", challenge.Code);

        Assert.False(verified.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, verified.StatusCode);
        var redeemed = await access.RedeemChallengeAsync(challenge.ChallengeId, challenge.RedemptionSecret);
        Assert.True(redeemed.IsSuccess);
        Assert.False(redeemed.Value!.Response.Authorized);
        Assert.Null(redeemed.Value.AccessToken);
    }

    [Fact]
    public async Task Settlement_access_revokes_remembered_device_after_group_role_is_removed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m));
        var approving = new SettlementAccessService(
            fixture.Db,
            (_, _, _) => Task.FromResult(ServiceResult<ZaloGroupRoleAuthorization>.Success(new(true, false))));
        var challenge = (await approving.CreateChallengeAsync("session")).Value!;
        Assert.True((await approving.VerifyFromZaloAsync(
            "bot-account", "group", "leader", "Leader", challenge.Code)).Value!.Approved);
        var token = (await approving.RedeemChallengeAsync(challenge.ChallengeId, challenge.RedemptionSecret)).Value!.AccessToken!;
        var storedAccess = await fixture.Db.SettlementAccessSessions
            .SingleAsync(item => item.TokenHash == SettlementAccessService.Hash(token));
        storedAccess.LastRoleVerifiedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await fixture.Db.SaveChangesAsync();

        var afterDemotion = new SettlementAccessService(
            fixture.Db,
            (_, _, _) => Task.FromResult(ServiceResult<ZaloGroupRoleAuthorization>.Success(new(false, false))));
        var result = await afterDemotion.AuthorizeAsync(token, "session");

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.NotNull((await fixture.Db.SettlementAccessSessions.SingleAsync(item => item.Id == storedAccess.Id)).RevokedAt);
    }

    [Fact]
    public async Task Settlement_access_denies_ordinary_member_and_never_issues_access_token()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m));
        var access = new SettlementAccessService(
            fixture.Db,
            (_, _, _) => Task.FromResult(ServiceResult<ZaloGroupRoleAuthorization>.Success(new(false, false))));

        var challenge = (await access.CreateChallengeAsync("session")).Value!;
        var verified = await access.VerifyFromZaloAsync(
            "bot-account", "group", "member", "Member", challenge.Code);

        Assert.True(verified.IsSuccess);
        Assert.False(verified.Value!.Approved);
        var redeemed = await access.RedeemChallengeAsync(challenge.ChallengeId, challenge.RedemptionSecret);
        Assert.True(redeemed.IsSuccess);
        Assert.Equal(SettlementAccessChallengeStatus.Denied, redeemed.Value!.Response.Status);
        Assert.False(redeemed.Value.Response.Authorized);
        Assert.Null(redeemed.Value.AccessToken);
    }

    [Fact]
    public async Task Settlement_access_accepts_configured_operator_without_group_role_lookup()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m));
        var session = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == "session");
        session.BotOperatorZaloUserIdsJson = "[\"operator\"]";
        await fixture.Db.SaveChangesAsync();
        var roleLookupCount = 0;
        var access = new SettlementAccessService(
            fixture.Db,
            (_, _, _) =>
            {
                roleLookupCount += 1;
                return Task.FromResult(ServiceResult<ZaloGroupRoleAuthorization>.Success(new(false, false)));
            });

        var challenge = (await access.CreateChallengeAsync("session")).Value!;
        var verified = await access.VerifyFromZaloAsync(
            "bot-account", "group", "operator", "Operator", challenge.Code);

        Assert.True(verified.IsSuccess);
        Assert.True(verified.Value!.Approved);
        Assert.Equal(0, roleLookupCount);

        var token = (await access.RedeemChallengeAsync(challenge.ChallengeId, challenge.RedemptionSecret)).Value!.AccessToken!;
        var sameSession = await access.AuthorizeAsync(token, "session");
        Assert.True(sameSession.IsSuccess);
        Assert.Equal(0, roleLookupCount);

        fixture.Db.MatchSessions.Add(new MatchSession
        {
            Id = "same-group-other-session",
            Name = "Same group other",
            AdminUserId = "admin",
            ZaloConnectionId = "connection",
            ZaloGroupId = "group",
            BotOperatorZaloUserIdsJson = "[]"
        });
        await fixture.Db.SaveChangesAsync();

        var otherSession = await access.AuthorizeAsync(token, "same-group-other-session");
        Assert.False(otherSession.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, otherSession.StatusCode);
        Assert.Equal(1, roleLookupCount);

        // Denying the cross-session attempt must not destroy the operator's legitimate
        // access to the session where that UID is explicitly configured.
        var originalStillWorks = await access.AuthorizeAsync(token, "session");
        Assert.True(originalStillWorks.IsSuccess);
    }

    [Fact]
    public async Task Settlement_access_challenge_rate_limit_is_per_requester_not_session_wide()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m));
        var access = new SettlementAccessService(
            fixture.Db,
            (_, _, _) => Task.FromResult(ServiceResult<ZaloGroupRoleAuthorization>.Success(new(true, false))));

        for (var index = 0; index < 8; index += 1)
        {
            var created = await access.CreateChallengeAsync("session", "203.0.113.10");
            Assert.True(created.IsSuccess);
        }

        var blockedSameRequester = await access.CreateChallengeAsync("session", "203.0.113.10");
        Assert.False(blockedSameRequester.IsSuccess);
        Assert.Equal(StatusCodes.Status429TooManyRequests, blockedSameRequester.StatusCode);

        var differentRequester = await access.CreateChallengeAsync("session", "203.0.113.11");
        Assert.True(differentRequester.IsSuccess);
    }

    [Fact]
    public async Task Create_seeds_present_players_and_shared_slot_as_half()
    {
        await using var fixture = await Fixture.CreateAsync();

        var created = await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(60_000m));

        Assert.True(created.IsSuccess);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var settlement = Assert.IsType<SettlementResponse>(created.Value);
        Assert.Equal(2, settlement.TotalCount);
        Assert.Equal(90_000m, settlement.TotalDue);
        Assert.Equal(0m, settlement.TotalCollected);
        var full = Assert.Single(settlement.Entries, item => item.SessionPlayerId == "player-full");
        Assert.Equal(1m, full.SlotFactor);
        Assert.Equal(60_000m, full.AmountDue);
        Assert.Equal("uid-full", full.ZaloUserId);
        var half = Assert.Single(settlement.Entries, item => item.SessionPlayerId == "player-half");
        Assert.Equal(0.5m, half.SlotFactor);
        Assert.Equal(30_000m, half.AmountDue);
        Assert.DoesNotContain(settlement.Entries, item => item.SessionPlayerId == "player-absent");

        var duplicate = await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(60_000m));
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(StatusCodes.Status409Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Update_entry_accepts_only_full_or_half_and_recalculates_totals()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(60_000m))).Value!;
        var full = created.Entries.Single(item => item.SessionPlayerId == "player-full");

        var invalid = await fixture.Service.UpdateEntryAsync(
            "admin",
            "session",
            full.Id,
            new UpdateSettlementEntryRequest(0.75m, true));
        Assert.False(invalid.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);

        var updated = await fixture.Service.UpdateEntryAsync(
            "admin",
            "session",
            full.Id,
            new UpdateSettlementEntryRequest(0.5m, true));
        Assert.True(updated.IsSuccess);
        Assert.Equal(60_000m, updated.Value!.TotalDue);
        Assert.Equal(30_000m, updated.Value.TotalCollected);
        Assert.Equal(1, updated.Value.PaidCount);
        Assert.Equal(SettlementPaymentStatus.Paid,
            updated.Value.Entries.Single(item => item.Id == full.Id).PaymentStatus);
    }

    [Fact]
    public async Task Settlement_rejects_amounts_outside_database_precision_and_scale()
    {
        await using var fixture = await Fixture.CreateAsync();

        var excessiveScale = await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(60_000.001m));
        Assert.False(excessiveScale.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, excessiveScale.StatusCode);

        var excessivePrecision = await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(10_000_000_000_000_000m));
        Assert.False(excessivePrecision.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, excessivePrecision.StatusCode);
    }

    [Fact]
    public void Audience_message_targets_only_matching_entries_with_exact_mentions()
    {
        var entries = new List<SettlementEntry>
        {
            new() { Id = "paid", DisplayNameSnapshot = "B", ZaloUserIdSnapshot = "uid-b", PaymentStatus = SettlementPaymentStatus.Paid },
            new() { Id = "unpaid-a", DisplayNameSnapshot = "A", ZaloUserIdSnapshot = "uid-a", PaymentStatus = SettlementPaymentStatus.Unpaid },
            new() { Id = "unpaid-c", DisplayNameSnapshot = "C", ZaloUserIdSnapshot = "uid-c", PaymentStatus = SettlementPaymentStatus.Unpaid }
        };

        var result = SettlementService.BuildAudienceMessage(
            SettlementAudience.Unpaid,
            entries,
            [],
            "đóng tiền nhé");

        Assert.Equal("@A @C đóng tiền nhé", result.Message);
        Assert.Equal(["unpaid-a", "unpaid-c"], result.TargetEntryIds);
        Assert.Collection(result.Mentions,
            mention => Assert.Equal(new BridgeOutgoingMention("uid-a", 0, 2), mention),
            mention => Assert.Equal(new BridgeOutgoingMention("uid-c", 3, 2), mention));
    }

    [Fact]
    public async Task Delayed_unpaid_action_resolves_payment_status_at_send_time()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(60_000m))).Value!;
        var full = created.Entries.Single(item => item.SessionPlayerId == "player-full");

        var queued = await fixture.Service.QueueActionAsync(
            "admin",
            "session",
            new CreateSettlementActionRequest(
                SettlementActionKind.Reminder,
                SettlementAudience.Unpaid,
                null,
                "nhắc tiền",
                false,
                1));
        Assert.True(queued.IsSuccess);
        Assert.Equal(SettlementMessageStatus.Pending, queued.Value!.Status);

        await fixture.Service.UpdateEntryAsync(
            "admin",
            "session",
            full.Id,
            new UpdateSettlementEntryRequest(1m, true));
        var pending = await fixture.Db.SettlementMessages.SingleAsync(item => item.Id == queued.Value.Id);
        pending.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        var processed = await fixture.Service.ProcessDueAsync();

        Assert.Equal(1, processed);
        Assert.Single(fixture.Handler.RequestBodies);
        using var payload = JsonDocument.Parse(fixture.Handler.RequestBodies[0]);
        var mentions = payload.RootElement.GetProperty("mentions").EnumerateArray().ToList();
        var mention = Assert.Single(mentions);
        Assert.Equal("uid-half", mention.GetProperty("uid").GetString());
        Assert.DoesNotContain("uid-full", fixture.Handler.RequestBodies[0], StringComparison.Ordinal);
        var sent = await fixture.Db.SettlementMessages.AsNoTracking().SingleAsync(item => item.Id == queued.Value.Id);
        Assert.Equal(SettlementMessageStatus.Sent, sent.Status);
    }

    [Fact]
    public async Task Due_action_is_not_starved_by_more_than_one_hundred_future_messages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(60_000m))).Value!;
        var now = DateTimeOffset.UtcNow;

        for (var index = 0; index < 101; index++)
        {
            fixture.Db.SettlementMessages.Add(new SettlementMessage
            {
                SettlementId = created.Id,
                Kind = SettlementActionKind.Reminder,
                Audience = SettlementAudience.Unpaid,
                SelectedEntryIdsJson = "[]",
                Message = "future",
                DueAt = now.AddHours(1).AddMinutes(index),
                Status = SettlementMessageStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var due = new SettlementMessage
        {
            SettlementId = created.Id,
            Kind = SettlementActionKind.Reminder,
            Audience = SettlementAudience.Unpaid,
            SelectedEntryIdsJson = "[]",
            Message = "due now",
            DueAt = now.AddMinutes(-1),
            Status = SettlementMessageStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        fixture.Db.SettlementMessages.Add(due);
        await fixture.Db.SaveChangesAsync();

        var processed = await fixture.Service.ProcessDueAsync();

        Assert.Equal(1, processed);
        Assert.Single(fixture.Handler.RequestBodies);
        var sent = await fixture.Db.SettlementMessages.AsNoTracking().SingleAsync(item => item.Id == due.Id);
        Assert.Equal(SettlementMessageStatus.Sent, sent.Status);
    }

    [Fact]
    public async Task Action_rejects_undefined_enums_instead_of_falling_back_to_all()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m));

        var invalidAudience = await fixture.Service.QueueActionAsync(
            "admin",
            "session",
            new CreateSettlementActionRequest(
                SettlementActionKind.Reminder,
                (SettlementAudience)99,
                null,
                "không được broadcast",
                false,
                0));

        Assert.False(invalidAudience.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, invalidAudience.StatusCode);
        Assert.Empty(fixture.Handler.RequestBodies);
    }

    [Fact]
    public async Task Client_request_id_makes_action_post_idempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m));
        var request = new CreateSettlementActionRequest(
            SettlementActionKind.Reminder,
            SettlementAudience.Unpaid,
            null,
            "nhắc một lần",
            false,
            0,
            "mobile-request-1");

        var first = await fixture.Service.QueueActionAsync("admin", "session", request);
        var second = await fixture.Service.QueueActionAsync("admin", "session", request);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
        Assert.Single(fixture.Handler.RequestBodies);
        Assert.Equal(1, await fixture.Db.SettlementMessages.CountAsync());
    }

    [Fact]
    public async Task Delayed_action_keeps_original_zalo_group_when_session_is_relinked()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m));
        var queued = await fixture.Service.QueueActionAsync(
            "admin",
            "session",
            new CreateSettlementActionRequest(
                SettlementActionKind.Reminder,
                SettlementAudience.Unpaid,
                null,
                "nhắc đúng nhóm cũ",
                false,
                1,
                "relink-safe"));
        Assert.True(queued.IsSuccess);

        var session = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == "session");
        session.ZaloGroupId = "group-new";
        var pending = await fixture.Db.SettlementMessages.SingleAsync(item => item.Id == queued.Value!.Id);
        pending.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(1, await fixture.Service.ProcessDueAsync());
        var body = Assert.Single(fixture.Handler.RequestBodies);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("group", payload.RootElement.GetProperty("groupId").GetString());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(
            SqliteConnection connection,
            VolleyDraftDbContext db,
            CaptureHandler handler,
            SettlementService service)
        {
            this.connection = connection;
            Db = db;
            Handler = handler;
            Service = service;
        }

        public VolleyDraftDbContext Db { get; }
        public CaptureHandler Handler { get; }
        public SettlementService Service { get; }

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
                Id = "admin",
                DisplayName = "Admin",
                Email = $"settlement-{Guid.NewGuid():n}@test.local",
                PasswordHash = "hash"
            };
            var connectionModel = new ZaloConnection
            {
                Id = "connection",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Bot",
                EncryptedCredentials = "test",
                Status = ZaloConnectionStatus.Connected
            };
            var fullProfile = new PlayerProfile
            {
                Id = "profile-full",
                ZaloUserId = "uid-full",
                DisplayName = "Full"
            };
            var halfProfile = new PlayerProfile
            {
                Id = "profile-half",
                ZaloUserId = "uid-half",
                DisplayName = "Half"
            };
            var absentProfile = new PlayerProfile
            {
                Id = "profile-absent",
                ZaloUserId = "uid-absent",
                DisplayName = "Absent"
            };
            var session = new MatchSession
            {
                Id = "session",
                Name = "T6",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = connectionModel.Id,
                ZaloConnection = connectionModel,
                ZaloGroupId = "group",
                PaymentQrImageUrl = "https://example.test/qr.png"
            };
            session.Players.Add(new SessionPlayer
            {
                Id = "player-full",
                SessionId = session.Id,
                DisplayName = "Full",
                PlayerProfileId = fullProfile.Id,
                PlayerProfile = fullProfile,
                IsPresent = true,
                IsInsideSharedSlot = false
            });
            session.Players.Add(new SessionPlayer
            {
                Id = "player-half",
                SessionId = session.Id,
                DisplayName = "Half",
                PlayerProfileId = halfProfile.Id,
                PlayerProfile = halfProfile,
                IsPresent = true,
                IsInsideSharedSlot = true
            });
            session.Players.Add(new SessionPlayer
            {
                Id = "player-absent",
                SessionId = session.Id,
                DisplayName = "Absent",
                PlayerProfileId = absentProfile.Id,
                PlayerProfile = absentProfile,
                IsPresent = false
            });
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();

            var handler = new CaptureHandler();
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://bridge.test/") };
            var bridge = new ZaloBridgeClient(httpClient);
            var service = new SettlementService(db, bridge, NullLogger<SettlementService>.Instance);
            return new Fixture(connection, db, handler, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    public sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"sent\":true,\"mock\":true,\"messageId\":null}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
