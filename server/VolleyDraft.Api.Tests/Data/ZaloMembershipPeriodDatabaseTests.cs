using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using Xunit;

namespace VolleyDraft.Api.Tests.Data;

public sealed class ZaloMembershipPeriodDatabaseTests
{
    [Fact]
    public async Task Database_rejects_two_current_periods_but_accepts_a_new_period_after_leave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setup = new VolleyDraftDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(setup);
            setup.Users.Add(new User { Id = "admin", Email = "admin@membership.test" });
            setup.ZaloConnections.Add(new ZaloConnection
            {
                Id = "connection",
                AdminUserId = "admin",
                AccountZaloId = "bot"
            });
            setup.ZaloGroupMembershipPeriods.Add(NewPeriod("first"));
            await setup.SaveChangesAsync();
        }

        // A second writer must not be able to publish another simultaneous current period.
        await using (var competing = new VolleyDraftDbContext(options))
        {
            competing.ZaloGroupMembershipPeriods.Add(NewPeriod("duplicate"));
            await Assert.ThrowsAsync<DbUpdateException>(() => competing.SaveChangesAsync());
        }

        // Leaving closes the old period; a verified rejoin is a new legitimate period.
        await using (var rejoined = new VolleyDraftDbContext(options))
        {
            var first = await rejoined.ZaloGroupMembershipPeriods.SingleAsync();
            first.IsCurrentPeriod = false;
            first.LeftAt = new DateTimeOffset(2026, 9, 23, 18, 0, 0, TimeSpan.Zero);
            await rejoined.SaveChangesAsync();
            rejoined.ZaloGroupMembershipPeriods.Add(NewPeriod("rejoined"));
            await rejoined.SaveChangesAsync();
            Assert.Equal(2, await rejoined.ZaloGroupMembershipPeriods.CountAsync());
            Assert.Equal(1, await rejoined.ZaloGroupMembershipPeriods.CountAsync(p => p.IsCurrentPeriod));
        }
    }

    private static ZaloGroupMembershipPeriod NewPeriod(string id) => new()
    {
        Id = id,
        ZaloConnectionId = "connection",
        GroupId = "group",
        ZaloUserId = "member",
        JoinedAt = new DateTimeOffset(2026, 9, 23, 17, 0, 0, TimeSpan.Zero),
        EvidenceKind = ZaloMembershipEvidenceKind.ProviderJoinEvent,
        SourceEventId = id
    };
}
