using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloProactiveTargetResolverTests
{
    [Fact]
    public async Task Configured_group_remains_eligible_without_any_match_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTrackedGroupAsync("g-configured");

        var targets = await new ZaloProactiveTargetResolver(fixture.Db).GetTargetsAsync();

        var target = Assert.Single(targets);
        Assert.Equal("conn-1", target.ConnectionId);
        Assert.Equal("g-configured", target.GroupId);
        Assert.Equal("bot-account", target.AccountId);
        Assert.Empty(await fixture.Db.MatchSessions.ToListAsync());
    }

    [Fact]
    public async Task Configured_and_legacy_session_sources_are_deduplicated()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTrackedGroupAsync("g1");
        fixture.Db.MatchSessions.Add(new MatchSession
        {
            Id = "session-1",
            Name = "T6",
            AdminUserId = fixture.Admin.Id,
            ZaloConnectionId = fixture.Connection.Id,
            ZaloGroupId = "g1",
            BotEnabled = true,
            Status = SessionStatus.Setup,
            StartTime = DateTimeOffset.UtcNow.AddDays(1)
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var targets = await new ZaloProactiveTargetResolver(fixture.Db).GetTargetsAsync();

        Assert.Single(targets);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteConnection sqlite,
            VolleyDraftDbContext db,
            User admin,
            ZaloConnection connection)
        {
            Sqlite = sqlite;
            Db = db;
            Admin = admin;
            Connection = connection;
        }

        public SqliteConnection Sqlite { get; }
        public VolleyDraftDbContext Db { get; }
        public User Admin { get; }
        public ZaloConnection Connection { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(sqlite)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"proactive-target-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var connection = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test",
                Status = ZaloConnectionStatus.Connected
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(connection);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return new Fixture(sqlite, db, admin, connection);
        }

        public async Task AddTrackedGroupAsync(string groupId)
        {
            await new ZaloAutoSessionStore(Db).EnsureAsync();
            var now = DateTimeOffset.UtcNow.ToString("O");
            await Db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "ZaloTrackedGroups" (
                    "Id", "AdminUserId", "ZaloConnectionId", "GroupId", "GroupName", "CreatedAt", "UpdatedAt")
                VALUES (
                    {{Guid.NewGuid().ToString("n")}}, {{Admin.Id}}, {{Connection.Id}}, {{groupId}}, {{groupId}}, {{now}}, {{now}});
                """);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Sqlite.DisposeAsync();
        }
    }
}
