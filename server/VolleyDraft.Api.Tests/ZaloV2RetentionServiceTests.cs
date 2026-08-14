using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloV2RetentionServiceTests
{
    [Fact]
    public async Task Cleanup_removes_old_trace_relation_and_expired_concept_without_business_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await new ZaloBotTraceStore(db).WriteAsync(new ZaloBotTraceEntry(
            "m1", "g1", "u1", "ExplicitMention"));
        await new ZaloMessageGraphStore(db).RememberOutboundAsync(
            "conn1", "g1", "provider1", "m1");
        await new ZaloUserConceptStore(db).RememberAsync(
            "g1",
            new ZaloAiSender("u1", "Long"),
            new ZaloUserConceptDraft(
                "Preference",
                "session_availability",
                JsonSerializer.Serialize(new { sessions = new[] { "T6" }, mode = "prefer" }),
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5)));

        // Advance the logical retention clock rather than sleeping or mutating timestamps.
        var future = DateTimeOffset.UtcNow.AddDays(200);
        var policy = new ZaloRetentionPolicy(
            TraceRetention: TimeSpan.FromDays(30),
            MessageRelationRetention: TimeSpan.FromDays(90),
            ActiveUserConceptRetention: null);
        var result = await new ZaloV2RetentionService(db).CleanupAsync(policy, future);

        Assert.Equal(1, result.DeletedTraces);
        Assert.Equal(1, result.DeletedMessageRelations);
        Assert.Equal(1, result.DeletedUserConcepts);
        Assert.Empty(await new ZaloUserConceptStore(db).LoadActiveAsync("g1", "u1"));
    }
}
