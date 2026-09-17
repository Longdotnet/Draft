using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamPreferenceExitServiceTests
{
    [Fact]
    public async Task Creator_can_preview_and_remove_another_member_without_breaking_remaining_group()
    {
        await using var fixture = await Fixture.CreateAsync(withCreatorProvenance: true);
        var service = new ZaloTeamPreferenceExitService(fixture.Db);
        var candidates = await service.LoadCandidatesAsync("conn-1", "g1", "user-long");
        var plan = service.BuildPlan(
            candidates,
            "user-long",
            "m-preview",
            "user-toan",
            "To An",
            null,
            aiInterpreted: true,
            semanticConfidence: .96,
            semanticReason: "dislike_target",
            out var clarification);

        Assert.Null(clarification);
        Assert.NotNull(plan);
        Assert.Equal(ZaloTeamPreferenceExitAction.RemoveTarget, plan!.Action);
        Assert.Equal("sp-toan", plan.RemovePlayerId);

        var result = await service.ApplyAsync("user-long", plan);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.Value!.GroupDeleted);
        var remaining = await fixture.Db.TeamPreferenceGroupPlayers.AsNoTracking()
            .Where(link => link.TeamPreferenceGroupId == "pref-1")
            .Select(link => link.SessionPlayerId)
            .ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains("sp-long", remaining);
        Assert.Contains("sp-nguyen", remaining);
        Assert.DoesNotContain("sp-toan", remaining);
    }

    [Fact]
    public async Task Ordinary_member_cannot_kick_target_and_withdraws_self_instead()
    {
        await using var fixture = await Fixture.CreateAsync(withCreatorProvenance: true);
        var service = new ZaloTeamPreferenceExitService(fixture.Db);
        var candidates = await service.LoadCandidatesAsync("conn-1", "g1", "user-nguyen");
        var plan = service.BuildPlan(
            candidates,
            "user-nguyen",
            "m-preview",
            "user-long",
            "Thanh Long",
            null,
            aiInterpreted: true,
            semanticConfidence: .94,
            semanticReason: "does_not_want_target",
            out var clarification);

        Assert.Null(clarification);
        Assert.NotNull(plan);
        Assert.Equal(ZaloTeamPreferenceExitAction.RemoveSelf, plan!.Action);
        Assert.Equal("sp-nguyen", plan.RemovePlayerId);

        var result = await service.ApplyAsync("user-nguyen", plan);

        Assert.True(result.IsSuccess, result.Error);
        var remaining = await fixture.Db.TeamPreferenceGroupPlayers.AsNoTracking()
            .Where(link => link.TeamPreferenceGroupId == "pref-1")
            .Select(link => link.SessionPlayerId)
            .ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains("sp-long", remaining);
        Assert.Contains("sp-toan", remaining);
        Assert.DoesNotContain("sp-nguyen", remaining);
    }

    [Fact]
    public async Task Withdrawing_from_two_person_group_deletes_meaningless_singleton_group()
    {
        await using var fixture = await Fixture.CreateAsync(withCreatorProvenance: false, includeNguyen: false);
        var service = new ZaloTeamPreferenceExitService(fixture.Db);
        var candidates = await service.LoadCandidatesAsync("conn-1", "g1", "user-toan");
        var plan = service.BuildPlan(
            candidates,
            "user-toan",
            "m-preview",
            "user-long",
            "Thanh Long",
            null,
            aiInterpreted: false,
            semanticConfidence: 1,
            semanticReason: "explicit_negation",
            out _)!;

        var result = await service.ApplyAsync("user-toan", plan);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value!.GroupDeleted);
        Assert.False(await fixture.Db.TeamPreferenceGroups.AsNoTracking().AnyAsync());
        Assert.False(await fixture.Db.TeamPreferenceGroupPlayers.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task Confirmation_fails_closed_when_group_membership_changed_after_preview()
    {
        await using var fixture = await Fixture.CreateAsync(withCreatorProvenance: true);
        var service = new ZaloTeamPreferenceExitService(fixture.Db);
        var candidates = await service.LoadCandidatesAsync("conn-1", "g1", "user-long");
        var plan = service.BuildPlan(
            candidates,
            "user-long",
            "m-preview",
            "user-toan",
            "To An",
            null,
            aiInterpreted: false,
            semanticConfidence: 1,
            semanticReason: "explicit_negation",
            out _)!;

        var chiProfile = new PlayerProfile { Id = "profile-chi", ZaloUserId = "user-chi", DisplayName = "Chi" };
        var chi = new SessionPlayer
        {
            Id = "sp-chi",
            SessionId = "session-t4",
            PlayerProfileId = chiProfile.Id,
            PlayerProfile = chiProfile,
            DisplayName = "Chi",
            IsPresent = true
        };
        fixture.Db.PlayerProfiles.Add(chiProfile);
        fixture.Db.SessionPlayers.Add(chi);
        fixture.Db.TeamPreferenceGroupPlayers.Add(new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = "pref-1",
            SessionPlayerId = chi.Id,
            RotationOrder = 3
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await service.ApplyAsync("user-long", plan);

        Assert.False(result.IsSuccess);
        Assert.Equal(4, await fixture.Db.TeamPreferenceGroupPlayers.AsNoTracking()
            .CountAsync(link => link.TeamPreferenceGroupId == "pref-1"));
        Assert.True(await fixture.Db.TeamPreferenceGroupPlayers.AsNoTracking()
            .AnyAsync(link => link.TeamPreferenceGroupId == "pref-1" && link.SessionPlayerId == "sp-toan"));
    }

    [Fact]
    public async Task Confirmation_fails_closed_when_draft_started_after_preview()
    {
        await using var fixture = await Fixture.CreateAsync(withCreatorProvenance: true);
        var service = new ZaloTeamPreferenceExitService(fixture.Db);
        var candidates = await service.LoadCandidatesAsync("conn-1", "g1", "user-long");
        var plan = service.BuildPlan(
            candidates,
            "user-long",
            "m-preview",
            "user-toan",
            "To An",
            null,
            aiInterpreted: false,
            semanticConfidence: 1,
            semanticReason: "explicit_negation",
            out _)!;

        await fixture.Db.MatchSessions.Where(session => session.Id == "session-t4")
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.Status, SessionStatus.Drafting));

        var result = await service.ApplyAsync("user-long", plan);

        Assert.False(result.IsSuccess);
        Assert.Equal(3, await fixture.Db.TeamPreferenceGroupPlayers.AsNoTracking()
            .CountAsync(link => link.TeamPreferenceGroupId == "pref-1"));
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

        public static async Task<Fixture> CreateAsync(bool withCreatorProvenance, bool includeNguyen = true)
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
                Email = $"team-exit-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var connectionRow = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Bott",
                EncryptedCredentials = "test"
            };
            var session = new MatchSession
            {
                Id = "session-t4",
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = connectionRow.Id,
                ZaloConnection = connectionRow,
                ZaloGroupId = "g1",
                Name = "T4 16/09 17:30 - thứ 4",
                BotEnabled = true,
                Status = SessionStatus.Setup,
                StartTime = DateTimeOffset.UtcNow.AddDays(2),
                TeamCount = 3,
                TeamSize = 6
            };

            var longProfile = Profile("profile-long", "user-long", "Thanh Long");
            var toAnProfile = Profile("profile-toan", "user-toan", "To An");
            var nguyenProfile = Profile("profile-nguyen", "user-nguyen", "Đặng Thế Nguyễn");
            var longPlayer = Player("sp-long", session.Id, longProfile);
            var toAnPlayer = Player("sp-toan", session.Id, toAnProfile);
            var nguyenPlayer = Player("sp-nguyen", session.Id, nguyenProfile);
            session.Players.Add(longPlayer);
            session.Players.Add(toAnPlayer);
            if (includeNguyen) session.Players.Add(nguyenPlayer);

            var preference = new TeamPreferenceGroup { Id = "pref-1", SessionId = session.Id, Session = session };
            preference.Players.Add(new TeamPreferenceGroupPlayer
            {
                TeamPreferenceGroupId = preference.Id,
                TeamPreferenceGroup = preference,
                SessionPlayerId = longPlayer.Id,
                SessionPlayer = longPlayer,
                RotationOrder = 0
            });
            preference.Players.Add(new TeamPreferenceGroupPlayer
            {
                TeamPreferenceGroupId = preference.Id,
                TeamPreferenceGroup = preference,
                SessionPlayerId = toAnPlayer.Id,
                SessionPlayer = toAnPlayer,
                RotationOrder = 1
            });
            if (includeNguyen)
            {
                preference.Players.Add(new TeamPreferenceGroupPlayer
                {
                    TeamPreferenceGroupId = preference.Id,
                    TeamPreferenceGroup = preference,
                    SessionPlayerId = nguyenPlayer.Id,
                    SessionPlayer = nguyenPlayer,
                    RotationOrder = 2
                });
            }

            db.Users.Add(admin);
            db.ZaloConnections.Add(connectionRow);
            db.PlayerProfiles.AddRange(longProfile, toAnProfile, nguyenProfile);
            db.MatchSessions.Add(session);
            db.TeamPreferenceGroups.Add(preference);
            if (withCreatorProvenance)
            {
                db.ZaloBotActionHistory.Add(new ZaloBotActionHistory
                {
                    Id = "history-create",
                    SessionId = session.Id,
                    ActorZaloUserId = "user-long",
                    ActorName = "Thanh Long",
                    ActionType = "TeamPreference",
                    Summary = "Thanh Long tạo/mở rộng nhóm",
                    BeforeStateJson = "{}",
                    AfterStateJson = "{\"preferenceGroupId\":\"pref-1\"}",
                    BeforeHash = "before",
                    AfterHash = "after"
                });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        private static PlayerProfile Profile(string id, string uid, string name) => new()
        {
            Id = id,
            ZaloUserId = uid,
            DisplayName = name
        };

        private static SessionPlayer Player(string id, string sessionId, PlayerProfile profile) => new()
        {
            Id = id,
            SessionId = sessionId,
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            DisplayName = profile.DisplayName,
            IsPresent = true,
            Role = PlayerRole.FullStack,
            Level = PlayerLevel.Average,
            Gender = PlayerGender.Unknown,
            Score = 2
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
