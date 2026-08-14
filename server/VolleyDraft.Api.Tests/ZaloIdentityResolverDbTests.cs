using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloIdentityResolverDbTests
{
    [Fact]
    public async Task Resolver_uses_current_group_member_uid_and_preferred_alias()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.ZaloGroupMembers.Add(new ZaloGroupMember
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            ZaloUserId = "u-long",
            DisplayName = "Long Nguyễn",
            IsCurrentMember = true
        });
        db.PlayerProfiles.Add(new PlayerProfile
        {
            Id = "p-long",
            ZaloUserId = "u-long",
            DisplayName = "Long Nguyễn"
        });
        await db.SaveChangesAsync();
        var conceptStore = new ZaloUserConceptStore(db);
        await conceptStore.RememberAsync(
            "g1",
            new ZaloAiSender("u-long", "Long Nguyễn"),
            new ZaloUserConceptDraft("Alias", "preferred_name", JsonSerializer.Serialize(new { name = "Tồ" })));

        var resolver = new ZaloIdentityResolver(db);
        var result = await resolver.ResolveAsync("g1", "Tồ");

        Assert.Equal(ZaloIdentityResolutionStatus.Resolved, result.Status);
        Assert.Equal("u-long", result.ZaloUserId);
        Assert.Equal("p-long", result.PlayerProfileId);
        Assert.Equal("zalo:u-long", result.PersonKey);
    }
}
