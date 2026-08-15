using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDomainEventNarrationTelemetryTests
{
    [Theory]
    [InlineData(true, true, "sent", "DomainEventNarratorSent", "status:Sent")]
    [InlineData(true, false, "global_shadow_mode", "DomainEventNarratorSuppressed", "status:Suppressed")]
    [InlineData(false, false, "event_not_narratable", "DomainEventNarratorNotEligible", "status:NotEligible")]
    public async Task Delivery_outcome_is_persisted_as_metadata_only_trace(
        bool eligible,
        bool sent,
        string reason,
        string expectedAddressReason,
        string expectedStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var decision = new ZaloDomainEventShadowDecision("RosterFilled", 17, 18, 18, "shadow-trace-1");
        var narration = new ZaloDomainEventNarratorResult(
            eligible,
            sent,
            "THIS USER VISIBLE MESSAGE MUST NOT BE STORED",
            reason);

        await new ZaloDomainEventNarrationTelemetry(db).RecordAsync(
            "group-1",
            "session-1",
            decision,
            narration);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "IntentSource", "Intent", "AddressReason", "FallbackReason"
            FROM "ZaloBotTraces";
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("AmbientDomainEventNarrator", reader.GetString(0));
        Assert.Equal("RosterFilled", reader.GetString(1));
        Assert.Equal(expectedAddressReason, reader.GetString(2));
        var metadata = reader.GetString(3);
        Assert.Contains(expectedStatus, metadata);
        Assert.Contains($"reason:{reason}", metadata);
        Assert.Contains("before:17|after:18|capacity:18", metadata);
        Assert.DoesNotContain("THIS USER VISIBLE MESSAGE", metadata, StringComparison.Ordinal);
        Assert.False(await reader.ReadAsync());
    }
}
