using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing.Targets;

public sealed class ZaloSchedulerLateRenewalReleaseFenceFuzzTests
{
    [Fact]
    public async Task Released_scheduler_authority_must_not_be_resurrected_by_late_renewal()
    {
        const int seedCount = 64;

        for (var seed = 1; seed <= seedCount; seed++)
        {
            var random = new StableFuzzRandom(seed * 196613);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;

            var acquiredAt = new DateTimeOffset(2026, 9, 12, 5, 0, 0, TimeSpan.Zero)
                .AddMilliseconds(random.NextInt(1000));
            var leaseDuration = TimeSpan.FromSeconds(30 + random.NextInt(91));
            var owner = $"late-renew-owner-{seed}";
            var renewalStartedAt = acquiredAt.AddSeconds(1 + random.NextInt(5));
            var releasedAt = renewalStartedAt.AddMilliseconds(1 + random.NextInt(500));

            await using (var ownerDb = new VolleyDraftDbContext(options))
            {
                var store = new ZaloSchedulerLeaseStore(ownerDb);
                Assert.True(await store.TryAcquireAsync(owner, acquiredAt, leaseDuration));
                await store.MarkAttemptAsync(owner, acquiredAt.AddMilliseconds(1));
                Assert.True(await store.ReleaseAsync(owner, releasedAt));
            }

            // Model an in-flight renewal that captured its authoritative timestamp before timeout/
            // release but reaches persistence afterwards because the provider/DB boundary ignored
            // cancellation. A terminal release must fence that stale authority permanently.
            await using (var staleRenewalDb = new VolleyDraftDbContext(options))
            {
                var staleRenewalStore = new ZaloSchedulerLeaseStore(staleRenewalDb);
                Assert.False(await staleRenewalStore.TryRenewAsync(
                    owner,
                    renewalStartedAt,
                    leaseDuration));
            }

            await using (var verifyDb = new VolleyDraftDbContext(options))
            {
                var snapshot = Assert.IsType<ZaloSchedulerLeaseSnapshot>(
                    await new ZaloSchedulerLeaseStore(verifyDb).GetAsync());

                Assert.NotEqual(owner, snapshot.OwnerId);
                Assert.StartsWith("released:", snapshot.OwnerId, StringComparison.Ordinal);
                Assert.Equal(releasedAt, snapshot.LeaseUntil);
                Assert.True(snapshot.LeaseUntil <= releasedAt);
            }
        }
    }
}
