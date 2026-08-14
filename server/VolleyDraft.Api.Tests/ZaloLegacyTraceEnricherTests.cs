using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloLegacyTraceEnricherTests
{
    [Fact]
    public async Task Projected_terminal_trace_gets_provider_reply_and_pre_routing_person_ids()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new User
        {
            Id = "admin-1",
            DisplayName = "Admin",
            Email = "trace-enrich@example.test",
            PasswordHash = "test"
        });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = "conn-1",
            AdminUserId = "admin-1",
            AccountZaloId = "bot-uid",
            DisplayName = "Bot",
            EncryptedCredentials = "test"
        });
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            MessageId = "incoming-1",
            SenderId = "u-sender",
            SenderName = "Sender",
            Content = "xem Tồ",
            IsFromBot = false,
            SentAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            SelectedIntent = "GetMemberLastActivity",
            AiCalled = false,
            ReplyOutcome = "sent"
        });
        await db.SaveChangesAsync();

        await new ZaloMessageGraphStore(db)
            .RememberOutboundAsync("conn-1", "g1", "provider-reply-1", "incoming-1");
        await new ZaloBotTraceStore(db).WriteAsync(new ZaloBotTraceEntry(
            "incoming-1",
            "g1",
            "u-sender",
            "ExplicitMention",
            IntentSource: "IdentityPreRouting",
            ResolvedPersonIdsJson: "[\"zalo:u-long\"]"));
        await new ZaloLegacyOutcomeTraceProjector(db).ProjectAsync();

        var result = await new ZaloLegacyTraceEnricher(db).EnrichAsync();

        Assert.Equal(1, result.Enriched);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ReplyMessageId", "ResolvedPersonIdsJson"
            FROM "ZaloBotTraces"
            WHERE "MessageId"='incoming-1' AND "IntentSource"='LegacyOutcomeProjection';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("provider-reply-1", reader.GetString(0));
        Assert.Equal("[\"zalo:u-long\"]", reader.GetString(1));
    }
}
