using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloInboundLeaseRestartFuzzTests
{
    [Fact]
    public async Task Fresh_restart_shaped_retry_cannot_steal_an_active_ingress_lease()
    {
        for (var seed = 1; seed <= 64; seed += 1)
        {
            var connectionString = $"Data Source=inbound-active-lease-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await SeedTrackedGroupAsync(options, seed);
            var incoming = Incoming($"active-{seed}", seed);

            await using (var firstDb = new VolleyDraftDbContext(options))
            {
                var first = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                    firstDb,
                    NullLogger<ZaloInboundCoordinator>.Instance,
                    incoming);
                Assert.True(first.IsTracked && !first.IsDuplicate);

                await firstDb.ZaloGroupMessages
                    .Where(message => message.MessageId == incoming.MessageId)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(message => message.ProcessingStartedAt, DateTimeOffset.UtcNow.AddSeconds(-119)));
            }

            await using var retryDb = new VolleyDraftDbContext(options);
            var retry = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                retryDb,
                NullLogger<ZaloInboundCoordinator>.Instance,
                incoming with { Content = incoming.Content + " retry" });

            Assert.True(
                retry.IsDuplicate,
                $"seed={seed}; fingerprint=idempotency:active-ingress-lease-stolen-after-restart");
        }
    }

    [Fact]
    public async Task Expired_restart_shaped_lease_is_reclaimed_by_exactly_one_competing_instance()
    {
        for (var seed = 1; seed <= 48; seed += 1)
        {
            var connectionString = $"Data Source=inbound-expired-lease-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await SeedTrackedGroupAsync(options, seed + 1000);
            var incoming = Incoming($"expired-{seed}", seed);

            await using (var firstDb = new VolleyDraftDbContext(options))
            {
                var first = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                    firstDb,
                    NullLogger<ZaloInboundCoordinator>.Instance,
                    incoming);
                Assert.True(first.IsTracked && !first.IsDuplicate);

                await firstDb.ZaloGroupMessages
                    .Where(message => message.MessageId == incoming.MessageId)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(message => message.ProcessingStartedAt, DateTimeOffset.UtcNow.AddSeconds(-121)));
            }

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 3)
                .Select(worker => Task.Run(async () =>
                {
                    await using var db = new VolleyDraftDbContext(options);
                    await start.Task;
                    try
                    {
                        return await ZaloInboundCoordinator.TryClaimTrackedAsync(
                            db,
                            NullLogger<ZaloInboundCoordinator>.Instance,
                            incoming with { SenderName = $"retry-{worker}" });
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
                    {
                        await Task.Delay((seed + worker) % 4 + 1);
                        await using var retryDb = new VolleyDraftDbContext(options);
                        return await ZaloInboundCoordinator.TryClaimTrackedAsync(
                            retryDb,
                            NullLogger<ZaloInboundCoordinator>.Instance,
                            incoming with { SenderName = $"retry-{worker}" });
                    }
                }))
                .ToArray();

            start.SetResult();
            var results = await Task.WhenAll(attempts);
            var winners = results.Count(result => result.IsTracked && !result.IsDuplicate);
            var duplicates = results.Count(result => result.IsDuplicate);

            Assert.True(
                winners == 1 && duplicates == 2,
                $"seed={seed}; fingerprint=idempotency:expired-ingress-lease-multiwinner; winners={winners}; duplicates={duplicates}");

            await using var verifier = new VolleyDraftDbContext(options);
            var row = await verifier.ZaloGroupMessages
                .AsNoTracking()
                .SingleAsync(message => message.MessageId == incoming.MessageId);
            Assert.Equal("ingress_processing", row.ReplyOutcome);
            Assert.False(string.IsNullOrWhiteSpace(row.ProcessingToken));
            Assert.True(row.ProcessingStartedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        }
    }

    [Fact]
    public async Task Terminal_outcomes_never_resurrect_even_when_old_processing_timestamp_is_stale()
    {
        var outcomes = new[] { "throttled", "no_reply", "pre_route_handled" };

        for (var seed = 1; seed <= 48; seed += 1)
        {
            var connectionString = $"Data Source=inbound-terminal-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await SeedTrackedGroupAsync(options, seed + 2000);
            var incoming = Incoming($"terminal-{seed}", seed);
            var terminalOutcome = outcomes[seed % outcomes.Length];

            await using (var firstDb = new VolleyDraftDbContext(options))
            {
                var first = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                    firstDb,
                    NullLogger<ZaloInboundCoordinator>.Instance,
                    incoming);
                Assert.True(first.IsTracked && !first.IsDuplicate);

                await firstDb.ZaloGroupMessages
                    .Where(message => message.MessageId == incoming.MessageId)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(message => message.ReplyOutcome, terminalOutcome)
                        .SetProperty(message => message.ProcessingStartedAt, DateTimeOffset.UtcNow.AddHours(-1))
                        .SetProperty(message => message.ProcessingToken, "stale-token"));
            }

            await using var retryDb = new VolleyDraftDbContext(options);
            var retry = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                retryDb,
                NullLogger<ZaloInboundCoordinator>.Instance,
                incoming with { Content = incoming.Content + " delayed duplicate" });

            Assert.True(
                retry.IsDuplicate,
                $"seed={seed}; fingerprint=idempotency:terminal-ingress-outcome-resurrected; outcome={terminalOutcome}");

            var row = await retryDb.ZaloGroupMessages
                .AsNoTracking()
                .SingleAsync(message => message.MessageId == incoming.MessageId);
            Assert.Equal(terminalOutcome, row.ReplyOutcome);
            Assert.Equal("stale-token", row.ProcessingToken);
        }
    }

    private static ZaloIncomingMessageEvent Incoming(string messageId, int seed) =>
        new(
            "bot-account",
            "bot-account",
            "g1",
            messageId,
            $"u-{seed}",
            $"u-{seed}",
            seed % 2 == 0 ? "@Npc 9" : "claim slot",
            [],
            seed % 2 == 0,
            new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero).AddSeconds(seed).ToUnixTimeMilliseconds());

    private static async Task SeedTrackedGroupAsync(
        DbContextOptions<VolleyDraftDbContext> options,
        int seed)
    {
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin",
            DisplayName = "Admin",
            Email = $"inbound-lease-{seed}@example.test",
            PasswordHash = "x"
        };
        var connection = new ZaloConnection
        {
            Id = "conn",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "bot-account",
            DisplayName = "Npc",
            EncryptedCredentials = "x",
            Status = ZaloConnectionStatus.Connected
        };
        db.AddRange(admin, connection);
        await db.SaveChangesAsync();

        await new ZaloAutoSessionStore(db).EnsureAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloTrackedGroups" (
                "Id", "AdminUserId", "ZaloConnectionId", "GroupId", "GroupName", "AutoSessionEnabled", "CreatedAt", "UpdatedAt")
            VALUES (
                {{Guid.NewGuid().ToString("n")}}, {{admin.Id}}, {{connection.Id}}, {{"g1"}}, {{"g1"}}, {{1}}, {{now}}, {{now}});
            """);
        db.ChangeTracker.Clear();
    }
}
