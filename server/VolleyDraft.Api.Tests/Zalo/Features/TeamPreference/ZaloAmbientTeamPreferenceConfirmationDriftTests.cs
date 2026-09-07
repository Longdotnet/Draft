using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientTeamPreferenceConfirmationDriftTests
{
    [Fact]
    public async Task Exact_reply_does_not_silently_expand_two_person_proposal_into_existing_group()
    {
        await using var fixture = await Fixture.CreateAsync(withExistingPartnerGroup: true);
        await fixture.SaveProposalAsync();

        var incoming = Confirmation("confirm-expanded");
        var result = await new ZaloMemoryV2Service(fixture.Db)
            .ProcessAsync("g1", incoming, incoming.Content);

        Assert.True(result.Handled);
        Assert.NotNull(result.Response);
        Assert.Contains("không còn chỉ Long + To An", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Long", result.Response, StringComparison.Ordinal);
        Assert.Contains("To An", result.Response, StringComparison.Ordinal);
        Assert.Contains("Chi", result.Response, StringComparison.Ordinal);
        Assert.Contains("Mình chưa áp dụng", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Npc xếp tui chung team với @To An ở T6 đi", result.Response, StringComparison.Ordinal);

        Assert.Empty(await fixture.Db.ZaloBotConversationStates.AsNoTracking().ToListAsync());
        Assert.Null(await new ZaloConversationStateV2Store(fixture.Db)
            .LoadActiveAsync("g1", "user-long"));

        var groups = await fixture.Db.TeamPreferenceGroups.AsNoTracking()
            .Include(group => group.Players)
            .ToListAsync();
        var group = Assert.Single(groups);
        Assert.Equal(
            new[] { "session-chi", "session-toan" },
            group.Players.Select(link => link.SessionPlayerId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task Exact_reply_still_promotes_when_fresh_plan_matches_disclosed_pair()
    {
        await using var fixture = await Fixture.CreateAsync(withExistingPartnerGroup: false);
        await fixture.SaveProposalAsync();

        var incoming = Confirmation("confirm-exact-pair");
        var result = await new ZaloMemoryV2Service(fixture.Db)
            .ProcessAsync("g1", incoming, incoming.Content);

        Assert.False(result.Handled);
        Assert.Null(result.Response);
        var pending = await fixture.Db.ZaloBotConversationStates.AsNoTracking().SingleAsync();
        Assert.Equal(ZaloBotIntent.TeamPreferenceConfirm.ToString(), pending.PendingIntent);

        using var payload = JsonDocument.Parse(pending.PendingPayloadJson);
        var names = payload.RootElement.GetProperty("Plan").GetProperty("PlayerNames")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { "Long", "To An" }, names);
    }

    private static ZaloIncomingMessageEvent Confirmation(string messageId) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Long",
        content: "xác nhận",
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        quote: new ZaloBridgeMessageQuote(
            "provider-proposal",
            "bot-account",
            "Volley Bot",
            "Long + To An muốn chung team ở T6. Reply tin này và xác nhận để áp dụng.",
            "text",
            DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(),
            null));

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync(bool withExistingPartnerGroup)
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
                Email = $"confirmation-drift-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Volley Bot",
                EncryptedCredentials = "test"
            };
            var longProfile = Profile("profile-long", "user-long", "Long");
            var toAnProfile = Profile("profile-toan", "user-toan", "To An");
            var chiProfile = Profile("profile-chi", "user-chi", "Chi");
            var session = new MatchSession
            {
                Id = "session-t6",
                AdminUserId = admin.Id,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                Name = "T6",
                Status = SessionStatus.Setup,
                BotEnabled = true,
                StartTime = DateTimeOffset.UtcNow.AddDays(1),
                TeamCount = 3,
                TeamSize = 6
            };
            var longPlayer = Player("session-long", session.Id, longProfile);
            var toAnPlayer = Player("session-toan", session.Id, toAnProfile);
            var chiPlayer = Player("session-chi", session.Id, chiProfile);
            session.Players.AddRange([longPlayer, toAnPlayer, chiPlayer]);

            if (withExistingPartnerGroup)
            {
                var group = new TeamPreferenceGroup
                {
                    Id = "existing-toan-chi",
                    SessionId = session.Id
                };
                group.Players.AddRange([
                    new TeamPreferenceGroupPlayer
                    {
                        TeamPreferenceGroupId = group.Id,
                        SessionPlayerId = toAnPlayer.Id,
                        SessionPlayer = toAnPlayer,
                        RotationOrder = 1
                    },
                    new TeamPreferenceGroupPlayer
                    {
                        TeamPreferenceGroupId = group.Id,
                        SessionPlayerId = chiPlayer.Id,
                        SessionPlayer = chiPlayer,
                        RotationOrder = 2
                    }
                ]);
                session.TeamPreferenceGroups.Add(group);
            }

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.PlayerProfiles.AddRange(longProfile, toAnProfile, chiProfile);
            db.MatchSessions.Add(session);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async Task SaveProposalAsync()
        {
            var collected = JsonSerializer.Serialize(new
            {
                requesterZaloUserId = "user-long",
                requesterDisplayName = "Long",
                partnerZaloUserId = "user-toan",
                partnerDisplayName = "To An",
                sessionId = "session-t6",
                sessionName = "T6",
                metadata = new
                {
                    speechAct = "proposal",
                    writeAuthorized = false,
                    domain = "TeamPreference"
                }
            });
            await new ZaloConversationStateV2Store(Db).SaveActiveAsync(
                "g1",
                "user-long",
                ZaloAmbientTeamPreferenceHandoff.ProposalIntent,
                collected,
                "[]",
                "[]",
                "proposal-source",
                "proposal-source",
                DateTimeOffset.UtcNow.AddMinutes(5));
            await new ZaloMessageGraphStore(Db).RememberOutboundAsync(
                "conn-1",
                "g1",
                "provider-proposal",
                "proposal-source");
        }

        private static PlayerProfile Profile(string id, string uid, string name) => new()
        {
            Id = id,
            ZaloUserId = uid,
            DisplayName = name,
            Gender = PlayerGender.Male,
            DefaultRole = PlayerRole.Attack,
            DefaultLevel = PlayerLevel.Average
        };

        private static SessionPlayer Player(string id, string sessionId, PlayerProfile profile) => new()
        {
            Id = id,
            SessionId = sessionId,
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            DisplayName = profile.DisplayName,
            Gender = PlayerGender.Male,
            Role = PlayerRole.Attack,
            Level = PlayerLevel.Average,
            Score = 2,
            IsPresent = true,
            IsCaptainEligible = true
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
