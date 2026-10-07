using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDraftTeamPreferenceFinalReconcileTests
{
    [Fact]
    public async Task Draft_rejects_transitive_together_share_component_that_contains_apart_pair()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin-pref-apart-final",
            DisplayName = "Admin",
            Email = "admin-pref-apart-final@example.test",
            PasswordHash = "test"
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = new SessionDraftService(db);
        var created = await service.CreateSessionAsync(admin.Id, new CreateSessionRequest("T6", 3, 2));
        Assert.True(created.IsSuccess);
        var sessionId = created.Value!.Id;
        var playerIds = new List<string>();
        for (var i = 1; i <= 8; i++)
        {
            var added = await service.AddPlayerAsync(
                admin.Id,
                sessionId,
                new AddPlayerRequest($"P{i}", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
            Assert.True(added.IsSuccess);
            playerIds.Add(added.Value!.Id);
        }

        var captains = await service.SetManualCaptainsAsync(
            admin.Id,
            sessionId,
            new ManualCaptainsRequest(playerIds.Take(3).ToList()));
        Assert.True(captains.IsSuccess);

        var group = new TeamPreferenceGroup { SessionId = sessionId };
        group.Players.Add(new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = group.Id,
            SessionPlayerId = playerIds[0],
            RotationOrder = 1
        });
        group.Players.Add(new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = group.Id,
            SessionPlayerId = playerIds[3],
            RotationOrder = 2
        });
        var sharedSlot = new DraftSlot
        {
            SessionId = sessionId,
            Type = DraftSlotType.Shared,
            DisplayName = "P4 / P5"
        };
        sharedSlot.Players.Add(new DraftSlotPlayer
        {
            DraftSlotId = sharedSlot.Id,
            SessionPlayerId = playerIds[3],
            RotationOrder = 1
        });
        sharedSlot.Players.Add(new DraftSlotPlayer
        {
            DraftSlotId = sharedSlot.Id,
            SessionPlayerId = playerIds[4],
            RotationOrder = 2
        });
        db.TeamPreferenceGroups.Add(group);
        db.DraftSlots.Add(sharedSlot);
        db.TeamSeparationConstraints.Add(new TeamSeparationConstraint
        {
            SessionId = sessionId,
            FirstSessionPlayerId = playerIds[0],
            SecondSessionPlayerId = playerIds[4]
        });
        await db.SaveChangesAsync();

        var started = await service.StartDraftAsync(admin.Id, sessionId);

        Assert.False(started.IsSuccess);
        Assert.Contains("khác team", started.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(SessionStatus.Drafting, await db.MatchSessions
            .Where(item => item.Id == sessionId)
            .Select(item => item.Status)
            .SingleAsync());
    }

    [Fact]
    public async Task Draft_reconciles_historical_stale_preference_before_consuming_constraints()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin-pref-final",
            DisplayName = "Admin",
            Email = "admin-pref-final@example.test",
            PasswordHash = "test"
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = new SessionDraftService(db);
        var created = await service.CreateSessionAsync(admin.Id, new CreateSessionRequest("CN", 3, 2));
        Assert.True(created.IsSuccess);
        var sessionId = created.Value!.Id;

        var playerIds = new List<string>();
        for (var i = 1; i <= 10; i++)
        {
            var added = await service.AddPlayerAsync(
                admin.Id,
                sessionId,
                new AddPlayerRequest($"P{i}", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
            Assert.True(added.IsSuccess);
            playerIds.Add(added.Value!.Id);
        }

        var captains = await service.SetManualCaptainsAsync(
            admin.Id,
            sessionId,
            new ManualCaptainsRequest(playerIds.Take(3).ToList()));
        Assert.True(captains.IsSuccess);

        var stalePlayer = await db.SessionPlayers.SingleAsync(player => player.Id == playerIds[9]);
        stalePlayer.IsPresent = false;
        var group = new TeamPreferenceGroup { SessionId = sessionId };
        group.Players.Add(new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = group.Id,
            SessionPlayerId = playerIds[0],
            RotationOrder = 1
        });
        group.Players.Add(new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = group.Id,
            SessionPlayerId = playerIds[3],
            RotationOrder = 2
        });
        group.Players.Add(new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = group.Id,
            SessionPlayerId = playerIds[9],
            RotationOrder = 3
        });
        db.TeamPreferenceGroups.Add(group);
        await db.SaveChangesAsync();

        var started = await service.StartDraftAsync(admin.Id, sessionId);

        Assert.True(started.IsSuccess, started.Error);
        Assert.Equal(SessionStatus.Drafting, started.Value!.SessionStatus);
        var remaining = await db.TeamPreferenceGroupPlayers.AsNoTracking()
            .Where(link => link.TeamPreferenceGroupId == group.Id)
            .OrderBy(link => link.RotationOrder)
            .ToListAsync();
        Assert.Equal([playerIds[0], playerIds[3]], remaining.Select(link => link.SessionPlayerId).ToArray());
        Assert.Equal([1, 2], remaining.Select(link => link.RotationOrder).ToArray());
        Assert.DoesNotContain(remaining, link => link.SessionPlayerId == playerIds[9]);
    }

    [Fact]
    public async Task Draft_deletes_stale_preference_when_fewer_than_two_members_remain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin-pref-delete",
            DisplayName = "Admin",
            Email = "admin-pref-delete@example.test",
            PasswordHash = "test"
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = new SessionDraftService(db);
        var created = await service.CreateSessionAsync(admin.Id, new CreateSessionRequest("T6", 3, 2));
        Assert.True(created.IsSuccess);
        var sessionId = created.Value!.Id;

        var playerIds = new List<string>();
        for (var i = 1; i <= 11; i++)
        {
            var added = await service.AddPlayerAsync(
                admin.Id,
                sessionId,
                new AddPlayerRequest($"P{i}", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
            Assert.True(added.IsSuccess);
            playerIds.Add(added.Value!.Id);
        }

        var captains = await service.SetManualCaptainsAsync(
            admin.Id,
            sessionId,
            new ManualCaptainsRequest(playerIds.Take(3).ToList()));
        Assert.True(captains.IsSuccess);

        (await db.SessionPlayers.SingleAsync(player => player.Id == playerIds[9])).IsPresent = false;
        (await db.SessionPlayers.SingleAsync(player => player.Id == playerIds[10])).IsPresent = false;
        var group = new TeamPreferenceGroup { SessionId = sessionId };
        group.Players.Add(new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = group.Id,
            SessionPlayerId = playerIds[3],
            RotationOrder = 1
        });
        group.Players.Add(new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = group.Id,
            SessionPlayerId = playerIds[9],
            RotationOrder = 2
        });
        group.Players.Add(new TeamPreferenceGroupPlayer
        {
            TeamPreferenceGroupId = group.Id,
            SessionPlayerId = playerIds[10],
            RotationOrder = 3
        });
        db.TeamPreferenceGroups.Add(group);
        await db.SaveChangesAsync();

        var started = await service.StartDraftAsync(admin.Id, sessionId);

        Assert.True(started.IsSuccess, started.Error);
        Assert.False(await db.TeamPreferenceGroups.AsNoTracking().AnyAsync(item => item.Id == group.Id));
        Assert.False(await db.TeamPreferenceGroupPlayers.AsNoTracking()
            .AnyAsync(link => link.TeamPreferenceGroupId == group.Id));
    }
}
