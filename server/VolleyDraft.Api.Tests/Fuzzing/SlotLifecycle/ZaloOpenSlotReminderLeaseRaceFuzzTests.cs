using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloOpenSlotReminderLeaseRaceFuzzTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Concurrent_workers_cannot_both_take_over_one_expired_reminder_lease()
    {
        var connectionString = $"Data Source=slot-reminder-lease-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using (var bootstrap = new VolleyDraftDbContext(options))
            await bootstrap.Database.EnsureCreatedAsync();

        for (var seed = 1; seed <= 128; seed += 1)
        {
            await using var ownerDb = new VolleyDraftDbContext(options);
            await using var workerADb = new VolleyDraftDbContext(options);
            await using var workerBDb = new VolleyDraftDbContext(options);

            var ownerStore = new ZaloOpenSlotOfferStore(ownerDb);
            var workerAStore = new ZaloOpenSlotOfferStore(workerADb);
            var workerBStore = new ZaloOpenSlotOfferStore(workerBDb);
            var baseTime = new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero).AddMinutes(seed * 3);

            var offer = await ownerStore.OpenAsync(
                "connection-a",
                "group-a",
                $"owner-{seed}",
                $"Owner {seed}",
                $"session-{seed}",
                $"Session {seed}",
                $"source-{seed}",
                DateTimeOffset.UtcNow.AddHours(6),
                DateTimeOffset.UtcNow.AddHours(1));

            Assert.True(await ownerStore.TryAcquireReminderLeaseAsync(
                offer,
                $"initial-{seed}",
                baseTime,
                LeaseDuration));

            var takeoverAt = baseTime.Add(LeaseDuration);
            var firstToken = seed % 2 == 0 ? $"worker-a-{seed}" : $"worker-b-{seed}";
            var secondToken = seed % 2 == 0 ? $"worker-b-{seed}" : $"worker-a-{seed}";
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = Task.Run(async () =>
            {
                await start.Task;
                return await workerAStore.TryAcquireReminderLeaseAsync(
                    offer,
                    firstToken,
                    takeoverAt,
                    LeaseDuration);
            });
            var second = Task.Run(async () =>
            {
                await start.Task;
                return await workerBStore.TryAcquireReminderLeaseAsync(
                    offer,
                    secondToken,
                    takeoverAt,
                    LeaseDuration);
            });

            start.SetResult();
            var results = await Task.WhenAll(first, second);
            var winnerCount = results.Count(result => result);

            Assert.True(
                winnerCount == 1,
                $"seed={seed}; fingerprint=slot-lifecycle:reminder-lease-concurrent-takeover; " +
                $"results=[{string.Join(',', results)}]");

            var winnerToken = results[0] ? firstToken : secondToken;
            var loserToken = results[0] ? secondToken : firstToken;

            Assert.False(await ownerStore.TryAcquireReminderLeaseAsync(
                offer,
                loserToken,
                takeoverAt.AddTicks(1),
                LeaseDuration));

            Assert.True(await ownerStore.MarkNudgedAsync(
                offer.Id,
                winnerToken,
                takeoverAt.AddSeconds(1),
                takeoverAt.AddMinutes(20)));

            Assert.False(await ownerStore.MarkNudgedAsync(
                offer.Id,
                loserToken,
                takeoverAt.AddSeconds(1),
                takeoverAt.AddMinutes(20)));
        }
    }
}
