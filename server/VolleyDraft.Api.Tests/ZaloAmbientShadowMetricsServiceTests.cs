using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientShadowMetricsServiceTests
{
    [Fact]
    public async Task Metrics_are_scoped_to_owned_session_group_and_aggregate_rollout_signals()
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
            Email = "ambient-metrics@example.test",
            PasswordHash = "test"
        });
        db.ZaloConnections.Add(new ZaloConnection
        {
            Id = "conn-1",
            AdminUserId = "admin-1",
            AccountZaloId = "bot-uid",
            DisplayName = "Volley Bot",
            EncryptedCredentials = "test"
        });
        db.MatchSessions.Add(new MatchSession
        {
            Id = "session-1",
            Name = "T6",
            AdminUserId = "admin-1",
            ZaloConnectionId = "conn-1",
            ZaloGroupId = "g1",
            BotEnabled = true
        });
        await db.SaveChangesAsync();

        var traces = new ZaloBotTraceStore(db);
        await traces.WriteAsync(new ZaloBotTraceEntry(
            "m1", "g1", "u1", "AmbientShadowWouldReply",
            IntentSource: "AmbientShadow",
            Intent: "MissingSlots",
            Confidence: .90,
            FallbackReason: "kind:Fact|fact_intent|question|session_reference"));
        await traces.WriteAsync(new ZaloBotTraceEntry(
            "m2", "g1", "u2", "AmbientShadowObserve",
            IntentSource: "AmbientShadow",
            Intent: "GeneralChat",
            Confidence: .15,
            FallbackReason: "kind:Social|reply_to_member|busy_group"));
        await traces.WriteAsync(new ZaloBotTraceEntry(
            "m3", "g1", "u3", "AmbientShadowObserve",
            IntentSource: "AmbientShadow",
            Intent: "Redraft",
            Confidence: .20,
            FallbackReason: "kind:Action|action_requires_address"));
        await traces.WriteAsync(new ZaloBotTraceEntry(
            "other-group", "g2", "u4", "AmbientShadowWouldReply",
            IntentSource: "AmbientShadow",
            Intent: "MissingSlots",
            Confidence: 1,
            FallbackReason: "kind:Fact|fact_intent"));
        await traces.WriteAsync(new ZaloBotTraceEntry(
            "explicit", "g1", "u5", "ExplicitMention",
            IntentSource: "Deterministic",
            Intent: "MissingSlots",
            Confidence: 1));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:Enabled"] = "true",
                ["ZaloBot:Ambient:ShadowMode"] = "true",
                ["ZaloBot:Ambient:WouldReplyThreshold"] = "65"
            })
            .Build();
        var service = new ZaloAmbientShadowMetricsService(db, configuration);

        var result = await service.GetForSessionAsync("admin-1", "session-1", 24);

        Assert.True(result.IsSuccess);
        var metrics = Assert.IsType<VolleyDraft.Api.Contracts.ZaloAmbientShadowMetricsResponse>(result.Value);
        Assert.Equal(3, metrics.ObservedCount);
        Assert.Equal(1, metrics.WouldReplyCount);
        Assert.Equal(0.3333, metrics.WouldReplyRate);
        Assert.Equal(41.67, metrics.AverageScore);
        Assert.Equal(1, metrics.HighConfidenceFactCount);
        Assert.Equal(1, metrics.CandidateKinds["Fact"]);
        Assert.Equal(1, metrics.CandidateKinds["Social"]);
        Assert.Equal(1, metrics.CandidateKinds["Action"]);
        Assert.Equal(1, metrics.SuppressionReasons["reply_to_member"]);
        Assert.Equal(1, metrics.SuppressionReasons["busy_group"]);
        Assert.Equal(1, metrics.SuppressionReasons["action_requires_address"]);
        Assert.Equal(65, metrics.WouldReplyThreshold);
        Assert.True(metrics.ShadowMode);
    }

    [Fact]
    public async Task Metrics_do_not_expose_another_admins_session()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new User
        {
            Id = "owner",
            DisplayName = "Owner",
            Email = "ambient-owner@example.test",
            PasswordHash = "test"
        });
        db.MatchSessions.Add(new MatchSession
        {
            Id = "private-session",
            Name = "Private",
            AdminUserId = "owner"
        });
        await db.SaveChangesAsync();

        var service = new ZaloAmbientShadowMetricsService(db, new ConfigurationBuilder().Build());
        var result = await service.GetForSessionAsync("someone-else", "private-session", 24);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }
}
