using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientTeamLineupTests
{
    private static readonly ZaloAmbientSettings Settings = new(
        Enabled: true,
        ShadowMode: false,
        WouldReplyThreshold: 60,
        RecentWindowMinutes: 5,
        MaxRecentMessages: 40,
        BotCooldownSeconds: 2,
        BusyGroupMessagesPerTwoMinutes: 8);

    [Fact]
    public async Task Untagged_team_lineup_reads_current_assigned_slots()
    {
        await using var fixture = await Fixture.CreateAsync(withLineup: true);
        var incoming = Incoming("lineup-1", "danh sách team T6");
        var decision = Decide(incoming);

        Assert.True(decision.WouldReply);
        Assert.Equal(ZaloBotIntent.TeamLineup.ToString(), decision.Intent);

        var reply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, 60);

        Assert.NotNull(reply);
        Assert.Equal(ZaloBotIntent.TeamLineup, reply!.Intent);
        Assert.Equal("session-t6", reply.SessionId);
        Assert.Contains("Đội hình T6", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Team A", reply.Text);
        Assert.Contains("Long", reply.Text);
        Assert.Contains("Nam", reply.Text);
        Assert.Contains("Team B", reply.Text);
        Assert.Contains("To An", reply.Text);
    }

    [Fact]
    public async Task Untagged_team_lineup_without_draft_returns_safe_read_only_status()
    {
        await using var fixture = await Fixture.CreateAsync(withLineup: false);
        var incoming = Incoming("lineup-empty", "danh sách team T6");
        var decision = Decide(incoming);
        var teamCountBefore = await fixture.Db.Teams.AsNoTracking().CountAsync();
        var slotCountBefore = await fixture.Db.DraftSlots.AsNoTracking().CountAsync();

        var reply = await new ZaloAmbientFactResponder(fixture.Db).TryBuildAsync(
            "bot-account", "g1", incoming, decision, 60);

        Assert.NotNull(reply);
        Assert.Contains("chưa có kết quả chia team", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(teamCountBefore, await fixture.Db.Teams.AsNoTracking().CountAsync());
        Assert.Equal(slotCountBefore, await fixture.Db.DraftSlots.AsNoTracking().CountAsync());
    }

    private static ZaloAmbientParticipationDecision Decide(ZaloIncomingMessageEvent incoming) =>
        ZaloAmbientParticipationEngine.Evaluate(
            incoming,
            new ZaloAmbientGroupSituation(1, 1, 1, 0, null, [incoming.MessageId]),
            Settings,
            DateTimeOffset.UtcNow);

    private static ZaloIncomingMessageEvent Incoming(string messageId, string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: messageId,
        senderId: "user-long",
        senderName: "Long",
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync(bool withLineup)
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
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"ambient-lineup-{Guid.NewGuid():n}@example.test",
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

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.MatchSessions.Add(session);

            if (withLineup)
            {
                var longPlayer = new SessionPlayer
                {
                    Id = "player-long",
                    SessionId = session.Id,
                    Session = session,
                    DisplayName = "Long",
                    IsPresent = true,
                    Score = 8
                };
                var toAnPlayer = new SessionPlayer
                {
                    Id = "player-toan",
                    SessionId = session.Id,
                    Session = session,
                    DisplayName = "To An",
                    IsPresent = true,
                    Score = 7
                };
                session.Players.Add(longPlayer);
                session.Players.Add(toAnPlayer);

                var teamA = new Team
                {
                    Id = "team-a",
                    SessionId = session.Id,
                    Session = session,
                    Name = "Team A",
                    CaptainSessionPlayerId = longPlayer.Id,
                    CaptainSessionPlayer = longPlayer
                };
                var teamB = new Team
                {
                    Id = "team-b",
                    SessionId = session.Id,
                    Session = session,
                    Name = "Team B",
                    CaptainSessionPlayerId = toAnPlayer.Id,
                    CaptainSessionPlayer = toAnPlayer
                };
                session.Teams.Add(teamA);
                session.Teams.Add(teamB);

                db.DraftSlots.AddRange(
                    new DraftSlot
                    {
                        Id = "slot-long",
                        SessionId = session.Id,
                        Session = session,
                        DisplayName = "Long",
                        IsCaptainSlot = true,
                        AverageScore = 8,
                        AssignedTeamId = teamA.Id,
                        AssignedTeam = teamA
                    },
                    new DraftSlot
                    {
                        Id = "slot-nam",
                        SessionId = session.Id,
                        Session = session,
                        DisplayName = "Nam",
                        AverageScore = 6,
                        AssignedTeamId = teamA.Id,
                        AssignedTeam = teamA
                    },
                    new DraftSlot
                    {
                        Id = "slot-toan",
                        SessionId = session.Id,
                        Session = session,
                        DisplayName = "To An",
                        IsCaptainSlot = true,
                        AverageScore = 7,
                        AssignedTeamId = teamB.Id,
                        AssignedTeam = teamB
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
