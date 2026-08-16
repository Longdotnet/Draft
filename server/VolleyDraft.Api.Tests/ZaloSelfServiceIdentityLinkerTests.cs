using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSelfServiceIdentityLinkerTests
{
    [Fact]
    public async Task Unique_exact_blank_profile_is_linked_to_realtime_sender_uid()
    {
        await using var fixture = await Fixture.CreateAsync([
            new ProfileSeed("p1", "Đặng Thế Nguyễn", string.Empty)
        ]);

        var result = await new ZaloSelfServiceIdentityLinker(fixture.Db)
            .TryLinkAsync("conn-1", "g1", Incoming());

        Assert.Equal(ZaloSelfServiceIdentityLinkResult.Linked, result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("user-nguyen", (await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync()).ZaloUserId);
    }

    [Fact]
    public async Task Existing_same_uid_is_already_linked_without_changes()
    {
        await using var fixture = await Fixture.CreateAsync([
            new ProfileSeed("p1", "Đặng Thế Nguyễn", "user-nguyen")
        ]);

        var result = await new ZaloSelfServiceIdentityLinker(fixture.Db)
            .TryLinkAsync("conn-1", "g1", Incoming());

        Assert.Equal(ZaloSelfServiceIdentityLinkResult.AlreadyLinked, result);
        Assert.Equal("user-nguyen", (await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync()).ZaloUserId);
    }

    [Fact]
    public async Task Duplicate_same_display_names_are_ambiguous_and_never_guessed()
    {
        // PlayerProfile.ZaloUserId is unique in the relational schema. Use two
        // distinct whitespace-only legacy values so both are still semantically
        // blank to the linker without violating the database constraint.
        await using var fixture = await Fixture.CreateAsync([
            new ProfileSeed("p1", "Đặng Thế Nguyễn", string.Empty),
            new ProfileSeed("p2", "Đặng Thế Nguyễn", " ")
        ]);

        var result = await new ZaloSelfServiceIdentityLinker(fixture.Db)
            .TryLinkAsync("conn-1", "g1", Incoming());

        Assert.Equal(ZaloSelfServiceIdentityLinkResult.Ambiguous, result);
        Assert.All(await fixture.Db.PlayerProfiles.AsNoTracking().ToListAsync(), profile =>
            Assert.True(string.IsNullOrWhiteSpace(profile.ZaloUserId)));
    }

    [Fact]
    public async Task Different_existing_uid_is_a_conflict_and_is_never_overwritten()
    {
        await using var fixture = await Fixture.CreateAsync([
            new ProfileSeed("p1", "Đặng Thế Nguyễn", "somebody-else")
        ]);

        var result = await new ZaloSelfServiceIdentityLinker(fixture.Db)
            .TryLinkAsync("conn-1", "g1", Incoming());

        Assert.Equal(ZaloSelfServiceIdentityLinkResult.Conflict, result);
        Assert.Equal("somebody-else", (await fixture.Db.PlayerProfiles.AsNoTracking().SingleAsync()).ZaloUserId);
    }

    private static ZaloIncomingMessageEvent Incoming() => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: Guid.NewGuid().ToString("n"),
        senderId: "user-nguyen",
        senderName: "Đặng Thế Nguyễn",
        content: "@Npc xếp tui chung team với To An ở CN 16/8 đi",
        mentions: [],
        mentionedBot: true,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed record ProfileSeed(string Id, string Name, string ZaloUserId);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync(IReadOnlyList<ProfileSeed> seeds)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"identity-{Guid.NewGuid():n}@example.test",
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

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            foreach (var seed in seeds)
            {
                var profile = new PlayerProfile
                {
                    Id = seed.Id,
                    ZaloUserId = seed.ZaloUserId,
                    DisplayName = seed.Name
                };
                db.PlayerProfiles.Add(profile);
                session.Players.Add(new SessionPlayer
                {
                    Id = $"sp-{seed.Id}",
                    SessionId = session.Id,
                    PlayerProfileId = profile.Id,
                    PlayerProfile = profile,
                    DisplayName = seed.Name,
                    IsPresent = true
                });
            }
            db.MatchSessions.Add(session);
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
