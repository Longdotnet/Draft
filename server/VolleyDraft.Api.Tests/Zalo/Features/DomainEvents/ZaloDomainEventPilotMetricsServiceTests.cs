using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloDomainEventPilotMetricsServiceTests
{
    [Fact]
    public async Task Metrics_are_scoped_to_admin_session_and_aggregate_metadata_only_telemetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var telemetry = new ZaloDomainEventNarrationTelemetry(fixture.Db);

        for (var index = 0; index < 6; index++)
        {
            await telemetry.RecordAsync(
                "group-1",
                "session-1",
                new ZaloDomainEventShadowDecision("RosterFilled", 17, 18, 18, $"filled-{index}"),
                new ZaloDomainEventNarratorResult(true, false, "not persisted", "global_shadow_mode"));
        }
        for (var index = 0; index < 4; index++)
        {
            await telemetry.RecordAsync(
                "group-1",
                "session-1",
                new ZaloDomainEventShadowDecision("RosterIncreased", index, index + 1, 18, $"increase-{index}"),
                new ZaloDomainEventNarratorResult(false, false, "not persisted", "event_not_narratable"));
        }
        await telemetry.RecordAsync(
            "group-other",
            "session-other",
            new ZaloDomainEventShadowDecision("RosterFilled", 17, 18, 18, "other"),
            new ZaloDomainEventNarratorResult(true, false, null, "global_shadow_mode"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:ShadowMode"] = "true",
                ["ZaloBot:Ambient:DomainEventPilot:Enabled"] = "false",
                ["ZaloBot:Ambient:DomainEventPilot:SendEnabled"] = "false"
            })
            .Build();
        var result = await new ZaloDomainEventPilotMetricsService(fixture.Db, configuration)
            .GetForSessionAsync("admin-1", "session-1", 168);

        Assert.True(result.IsSuccess, result.Error);
        Assert.NotNull(result.Value);
        var metrics = result.Value!;
        Assert.Equal(10, metrics.ObservedDecisionCount);
        Assert.Equal(6, metrics.NarratableCount);
        Assert.Equal(0, metrics.SentCount);
        Assert.Equal(6, metrics.SuppressedCount);
        Assert.Equal(4, metrics.NotEligibleCount);
        Assert.Equal(6, metrics.EventKinds["RosterFilled"]);
        Assert.Equal(4, metrics.EventKinds["RosterIncreased"]);
        Assert.Equal(6, metrics.SuppressionReasons["global_shadow_mode"]);
        Assert.True(metrics.ReadyForLiveReview);
        Assert.Empty(metrics.ReadinessBlockers);
        Assert.False(metrics.PilotEnabled);
        Assert.False(metrics.SendEnabled);
        Assert.True(metrics.GlobalShadowMode);
    }

    [Fact]
    public async Task Wrong_admin_cannot_read_session_metrics()
    {
        await using var fixture = await Fixture.CreateAsync();
        var configuration = new ConfigurationBuilder().Build();

        var result = await new ZaloDomainEventPilotMetricsService(fixture.Db, configuration)
            .GetForSessionAsync("admin-other", "session-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public void Readiness_policy_blocks_sparse_or_already_live_samples()
    {
        var sparse = ZaloDomainEventPilotMetricsService.BuildReadinessBlockers(
            observedCount: 2,
            narratableCount: 1,
            sentCount: 0,
            suppressedCount: 0);
        Assert.Equal(3, sparse.Count);

        var live = ZaloDomainEventPilotMetricsService.BuildReadinessBlockers(
            observedCount: 20,
            narratableCount: 8,
            sentCount: 1,
            suppressedCount: 7);
        Assert.Single(live);
        Assert.Contains("outbound send", live[0], StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"domain-readiness-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            db.MatchSessions.AddRange(
                new MatchSession
                {
                    Id = "session-1",
                    AdminUserId = admin.Id,
                    AdminUser = admin,
                    Name = "T6",
                    ZaloGroupId = "group-1",
                    Status = SessionStatus.Setup,
                    BotEnabled = true
                },
                new MatchSession
                {
                    Id = "session-other",
                    AdminUserId = admin.Id,
                    Name = "CN",
                    ZaloGroupId = "group-other",
                    Status = SessionStatus.Setup,
                    BotEnabled = true
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
