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
            var connectionString = CreateConnectionString("manual-draft-race");
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = CreateOptions(connectionString);

            var seeded = await SeedReadySessionAsync(options, seed);
            await using var primary = new VolleyDraftDbContext(options);
            await using var secondary = new VolleyDraftDbContext(options);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = RunStartAsync(primary, seeded.AdminId, seeded.SessionId, start.Task, seed % 3);
            var second = RunStartAsync(secondary, seeded.AdminId, seeded.SessionId, start.Task, (seed + 1) % 3);

            start.SetResult();
            var outcomes = await Task.WhenAll(first, second);

            AssertSingleWinnerWithoutExceptions(outcomes);
            await AssertDurableSingleDraftAsync(options, seeded.SessionId);
        }
    }

    [Fact]
    public async Task Three_way_start_race_then_restart_retry_preserves_single_logical_mutation()
    {
        // Exercise more interleavings than the historical two-caller race and then retry
        // through a fresh DbContext to model a client retry after an uncertain response or deploy.
        // The authoritative invariant is one Ready -> Drafting transition and one draft round.
        for (var seed = 1; seed <= 48; seed += 1)
        {
            var connectionString = CreateConnectionString("manual-draft-retry-race");
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = CreateOptions(connectionString);
            var seeded = await SeedReadySessionAsync(options, 10_000 + seed);

            await using var firstDb = new VolleyDraftDbContext(options);
            await using var secondDb = new VolleyDraftDbContext(options);
            await using var thirdDb = new VolleyDraftDbContext(options);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var delays = new[]
            {
                seed % 5,
                (seed * 3) % 5,
                (seed * 7) % 5
            };
            var outcomes = await ReleaseTogetherAsync(
                start,
                RunStartAsync(firstDb, seeded.AdminId, seeded.SessionId, start.Task, delays[0]),
                RunStartAsync(secondDb, seeded.AdminId, seeded.SessionId, start.Task, delays[1]),
                RunStartAsync(thirdDb, seeded.AdminId, seeded.SessionId, start.Task, delays[2]));

            AssertSingleWinnerWithoutExceptions(outcomes);
            await AssertDurableSingleDraftAsync(options, seeded.SessionId);

            // Fresh process-shaped retry: if the winning HTTP response was lost, retrying the
            // command must not produce another logical mutation or another DraftRound.
            await using var retryDb = new VolleyDraftDbContext(options);
            var retry = await new SessionDraftService(retryDb)
                .StartDraftAsync(seeded.AdminId, seeded.SessionId);
            Assert.False(retry.IsSuccess);
            await AssertDurableSingleDraftAsync(options, seeded.SessionId);
        }
    }

    private static async Task<StartOutcome[]> ReleaseTogetherAsync(
        TaskCompletionSource start,
        params Task<StartOutcome>[] operations)
    {
        start.SetResult();
        return await Task.WhenAll(operations);
    }

    private static void AssertSingleWinnerWithoutExceptions(IReadOnlyCollection<StartOutcome> outcomes)
    {
        Assert.All(outcomes, outcome => Assert.Null(outcome.Exception));
        Assert.Equal(1, outcomes.Count(outcome => outcome.IsSuccess));
        Assert.Equal(outcomes.Count - 1, outcomes.Count(outcome => !outcome.IsSuccess));
    }

    private static async Task AssertDurableSingleDraftAsync(
        DbContextOptions<VolleyDraftDbContext> options,
        string sessionId)
    {
        await using var verifier = new VolleyDraftDbContext(options);
        var durableSession = await verifier.MatchSessions.AsNoTracking()
            .SingleAsync(item => item.Id == sessionId);
        var rounds = await verifier.DraftRounds.AsNoTracking()
            .Where(item => item.SessionId == sessionId)
            .ToListAsync();

        Assert.Equal(SessionStatus.Drafting, durableSession.Status);
        Assert.Single(rounds);
    }

    private static string CreateConnectionString(string prefix) =>
        $"Data Source={prefix}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";

    private static DbContextOptions<VolleyDraftDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connectionString)
            .Options;

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
