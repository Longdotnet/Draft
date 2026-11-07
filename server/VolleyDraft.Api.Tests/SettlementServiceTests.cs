using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class SettlementServiceTests
{
    [Fact]
    public async Task Settlement_schema_patch_is_idempotent_with_actual_paid_and_group_fund_tables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await DatabaseSchemaPatch.EnsureLatestAsync(db);
        await DatabaseSchemaPatch.EnsureLatestAsync(db);

        await using var column = connection.CreateCommand();
        column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SettlementEntries') WHERE name = 'ActualPaidAmount'";
        Assert.Equal(1L, Convert.ToInt64(await column.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

        await using var preparedColumn = connection.CreateCommand();
        preparedColumn.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SettlementMessages') WHERE name = 'PreparedImageData'";
        Assert.Equal(1L, Convert.ToInt64(await preparedColumn.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

        await using var qrSnapshotColumn = connection.CreateCommand();
        qrSnapshotColumn.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SettlementMessages') WHERE name = 'PaymentQrImageUrlSnapshot'";
        Assert.Equal(1L, Convert.ToInt64(await qrSnapshotColumn.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

        await using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'GroupFundTransactions'";
        Assert.Equal(1L, Convert.ToInt64(await table.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

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
    public void Settlement_command_parser_reads_explicit_date_and_time_without_confusing_year_for_amount()
    {
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot",
            botId: "bot",
            groupId: "group",
            messageId: "m-date",
            senderId: "admin",
            senderName: "Admin",
            content: "@bot thu tiền 22k/người ngày 6/11/2026 20h30",
            mentions: [],
            mentionedBot: true,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var parsed = ZaloSettlementCommandParser.TryParse(incoming);

        Assert.NotNull(parsed);
        Assert.Equal(22_000m, parsed!.AmountPerFullSlot);
        Assert.Equal(new ZaloSettlementDateReference(6, 11, 2026), parsed.DateReference);
        Assert.Equal(new TimeOnly(20, 30), parsed.LocalTime);
    }

    [Fact]
    public void Settlement_command_parser_keeps_yearless_date_yearless_and_supports_yesterday()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.FromHours(7));
        var dated = new ZaloIncomingMessageEvent(
            "bot", "bot", "group", "m-date", "admin", "Admin",
            "@bot thu tiền 22000/người ngày 6/11", [], true, now.ToUnixTimeMilliseconds());
        var yesterday = dated with { Content = "@bot thu tiền 22k/người hôm qua" };

        var datedParsed = ZaloSettlementCommandParser.TryParse(dated, now);
        var yesterdayParsed = ZaloSettlementCommandParser.TryParse(yesterday, now);

        Assert.Equal(new ZaloSettlementDateReference(6, 11, null), datedParsed!.DateReference);
        Assert.Equal(new ZaloSettlementDateReference(10, 11, 2026), yesterdayParsed!.DateReference);
    }

    [Fact]
    public void Settlement_command_parser_never_treats_date_year_as_payment_amount_and_flags_invalid_date()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.FromHours(7));
        var dateOnly = new ZaloIncomingMessageEvent(
            "bot", "bot", "group", "m-date-only", "admin", "Admin",
            "@bot thu tiền ngày 6-11-2026", [], true, now.ToUnixTimeMilliseconds());
        var invalidDate = dateOnly with { Content = "@bot thu tiền 22k/người ngày 31/02/2026" };

        Assert.Null(ZaloSettlementCommandParser.TryParse(dateOnly, now));
        var parsedInvalid = ZaloSettlementCommandParser.TryParse(invalidDate, now);
        Assert.NotNull(parsedInvalid);
        Assert.NotNull(parsedInvalid!.SelectorError);
    }

    [Fact]
    public void Settlement_session_selector_uses_explicit_date_and_requires_time_when_that_day_is_ambiguous()
    {
        var now = new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime();
        var candidates = new List<MatchSession>
        {
            new() { Id = "past", Name = "T6 cũ", StartTime = new DateTimeOffset(2026, 11, 6, 18, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime() },
            new() { Id = "target-18", Name = "T6 18h", StartTime = new DateTimeOffset(2026, 11, 6, 18, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime() },
            new() { Id = "target-2030", Name = "T6 20h30", StartTime = new DateTimeOffset(2026, 11, 6, 20, 30, 0, TimeSpan.FromHours(7)).ToUniversalTime() },
            new() { Id = "future", Name = "T7", StartTime = new DateTimeOffset(2026, 11, 12, 19, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime() }
        };
        // Keep one 06/11 candidate from a different year to prove a yearless reference is
        // deliberately ambiguous across matching group history instead of silently guessing.
        candidates[0].StartTime = new DateTimeOffset(2025, 11, 6, 18, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime();

        var ambiguous = ZaloSettlementSessionSelector.Select(
            candidates,
            new ZaloSettlementOpenCommand(22_000m, new ZaloSettlementDateReference(6, 11, 2026)),
            now);
        Assert.False(ambiguous.IsSuccess);
        Assert.Contains("có 2 trận", ambiguous.Error);

        var selected = ZaloSettlementSessionSelector.Select(
            candidates,
            new ZaloSettlementOpenCommand(22_000m, new ZaloSettlementDateReference(6, 11, 2026), new TimeOnly(20, 30)),
            now);
        Assert.True(selected.IsSuccess);
        Assert.Equal("target-2030", selected.Session!.Id);

        var wrongClock = ZaloSettlementSessionSelector.Select(
            candidates,
            new ZaloSettlementOpenCommand(22_000m, new ZaloSettlementDateReference(6, 11, 2026), new TimeOnly(20, 0)),
            now);
        Assert.False(wrongClock.IsSuccess);
        Assert.Contains("Không có trận lúc 20:00", wrongClock.Error);
    }

    [Fact]
    public void Settlement_session_selector_without_date_prefers_latest_started_match_over_future_match()
    {
        var now = new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime();
        var candidates = new List<MatchSession>
        {
            new() { Id = "yesterday", Name = "Hôm qua", StartTime = new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime() },
            new() { Id = "tomorrow", Name = "Ngày mai", StartTime = new DateTimeOffset(2026, 11, 12, 19, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime() }
        };

        var selected = ZaloSettlementSessionSelector.Select(candidates, new ZaloSettlementOpenCommand(22_000m), now);

        Assert.True(selected.IsSuccess);
        Assert.Equal("yesterday", selected.Session!.Id);
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

    [Theory]
    [InlineData("@bot ai đóng tiền rồi?", "PaymentRoster")]
    [InlineData("@bot danh sách đóng tiền", "PaymentRoster")]
    [InlineData("@bot ai chưa đóng tiền", "PaymentRoster")]
    [InlineData("@bot tổng kết tiền", "FinancialSummary")]
    [InlineData("@bot báo cáo sổ tiền", "FinancialSummary")]
    public void Settlement_view_parser_reads_customer_friendly_commands(
        string content,
        string expectedKind)
    {
        var incoming = new ZaloIncomingMessageEvent(
            "bot-account", "bot-account", "group", "view-1", "member", "Member",
            content, [], true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var parsed = ZaloSettlementCommandParser.TryParseView(incoming);

        Assert.NotNull(parsed);
        Assert.Equal(expectedKind, parsed!.Kind.ToString());
    }

    [Fact]
    public void Settlement_view_parser_supports_date_time_and_requires_bot_mention()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.FromHours(7));
        var addressed = new ZaloIncomingMessageEvent(
            "bot-account", "bot-account", "group", "view-date", "member", "Member",
            "@bot danh sách đóng tiền ngày 6/11/2026 20h30", [], true, now.ToUnixTimeMilliseconds());
        var ambient = addressed with { MentionedBot = false };

        var parsed = ZaloSettlementCommandParser.TryParseView(addressed, now);

        Assert.NotNull(parsed);
        Assert.Equal(new ZaloSettlementDateReference(6, 11, 2026), parsed!.DateReference);
        Assert.Equal(new TimeOnly(20, 30), parsed.LocalTime);
        Assert.Null(ZaloSettlementCommandParser.TryParseView(ambient, now));
    }

    [Fact]
    public void Settlement_view_parser_matches_the_customer_command_from_production()
    {
        var now = new DateTimeOffset(2026, 11, 7, 0, 24, 0, TimeSpan.FromHours(7));
        var incoming = new ZaloIncomingMessageEvent(
            "bot-account", "bot-account", "group", "view-production", "member", "Member",
            "@Bott danh sách đóng tiền ngày 5/11", [], true, now.ToUnixTimeMilliseconds());

        var parsed = ZaloSettlementCommandParser.TryParseView(incoming, now);

        Assert.NotNull(parsed);
        Assert.Equal(ZaloSettlementViewKind.PaymentRoster, parsed!.Kind);
        Assert.Equal(new ZaloSettlementDateReference(5, 11, null), parsed.DateReference);
        Assert.Null(parsed.LocalTime);
        Assert.Null(parsed.SelectorError);
    }

    [Fact]
    public void Settlement_view_selector_with_time_only_uses_latest_started_exact_clock_match()
    {
        var now = new DateTimeOffset(2026, 11, 11, 22, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime();
        var candidates = new List<MatchSession>
        {
            new() { Id = "old-2030", Name = "Cũ 20h30", StartTime = new DateTimeOffset(2026, 11, 6, 20, 30, 0, TimeSpan.FromHours(7)).ToUniversalTime() },
            new() { Id = "latest-2030", Name = "Mới 20h30", StartTime = new DateTimeOffset(2026, 11, 10, 20, 30, 0, TimeSpan.FromHours(7)).ToUniversalTime() },
            new() { Id = "latest-other", Name = "Mới 19h", StartTime = new DateTimeOffset(2026, 11, 11, 19, 0, 0, TimeSpan.FromHours(7)).ToUniversalTime() }
        };
        var command = new ZaloSettlementViewCommand(
            ZaloSettlementViewKind.PaymentRoster,
            LocalTime: new TimeOnly(20, 30));

        var selected = ZaloSettlementSessionSelector.Select(candidates, command, now);

        Assert.True(selected.IsSuccess);
        Assert.Equal("latest-2030", selected.Session!.Id);
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
    public async Task Actual_paid_amount_tracks_surplus_and_group_fund_across_matches_without_double_counting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(60_000m))).Value!;
        var full = created.Entries.Single(item => item.SessionPlayerId == "player-full");
        var half = created.Entries.Single(item => item.SessionPlayerId == "player-half");

        var first = await fixture.Service.UpdateEntryAsync(
            "admin", "session", full.Id, new UpdateSettlementEntryRequest(1m, true, 65_000m));
        Assert.True(first.IsSuccess);
        Assert.Equal(60_000m, first.Value!.TotalCollected);
        Assert.Equal(65_000m, first.Value.TotalReceived);
        Assert.Equal(5_000m, first.Value.SettlementSurplus);
        Assert.Equal(5_000m, first.Value.GroupFundBalance);

        var second = await fixture.Service.UpdateEntryAsync(
            "admin", "session", half.Id, new UpdateSettlementEntryRequest(0.5m, true, 35_000m));
        Assert.Equal(10_000m, second.Value!.SettlementSurplus);
        Assert.Equal(10_000m, second.Value.GroupFundBalance);

        var corrected = await fixture.Service.UpdateEntryAsync(
            "admin", "session", full.Id, new UpdateSettlementEntryRequest(1m, true, 62_000m));
        Assert.Equal(7_000m, corrected.Value!.SettlementSurplus);
        Assert.Equal(7_000m, corrected.Value.GroupFundBalance);

        var profile = await fixture.Db.PlayerProfiles.SingleAsync(item => item.Id == "profile-full");
        var nextSession = new MatchSession
        {
            Id = "session-2",
            Name = "T7",
            AdminUserId = "admin",
            ZaloConnectionId = "connection",
            ZaloGroupId = "group"
        };
        nextSession.Players.Add(new SessionPlayer
        {
            Id = "player-next",
            SessionId = nextSession.Id,
            DisplayName = "Full",
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            IsPresent = true
        });
        fixture.Db.MatchSessions.Add(nextSession);
        await fixture.Db.SaveChangesAsync();

        var next = (await fixture.Service.CreateAsync("admin", "session-2", new CreateSettlementRequest(50_000m))).Value!;
        var nextEntry = Assert.Single(next.Entries);
        var nextPaid = await fixture.Service.UpdateEntryAsync(
            "admin", "session-2", nextEntry.Id, new UpdateSettlementEntryRequest(1m, true, 55_000m));

        Assert.Equal(5_000m, nextPaid.Value!.SettlementSurplus);
        Assert.Equal(12_000m, nextPaid.Value.GroupFundBalance);
        var deltas = await fixture.Db.GroupFundTransactions.AsNoTracking()
            .OrderBy(item => item.CreatedAt)
            .Select(item => item.Amount)
            .ToListAsync();
        Assert.Equal([5_000m, 5_000m, -3_000m, 5_000m], deltas);
    }

    [Fact]
    public async Task Settlement_excel_export_is_office_friendly_and_keeps_fund_provenance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == "session");
        session.StartTime = new DateTimeOffset(2026, 11, 6, 20, 30, 0, TimeSpan.FromHours(7)).ToUniversalTime();
        session.Location = "Sân ZYRO";
        session.ZaloGroupName = "ZYRO Volleyball";
        await fixture.Db.SaveChangesAsync();

        var created = (await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(60_000m))).Value!;
        var full = created.Entries.Single(item => item.SessionPlayerId == "player-full");
        var half = created.Entries.Single(item => item.SessionPlayerId == "player-half");
        await fixture.Service.UpdateEntryAsync(
            "admin", "session", full.Id, new UpdateSettlementEntryRequest(1m, true, 65_000m));
        await fixture.Service.UpdateEntryAsync(
            "admin", "session", half.Id, new UpdateSettlementEntryRequest(.5m, true, 30_000m));

        var exporter = new SettlementExcelExporter(fixture.Db);
        var result = await exporter.ExportAsync("admin", "session");

        Assert.True(result.IsSuccess);
        Assert.EndsWith(".xlsx", result.Value!.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Value.Content.Length > 5_000);

        using var workbook = new XLWorkbook(new MemoryStream(result.Value.Content));
        Assert.NotNull(workbook.Worksheet("01_TONG_QUAN"));
        Assert.NotNull(workbook.Worksheet("02_THU_TIEN"));
        Assert.NotNull(workbook.Worksheet("03_SO_QUY"));
        Assert.NotNull(workbook.Worksheet("04_NHAT_KY"));
        Assert.Equal(XLWorksheetVisibility.Hidden, workbook.Worksheet("99_NGUON_DU_LIEU").Visibility);

        var payment = workbook.Worksheet("02_THU_TIEN");
        var fullRow = payment.Column(2).CellsUsed().Single(cell => cell.GetString() == "Full").Address.RowNumber;
        Assert.Equal("Đã đóng", payment.Cell(fullRow, 8).GetString());
        Assert.Contains("MAX(0", payment.Cell(fullRow, 7).FormulaA1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vào quỹ", payment.Cell(fullRow, 9).GetString(), StringComparison.OrdinalIgnoreCase);

        var fund = workbook.Worksheet("03_SO_QUY");
        Assert.Contains(fund.Column(3).CellsUsed(), cell => cell.GetString() == "Full");
        Assert.Contains(fund.Column(7).CellsUsed(), cell => cell.GetString().Contains("quỹ tăng", StringComparison.OrdinalIgnoreCase));
        var fullFundRow = fund.Column(3).CellsUsed().Single(cell => cell.GetString() == "Full").Address.RowNumber;
        Assert.Contains("HYPERLINK", fund.Cell(fullFundRow, 9).FormulaA1, StringComparison.OrdinalIgnoreCase);

        var overview = workbook.Worksheet("01_TONG_QUAN");
        Assert.Contains("ZYRO", overview.Cell("B2").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Quỹ này được hình thành", overview.Cell("E11").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Paid_entry_rejects_actual_amount_below_due()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(22_000m))).Value!;
        var full = created.Entries.Single(item => item.SessionPlayerId == "player-full");

        var result = await fixture.Service.UpdateEntryAsync(
            "admin", "session", full.Id, new UpdateSettlementEntryRequest(1m, true, 20_000m));

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Empty(await fixture.Db.GroupFundTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Changing_fee_preserves_received_cash_and_adjusts_surplus_instead_of_rewriting_payment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(22_000m))).Value!;
        var full = created.Entries.Single(item => item.SessionPlayerId == "player-full");
        await fixture.Service.UpdateEntryAsync(
            "admin", "session", full.Id, new UpdateSettlementEntryRequest(1m, true, 25_000m));

        var lowered = await fixture.Service.UpdateAsync("admin", "session", new UpdateSettlementRequest(20_000m));

        Assert.True(lowered.IsSuccess);
        var loweredEntry = lowered.Value!.Entries.Single(item => item.Id == full.Id);
        Assert.Equal(25_000m, loweredEntry.ActualPaidAmount);
        Assert.Equal(5_000m, loweredEntry.SurplusAmount);
        Assert.Equal(5_000m, lowered.Value.GroupFundBalance);

        var raisedTooFar = await fixture.Service.UpdateAsync("admin", "session", new UpdateSettlementRequest(30_000m));
        Assert.False(raisedTooFar.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, raisedTooFar.StatusCode);

        var unchanged = await fixture.Service.GetAsync("admin", "session");
        Assert.Equal(20_000m, unchanged.Value!.AmountPerFullSlot);
        Assert.Equal(25_000m, unchanged.Value.Entries.Single(item => item.Id == full.Id).ActualPaidAmount);
        Assert.Equal(5_000m, unchanged.Value.GroupFundBalance);
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
    public async Task Summary_action_never_tags_all_and_attaches_zyro_png()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(22_000m))).Value!;
        var full = created.Entries.Single(item => item.SessionPlayerId == "player-full");
        await fixture.Service.UpdateEntryAsync(
            "admin",
            "session",
            full.Id,
            new UpdateSettlementEntryRequest(1m, true, 25_000m));

        var result = await fixture.Service.QueueActionAsync(
            "admin",
            "session",
            new CreateSettlementActionRequest(
                SettlementActionKind.Summary,
                SettlementAudience.All,
                [full.Id],
                null,
                true,
                0,
                "summary-no-all"));

        Assert.True(result.IsSuccess);
        Assert.Equal(SettlementMessageStatus.Sent, result.Value!.Status);
        Assert.False(result.Value.IncludePaymentQr);

        var body = Assert.Single(fixture.Handler.RequestBodies);
        using var payload = JsonDocument.Parse(body);
        var root = payload.RootElement;
        var message = root.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.DoesNotContain("@all", message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tổng kết tiền sân", message!, StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("mentions").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("imageUrl").ValueKind);
        Assert.Equal("image/png", root.GetProperty("imageContentType").GetString());
        Assert.EndsWith(".png", root.GetProperty("imageFileName").GetString(), StringComparison.OrdinalIgnoreCase);

        var base64 = root.GetProperty("imageBase64").GetString();
        Assert.False(string.IsNullOrWhiteSpace(base64));
        var bytes = Convert.FromBase64String(base64!);
        Assert.True(bytes.Length > 20_000);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(1080, bitmap!.Width);
        Assert.InRange(bitmap.Height, 1220, 2100);
    }

    [Fact]
    public async Task Legacy_pending_summary_ignores_stale_qr_flag_at_send_time()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(22_000m))).Value!;
        var session = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == "session");
        session.PaymentQrImageUrl = null;
        var legacy = new SettlementMessage
        {
            SettlementId = created.Id,
            ZaloAccountIdSnapshot = "bot-account",
            ZaloGroupIdSnapshot = "group",
            Kind = SettlementActionKind.Summary,
            Audience = SettlementAudience.All,
            SelectedEntryIdsJson = "[]",
            IncludePaymentQr = true,
            DueAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status = SettlementMessageStatus.Pending
        };
        fixture.Db.SettlementMessages.Add(legacy);
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.Service.ProcessActionAsync(legacy.Id));

        var body = Assert.Single(fixture.Handler.RequestBodies);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("imageUrl").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("imageBase64").GetString()));
        Assert.Empty(payload.RootElement.GetProperty("mentions").EnumerateArray());
    }

    [Fact]
    public async Task Payment_qr_action_broadcasts_without_mentions_and_reuses_snapshotted_qr_on_retry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(22_000m));
        var session = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == "session");
        session.PaymentInstructions = "Nội dung CK: ZYRO + tên Zalo";
        session.PaymentQrImageUrl = "https://example.test/qr-original.png";
        await fixture.Db.SaveChangesAsync();

        var queued = await fixture.Service.QueueActionAsync(
            "admin",
            "session",
            new CreateSettlementActionRequest(
                SettlementActionKind.PaymentQr,
                SettlementAudience.All,
                null,
                null,
                false,
                1,
                "qr-retry-stable"));
        Assert.True(queued.IsSuccess);
        Assert.True(queued.Value!.IncludePaymentQr);

        var action = await fixture.Db.SettlementMessages.SingleAsync(item => item.Id == queued.Value.Id);
        Assert.Equal("https://example.test/qr-original.png", action.PaymentQrImageUrlSnapshot);
        action.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        fixture.Handler.FailuresRemaining = 1;
        await fixture.Db.SaveChangesAsync();

        Assert.False(await fixture.Service.ProcessActionAsync(action.Id));
        var firstBody = Assert.Single(fixture.Handler.RequestBodies);
        using (var firstPayload = JsonDocument.Parse(firstBody))
        {
            var root = firstPayload.RootElement;
            Assert.Equal("https://example.test/qr-original.png", root.GetProperty("imageUrl").GetString());
            Assert.Empty(root.GetProperty("mentions").EnumerateArray());
            Assert.DoesNotContain("@all", root.GetProperty("message").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Nội dung CK", root.GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
        }

        session = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == "session");
        session.PaymentQrImageUrl = "https://example.test/qr-new.png";
        session.Name = "Tên trận đã đổi";
        session.PaymentInstructions = "Hướng dẫn đã đổi sau lần gửi lỗi";
        action = await fixture.Db.SettlementMessages.SingleAsync(item => item.Id == action.Id);
        action.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.Service.ProcessActionAsync(action.Id));
        Assert.Equal(2, fixture.Handler.RequestBodies.Count);
        Assert.Equal(fixture.Handler.RequestBodies[0], fixture.Handler.RequestBodies[1]);
    }

    [Fact]
    public async Task Payment_qr_action_requires_qr_and_update_is_scoped_to_owned_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("admin", "session", new CreateSettlementRequest(22_000m));
        var session = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == "session");
        session.PaymentQrImageUrl = null;
        await fixture.Db.SaveChangesAsync();

        var missingQr = await fixture.Service.QueueActionAsync(
            "admin",
            "session",
            new CreateSettlementActionRequest(
                SettlementActionKind.PaymentQr,
                SettlementAudience.All,
                null,
                null,
                true,
                0));
        Assert.False(missingQr.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, missingQr.StatusCode);

        var forbiddenOwner = await fixture.Service.UpdatePaymentQrAsync(
            "different-admin",
            "session",
            "https://example.test/forbidden.png");
        Assert.False(forbiddenOwner.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, forbiddenOwner.StatusCode);

        var updated = await fixture.Service.UpdatePaymentQrAsync(
            "admin",
            "session",
            "https://example.test/uploaded.png");
        Assert.True(updated.IsSuccess);
        Assert.Equal("https://example.test/uploaded.png", updated.Value!.PaymentQrImageUrl);
    }

    [Fact]
    public async Task Settlement_qr_upload_reuses_bot_image_validation_limits()
    {
        await using var fixture = await Fixture.CreateAsync();
        var imageService = new ZaloBotImageService(fixture.Db);

        var invalidType = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "qr.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };
        var invalid = await imageService.UploadAsync("admin", invalidType, "https://example.test");
        Assert.False(invalid.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);

        const int oversizedLength = 10 * 1024 * 1024 + 1;
        var oversized = new FormFile(Stream.Null, 0, oversizedLength, "file", "qr.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
        var tooLarge = await imageService.UploadAsync("admin", oversized, "https://example.test");
        Assert.False(tooLarge.IsSuccess);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, tooLarge.StatusCode);
    }

    [Fact]
    public async Task Delayed_summary_renders_latest_payment_and_fund_state_at_send_time()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(22_000m))).Value!;
        var full = created.Entries.Single(item => item.SessionPlayerId == "player-full");

        var queued = await fixture.Service.QueueActionAsync(
            "admin",
            "session",
            new CreateSettlementActionRequest(
                SettlementActionKind.Summary,
                SettlementAudience.All,
                null,
                null,
                false,
                1,
                "summary-latest-state"));
        Assert.True(queued.IsSuccess);
        Assert.Empty(fixture.Handler.RequestBodies);

        await fixture.Service.UpdateEntryAsync(
            "admin",
            "session",
            full.Id,
            new UpdateSettlementEntryRequest(1m, true, 25_000m));
        var relinkedSession = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == "session");
        relinkedSession.ZaloGroupId = "group-new";
        var pending = await fixture.Db.SettlementMessages.SingleAsync(item => item.Id == queued.Value!.Id);
        pending.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(1, await fixture.Service.ProcessDueAsync());
        var sentBody = Assert.Single(fixture.Handler.RequestBodies);
        using var payload = JsonDocument.Parse(sentBody);
        Assert.Equal("group", payload.RootElement.GetProperty("groupId").GetString());
        var actualBytes = Convert.FromBase64String(payload.RootElement.GetProperty("imageBase64").GetString()!);

        var latest = await fixture.Db.MatchSettlements.AsNoTracking()
            .Include(item => item.Session)
            .Include(item => item.Entries)
            .SingleAsync(item => item.Id == created.Id);
        var expectedBytes = new SettlementSummaryCardRenderer().Render(latest.Session, latest, 3_000m);

        Assert.Equal(expectedBytes, actualBytes);
        Assert.Empty(payload.RootElement.GetProperty("mentions").EnumerateArray());
    }

    [Fact]
    public async Task Summary_retry_reuses_prepared_image_when_money_changes_after_ambiguous_failure()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = (await fixture.Service.CreateAsync(
            "admin",
            "session",
            new CreateSettlementRequest(22_000m))).Value!;
        var full = created.Entries.Single(item => item.SessionPlayerId == "player-full");
        var queued = await fixture.Service.QueueActionAsync(
            "admin",
            "session",
            new CreateSettlementActionRequest(
                SettlementActionKind.Summary,
                SettlementAudience.All,
                null,
                null,
                false,
                1,
                "summary-retry-stable"));
        Assert.True(queued.IsSuccess);

        var action = await fixture.Db.SettlementMessages.SingleAsync(item => item.Id == queued.Value!.Id);
        action.Message = null; // emulate a Summary queued by the previous release
        action.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();
        fixture.Handler.FailuresRemaining = 1;

        Assert.False(await fixture.Service.ProcessActionAsync(action.Id));
        var preparedAfterFailure = await fixture.Db.SettlementMessages.AsNoTracking()
            .SingleAsync(item => item.Id == action.Id);
        Assert.NotNull(preparedAfterFailure.PreparedImageData);

        await fixture.Service.UpdateEntryAsync(
            "admin",
            "session",
            full.Id,
            new UpdateSettlementEntryRequest(1m, true, 25_000m));
        var renamedSession = await fixture.Db.MatchSessions.SingleAsync(item => item.Id == "session");
        renamedSession.Name = "Tên trận đã đổi sau lần gửi lỗi";
        action = await fixture.Db.SettlementMessages.SingleAsync(item => item.Id == action.Id);
        action.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.Service.ProcessActionAsync(action.Id));
        Assert.Equal(2, fixture.Handler.RequestBodies.Count);
        Assert.Equal(fixture.Handler.RequestBodies[0], fixture.Handler.RequestBodies[1]);
        var sent = await fixture.Db.SettlementMessages.AsNoTracking().SingleAsync(item => item.Id == action.Id);
        Assert.Equal(SettlementMessageStatus.Sent, sent.Status);
        Assert.Null(sent.PreparedImageData);
    }

    [Fact]
    public void Summary_renderer_scales_height_for_large_rosters()
    {
        var session = new MatchSession
        {
            Id = "large-session",
            Name = "ZYRO tối thứ 6",
            StartTime = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.FromHours(7))
        };
        var settlement = new MatchSettlement
        {
            Session = session,
            SessionId = session.Id,
            AmountPerFullSlot = 22_000m,
            Entries = Enumerable.Range(1, 500)
                .Select(index => new SettlementEntry
                {
                    Id = $"entry-{index}",
                    DisplayNameSnapshot = $"Thành viên {index:00}",
                    SlotFactor = index % 7 == 0 ? .5m : 1m,
                    PaymentStatus = SettlementPaymentStatus.Paid,
                    ActualPaidAmount = index % 5 == 0 ? 25_000m : 22_000m
                })
                .ToList()
        };

        var bytes = new SettlementSummaryCardRenderer().Render(session, settlement, 75_000m);

        Assert.True(bytes.Length < 10 * 1024 * 1024);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(1080, bitmap!.Width);
        Assert.InRange(bitmap.Height, 4_201, 8_000);
    }

    [Fact]
    public void Payment_roster_header_uses_authoritative_start_time_instead_of_legacy_name_time()
    {
        var session = new MatchSession
        {
            Id = "roster-header",
            Name = "T5 05/11 17:30 - Thứ 5",
            StartTime = new DateTimeOffset(2026, 11, 5, 19, 0, 0, TimeSpan.FromHours(7)),
            Location = "UTE"
        };

        var copy = SettlementSummaryCardRenderer.BuildRosterHeaderCopy(session);

        Assert.Contains("05/11", copy.Headline);
        Assert.Contains("19:00", copy.Metadata);
        Assert.DoesNotContain("17:30", copy.Headline);
        Assert.DoesNotContain("17:30", copy.Metadata);
        Assert.Contains("UTE", copy.Metadata);
    }

    [Fact]
    public void Payment_roster_all_paid_uses_compact_adaptive_layout()
    {
        var session = new MatchSession
        {
            Id = "roster-paid",
            Name = "T5 05/11 17:30 - Thứ 5",
            StartTime = new DateTimeOffset(2026, 11, 5, 19, 0, 0, TimeSpan.FromHours(7))
        };
        var settlement = new MatchSettlement
        {
            Session = session,
            SessionId = session.Id,
            AmountPerFullSlot = 22_000m,
            Entries = Enumerable.Range(1, 18)
                .Select(index => new SettlementEntry
                {
                    Id = $"paid-{index}",
                    DisplayNameSnapshot = $"Thành viên {index:00}",
                    SlotFactor = index % 6 == 0 ? .5m : 1m,
                    PaymentStatus = SettlementPaymentStatus.Paid,
                    ActualPaidAmount = index % 6 == 0 ? 11_000m : 22_000m
                })
                .ToList()
        };

        var bytes = new SettlementSummaryCardRenderer().RenderPaymentRoster(session, settlement);

        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(1080, bitmap!.Width);
        Assert.InRange(bitmap.Height, 1_050, 1_250);
    }

    [Fact]
    public void Payment_roster_uses_actual_received_amounts_tracks_unpaid_rows_and_totals_received_cash()
    {
        var settlement = new MatchSettlement
        {
            AmountPerFullSlot = 22_000m,
            Entries =
            [
                new SettlementEntry
                {
                    Id = "paid-25",
                    DisplayNameSnapshot = "Đặng Thế Nguyễn",
                    SlotFactor = 1m,
                    PaymentStatus = SettlementPaymentStatus.Paid,
                    ActualPaidAmount = 25_000m
                },
                new SettlementEntry
                {
                    Id = "paid-23",
                    DisplayNameSnapshot = "Trần Hải Nam",
                    SlotFactor = 1m,
                    PaymentStatus = SettlementPaymentStatus.Paid,
                    ActualPaidAmount = 23_000m
                },
                new SettlementEntry
                {
                    Id = "legacy-paid",
                    DisplayNameSnapshot = "Legacy Paid",
                    SlotFactor = .5m,
                    PaymentStatus = SettlementPaymentStatus.Paid,
                    ActualPaidAmount = 0m
                },
                new SettlementEntry
                {
                    Id = "unpaid",
                    DisplayNameSnapshot = "Chưa đóng",
                    SlotFactor = 1m,
                    PaymentStatus = SettlementPaymentStatus.Unpaid,
                    ActualPaidAmount = 0m
                }
            ]
        };

        var roster = SettlementSummaryCardRenderer.BuildPaymentRosterSnapshot(settlement);

        Assert.Equal(4, roster.TotalCount);
        Assert.Equal(3, roster.PaidRows.Count);
        Assert.DoesNotContain(roster.PaidRows, row => row.Name == "Chưa đóng");
        Assert.Single(roster.UnpaidRows);
        Assert.Equal("Chưa đóng", roster.UnpaidRows[0].Name);
        Assert.Equal(22_000m, roster.UnpaidRows[0].Due);
        Assert.Equal(25_000m, roster.PaidRows.Single(row => row.Name == "Đặng Thế Nguyễn").Received);
        Assert.Equal(23_000m, roster.PaidRows.Single(row => row.Name == "Trần Hải Nam").Received);
        Assert.Equal(11_000m, roster.PaidRows.Single(row => row.Name == "Legacy Paid").Received);
        Assert.Equal(59_000m, roster.TotalReceived);
    }

    [Fact]
    public void Payment_roster_all_paid_has_no_unpaid_section_data()
    {
        var settlement = new MatchSettlement
        {
            AmountPerFullSlot = 22_000m,
            Entries =
            [
                new SettlementEntry
                {
                    Id = "paid-a",
                    DisplayNameSnapshot = "A",
                    SlotFactor = 1m,
                    PaymentStatus = SettlementPaymentStatus.Paid,
                    ActualPaidAmount = 25_000m
                },
                new SettlementEntry
                {
                    Id = "paid-b",
                    DisplayNameSnapshot = "B",
                    SlotFactor = 1m,
                    PaymentStatus = SettlementPaymentStatus.Paid,
                    ActualPaidAmount = 22_000m
                }
            ]
        };

        var roster = SettlementSummaryCardRenderer.BuildPaymentRosterSnapshot(settlement);

        Assert.Equal(2, roster.PaidRows.Count);
        Assert.Empty(roster.UnpaidRows);
        Assert.Equal(47_000m, roster.TotalReceived);
    }

    [Fact]
    public void Payment_roster_with_unpaid_people_expands_layout_for_conditional_unpaid_section()
    {
        var session = new MatchSession
        {
            Id = "roster-partial",
            Name = "T5 05/11",
            StartTime = new DateTimeOffset(2026, 11, 5, 19, 0, 0, TimeSpan.FromHours(7))
        };
        var settlement = new MatchSettlement
        {
            Session = session,
            SessionId = session.Id,
            AmountPerFullSlot = 22_000m,
            Entries = Enumerable.Range(1, 18)
                .Select(index => new SettlementEntry
                {
                    Id = $"entry-{index}",
                    DisplayNameSnapshot = $"Thành viên {index:00}",
                    SlotFactor = 1m,
                    PaymentStatus = index <= 15 ? SettlementPaymentStatus.Paid : SettlementPaymentStatus.Unpaid,
                    ActualPaidAmount = index <= 15 ? 22_000m : 0m
                })
                .ToList()
        };

        var bytes = new SettlementSummaryCardRenderer().RenderPaymentRoster(session, settlement);

        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(1080, bitmap!.Width);
        Assert.True(bitmap.Height > 1_250);
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
            var service = new SettlementService(
                db,
                bridge,
                new SettlementSummaryCardRenderer(),
                NullLogger<SettlementService>.Instance);
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
        public int FailuresRemaining { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (FailuresRemaining > 0)
            {
                FailuresRemaining -= 1;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"temporary\"}", Encoding.UTF8, "application/json")
                };
            }
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
