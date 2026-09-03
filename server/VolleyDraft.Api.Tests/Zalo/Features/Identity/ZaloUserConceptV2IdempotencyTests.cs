using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloUserConceptV2IdempotencyTests
{
    [Fact]
    public async Task Repeating_same_concept_confirms_existing_row_instead_of_superseding_it()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloUserConceptStore(db);
        var sender = new ZaloAiSender("u1", "Long");
        var draft = new ZaloUserConceptDraft(
            "Preference",
            "session_availability",
            JsonSerializer.Serialize(new { sessions = new[] { "T6" }, mode = "prefer" }),
            .98);

        var first = await store.RememberAsync("g1", sender, draft, "m1");
        var second = await store.RememberAsync("g1", sender, draft, "m1");
        var active = await store.LoadActiveAsync("g1", "u1");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(active);
        Assert.Null(second.SupersedesConceptId);
        Assert.Equal("m1", second.SourceMessageId);
    }

    [Fact]
    public async Task Different_value_still_supersedes_previous_concept()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloUserConceptStore(db);
        var sender = new ZaloAiSender("u1", "Long");

        var first = await store.RememberAsync("g1", sender,
            new ZaloUserConceptDraft("DomainFact", "volleyball_role", JsonSerializer.Serialize(new { role = "Libero" })));
        var second = await store.RememberAsync("g1", sender,
            new ZaloUserConceptDraft("DomainFact", "volleyball_role", JsonSerializer.Serialize(new { role = "Setter" })));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id, second.SupersedesConceptId);
        Assert.Equal("Setter", JsonDocument.Parse(second.ValueJson).RootElement.GetProperty("role").GetString());
    }
}
