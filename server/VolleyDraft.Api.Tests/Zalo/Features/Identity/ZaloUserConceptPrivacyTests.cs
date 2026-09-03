using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloUserConceptPrivacyTests
{
    [Fact]
    public async Task Forget_key_deletes_active_and_superseded_history_for_only_that_key()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloUserConceptStore(db);
        var sender = new ZaloAiSender("u1", "Long");

        await store.RememberAsync("g1", sender,
            new ZaloUserConceptDraft("DomainFact", "volleyball_role", JsonSerializer.Serialize(new { role = "Libero" })));
        await store.RememberAsync("g1", sender,
            new ZaloUserConceptDraft("DomainFact", "volleyball_role", JsonSerializer.Serialize(new { role = "Setter" })));
        await store.RememberAsync("g1", sender,
            new ZaloUserConceptDraft("Alias", "preferred_name", JsonSerializer.Serialize(new { name = "Tồ" })));

        var service = new ZaloMemoryV2Service(db);
        var incoming = new ZaloIncomingMessageEvent(
            "bot", "bot", "g1", "m3", "u1", "Long", "bot quên vị trí của tui", [], true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var result = await service.ProcessAsync("g1", incoming, incoming.Content);

        Assert.True(result.Handled);
        Assert.Contains("xóa cả lịch sử", result.Response);
        var remaining = await store.LoadActiveAsync("g1", "u1", 50);
        Assert.Single(remaining);
        Assert.Equal("preferred_name", remaining[0].Key);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"ZaloUserConcepts\" WHERE \"GroupId\"='g1' AND \"SubjectZaloUserId\"='u1' AND \"ConceptKey\"='volleyball_role';";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Forget_all_is_scoped_to_requesting_user_and_group()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ZaloUserConceptStore(db);
        var draft = new ZaloUserConceptDraft("DomainFact", "volleyball_role", JsonSerializer.Serialize(new { role = "Libero" }));
        await store.RememberAsync("g1", new ZaloAiSender("u1", "Long"), draft);
        await store.RememberAsync("g1", new ZaloAiSender("u2", "Tùng"), draft);
        await store.RememberAsync("g2", new ZaloAiSender("u1", "Long"), draft);

        var incoming = new ZaloIncomingMessageEvent(
            "bot", "bot", "g1", "m4", "u1", "Long", "bot xóa hết memory của tui", [], true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await new ZaloMemoryV2Service(db).ProcessAsync("g1", incoming, incoming.Content);

        Assert.Empty(await store.LoadActiveAsync("g1", "u1"));
        Assert.Single(await store.LoadActiveAsync("g1", "u2"));
        Assert.Single(await store.LoadActiveAsync("g2", "u1"));
    }
}
