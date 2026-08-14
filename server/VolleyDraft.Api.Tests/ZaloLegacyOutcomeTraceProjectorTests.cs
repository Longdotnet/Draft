using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloLegacyOutcomeTraceProjectorTests
{
    [Fact]
    public async Task Terminal_legacy_outcome_is_projected_once_into_v2_trace_schema()
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
            Email = "trace-projector@example.test",
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
        var started = DateTimeOffset.UtcNow.AddMilliseconds(-25);
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            MessageId = "incoming-1",
            SenderId = "u1",
            SenderName = "Long",
            Content = "T6 còn slot không?",
            IsFromBot = false,
            SentAt = started,
            ReceivedAt = started,
            ProcessingStartedAt = started,
            BotReplySentAt = started.AddMilliseconds(25),
            SelectedIntent = "MissingSlots",
            AiCalled = false,
            ReplyOutcome = "sent"
        });
        await db.SaveChangesAsync();

        var projector = new ZaloLegacyOutcomeTraceProjector(db);
        var first = await projector.ProjectAsync();
        var second = await projector.ProjectAsync();

        Assert.Equal(1, first.Projected);
        Assert.Equal(0, second.Projected);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"IntentSource\", \"Intent\", \"FallbackReason\", \"TotalLatencyMs\" FROM \"ZaloBotTraces\" WHERE \"MessageId\"='incoming-1';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("LegacyOutcomeProjection", reader.GetString(0));
        Assert.Equal("MissingSlots", reader.GetString(1));
        Assert.Equal("sent", reader.GetString(2));
        Assert.Equal(25L, Convert.ToInt64(reader.GetValue(3)));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Processing_rows_are_not_projected_until_terminal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new User { Id = "admin-1", DisplayName = "Admin", Email = "trace-processing@example.test", PasswordHash = "test" });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = "conn-1", AdminUserId = "admin-1", AccountZaloId = "bot", DisplayName = "Bot", EncryptedCredentials = "test"
        });
        db.ZaloGroupMessages.Add(new ZaloGroupMessage
        {
            ZaloConnectionId = "conn-1",
            GroupId = "g1",
            MessageId = "incoming-processing",
            SenderId = "u1",
            SenderName = "Long",
            Content = "hello",
            IsFromBot = false,
            SentAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            ReplyOutcome = "processing"
        });
        await db.SaveChangesAsync();

        var result = await new ZaloLegacyOutcomeTraceProjector(db).ProjectAsync();
        Assert.Equal(0, result.Projected);
    }
}
