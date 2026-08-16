using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamPreferenceSelfServiceRegressionTests
{
    [Fact]
    public async Task Screenshot_case_links_sender_before_legacy_snapshot_so_request_is_self_service()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = $"team-self-{Guid.NewGuid():n}@example.test",
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
        var nguyen = new PlayerProfile
        {
            Id = "profile-nguyen",
            ZaloUserId = string.Empty,
            DisplayName = "Đặng Thế Nguyễn"
        };
        var toAn = new PlayerProfile
        {
            Id = "profile-toan",
            ZaloUserId = "user-toan",
            DisplayName = "To An"
        };
        var session = new MatchSession
        {
            Id = "session-cn",
            Name = "CN 16/8",
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
        session.Players.AddRange([
            new SessionPlayer
            {
                Id = "sp-nguyen",
                SessionId = session.Id,
                PlayerProfileId = nguyen.Id,
                PlayerProfile = nguyen,
                DisplayName = nguyen.DisplayName,
                IsPresent = true
            },
            new SessionPlayer
            {
                Id = "sp-toan",
                SessionId = session.Id,
                PlayerProfileId = toAn.Id,
                PlayerProfile = toAn,
                DisplayName = toAn.DisplayName,
                IsPresent = true
            }
        ]);
        db.Users.Add(admin);
        db.ZaloConnections.Add(zalo);
        db.PlayerProfiles.AddRange(nguyen, toAn);
        db.MatchSessions.Add(session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        const string content = "@Npc Xếp tui chung team với @To An ở CN 16/8 đi";
        var incoming = new ZaloIncomingMessageEvent(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: "screenshot-case",
            senderId: "user-nguyen",
            senderName: "Đặng Thế Nguyễn",
            content: content,
            mentions:
            [
                new ZaloBridgeMention("bot-account", 0, "@Npc".Length),
                new ZaloBridgeMention(
                    "user-toan",
                    content.IndexOf("@To An", StringComparison.Ordinal),
                    "@To An".Length)
            ],
            mentionedBot: true,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var linkResult = await new ZaloSelfServiceIdentityLinker(db)
            .TryLinkAsync("conn-1", "g1", incoming);

        Assert.Equal(ZaloSelfServiceIdentityLinkResult.Linked, linkResult);
        db.ChangeTracker.Clear();

        var senderListedExactlyLikeLegacySnapshot = await db.SessionPlayers
            .AsNoTracking()
            .Where(player => player.SessionId == session.Id && player.IsPresent)
            .AnyAsync(player => player.PlayerProfile != null &&
                                player.PlayerProfile.ZaloUserId == incoming.SenderId);
        Assert.True(senderListedExactlyLikeLegacySnapshot);

        Assert.True(ZaloNaturalCommandParser.TryParseTeamPreference(
            ZaloBotService.ExtractQuestion(incoming),
            out var parsed));
        var bound = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(
            [new ZaloMentionedUser("user-toan", "To An")],
            parsed);
        Assert.NotNull(bound);
        Assert.Equal("tui", bound!.PlayerReferences[0], ignoreCase: true);
        Assert.Equal("user-toan", bound.PlayerZaloUserIds![1]);

        // The existing team-preference handler defines self-service as: sender is in
        // the selected roster and at least one requested player resolves to the
        // sender UID/name. These are exactly the values consumed after the linker.
        var selfService = senderListedExactlyLikeLegacySnapshot &&
                          bound.PlayerReferences.Any(reference =>
                              ZaloBotIntelligence.Normalize(reference) == "tui" ||
                              ZaloBotIntelligence.Normalize(reference) ==
                              ZaloBotIntelligence.Normalize(incoming.SenderName));
        Assert.True(selfService);
    }
}
