using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ConcurrentManualDraftStartFuzzTests
{
    [Fact]
    public async Task Concurrent_manual_draft_starts_are_single_winner_and_never_duplicate_draft_state()
    {
        for (var seed = 1; seed <= 32; seed += 1)
        {
            var connectionString = $"Data Source=manual-draft-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            var seeded = await SeedReadySessionAsync(options, seed);
            await using var primary = new VolleyDraftDbContext(options);
            await using var secondary = new VolleyDraftDbContext(options);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = RunStartAsync(primary, seeded.AdminId, seeded.SessionId, start.Task, seed % 3);
            var second = RunStartAsync(secondary, seeded.AdminId, seeded.SessionId, start.Task, (seed + 1) % 3);

            start.SetResult();
            var outcomes = await Task.WhenAll(first, second);

            Assert.All(outcomes, outcome => Assert.Null(outcome.Exception));
            Assert.Equal(1, outcomes.Count(outcome => outcome.IsSuccess));
            Assert.Equal(1, outcomes.Count(outcome => !outcome.IsSuccess));

            await using var verifier = new VolleyDraftDbContext(options);
            var durableSession = await verifier.MatchSessions.AsNoTracking()
                .SingleAsync(item => item.Id == seeded.SessionId);
            var rounds = await verifier.DraftRounds.AsNoTracking()
                .Where(item => item.SessionId == seeded.SessionId)
                .ToListAsync();

            Assert.Equal(SessionStatus.Drafting, durableSession.Status);
            Assert.Single(rounds);
        }
    }

    private static async Task<StartOutcome> RunStartAsync(
        VolleyDraftDbContext db,
        string adminId,
        string sessionId,
        Task start,
        int delayMilliseconds)
    {
        try
        {
            await start;
            if (delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds);
            var result = await new SessionDraftService(db).StartDraftAsync(adminId, sessionId);
            return new StartOutcome(result.IsSuccess, result.StatusCode, result.Error, null);
        }
        catch (Exception exception)
        {
            return new StartOutcome(false, 500, exception.Message, exception);
        }
    }

    private static async Task<SeededSession> SeedReadySessionAsync(
        DbContextOptions<VolleyDraftDbContext> options,
        int seed)
    {
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var adminId = $"fuzz-manual-draft-admin-{seed}";
        db.Users.Add(new User
        {
            Id = adminId,
            DisplayName = $"Fuzz Manual Draft Admin {seed}",
            Email = $"fuzz-manual-draft-{seed}@example.test",
            PasswordHash = "test"
        });
        await db.SaveChangesAsync();

        var service = new SessionDraftService(db);
        var created = await service.CreateSessionAsync(adminId, new CreateSessionRequest("T6", 3, 2));
        Assert.True(created.IsSuccess, created.Error);
        var sessionId = created.Value!.Id;
        var playerIds = new List<string>();
        for (var index = 1; index <= 6; index += 1)
        {
            var added = await service.AddPlayerAsync(
                adminId,
                sessionId,
                new AddPlayerRequest($"P{index}", PlayerRole.New, PlayerLevel.New, PlayerGender.Male));
            Assert.True(added.IsSuccess, added.Error);
            playerIds.Add(added.Value!.Id);
        }

        var captains = await service.SetManualCaptainsAsync(
            adminId,
            sessionId,
            new ManualCaptainsRequest(playerIds.Take(3).ToList()));
        Assert.True(captains.IsSuccess, captains.Error);

        return new SeededSession(adminId, sessionId);
    }

    private sealed record SeededSession(string AdminId, string SessionId);

    private sealed record StartOutcome(
        bool IsSuccess,
        int StatusCode,
        string? Error,
        Exception? Exception);
}
