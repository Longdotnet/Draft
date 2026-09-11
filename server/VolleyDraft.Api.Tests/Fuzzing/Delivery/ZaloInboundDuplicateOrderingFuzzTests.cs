using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloInboundDuplicateOrderingFuzzTests
{
    [Fact]
    public async Task Duplicate_and_out_of_order_delivery_preserves_one_durable_row_and_one_active_claim_per_message()
    {
        for (var seed = 1; seed <= 96; seed += 1)
        {
            var connectionString = $"Data Source=inbound-ordering-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await SeedTrackedGroupAsync(options, seed);

            var baseTime = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero).AddMinutes(seed);
            var older = Incoming($"m-old-{seed}", "u1", "older", baseTime);
            var newer = Incoming($"m-new-{seed}", "u2", "newer", baseTime.AddMinutes(1));
            var delivery = seed % 2 == 0 ? new[] { newer, older } : new[] { older, newer };

            foreach (var message in delivery)
            {
                await using var firstDb = new VolleyDraftDbContext(options);
                var first = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                    firstDb,
                    NullLogger<ZaloInboundCoordinator>.Instance,
                    message);

                Assert.True(
                    first.IsTracked && !first.IsDuplicate,
                    $"seed={seed}; fingerprint=idempotency:inbound-first-delivery-not-claimable; message={message.MessageId}");

                await using var duplicateDb = new VolleyDraftDbContext(options);
                var duplicate = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                    duplicateDb,
                    NullLogger<ZaloInboundCoordinator>.Instance,
                    message with
                    {
                        Content = message.Content + " duplicate-mutated-payload",
                        SenderName = "mutated",
                        SentAtUnixMs = message.SentAtUnixMs + 1234
                    });

                Assert.True(
                    duplicate.IsDuplicate,
                    $"seed={seed}; fingerprint=idempotency:inbound-active-lease-accepted-duplicate; message={message.MessageId}");
            }

            await using var verifier = new VolleyDraftDbContext(options);
            var rows = await verifier.ZaloGroupMessages
                .AsNoTracking()
                .Where(message => message.ZaloConnectionId == "conn" && message.GroupId == "g1")
                .OrderBy(message => message.MessageId)
                .ToListAsync();

            Assert.True(
                rows.Count == 2,
                $"seed={seed}; fingerprint=idempotency:inbound-delivery-created-extra-row; count={rows.Count}");
            Assert.All(rows, row => Assert.Equal("ingress_processing", row.ReplyOutcome));
            Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.ProcessingToken)));

            var oldRow = Assert.Single(rows.Where(row => row.MessageId == older.MessageId));
            var newRow = Assert.Single(rows.Where(row => row.MessageId == newer.MessageId));
            Assert.Equal("older", oldRow.Content);
            Assert.Equal("newer", newRow.Content);
            Assert.Equal("u1", oldRow.SenderId);
            Assert.Equal("u2", newRow.SenderId);
            Assert.Equal(baseTime.ToUnixTimeMilliseconds(), oldRow.SentAt.ToUnixTimeMilliseconds());
            Assert.Equal(baseTime.AddMinutes(1).ToUnixTimeMilliseconds(), newRow.SentAt.ToUnixTimeMilliseconds());
        }
    }

    [Fact]
    public async Task Concurrent_duplicate_claims_have_exactly_one_winner()
    {
        for (var seed = 1; seed <= 64; seed += 1)
        {
            var connectionString = $"Data Source=inbound-duplicate-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await SeedTrackedGroupAsync(options, seed + 1000);
            var incoming = Incoming(
                $"m-race-{seed}",
                $"u-{seed}",
                "claim slot",
                new DateTimeOffset(2026, 9, 11, 2, 0, 0, TimeSpan.Zero).AddSeconds(seed));

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claims = Enumerable.Range(0, 3)
                .Select(worker => Task.Run(async () =>
                {
                    await using var db = new VolleyDraftDbContext(options);
                    await start.Task;
                    try
                    {
                        return await ZaloInboundCoordinator.TryClaimTrackedAsync(
                            db,
                            NullLogger<ZaloInboundCoordinator>.Instance,
                            incoming);
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
                    {
                        // SQLite's coarse writer lock is a test-environment artifact; retry as a
                        // fresh production-shaped request rather than counting it as a domain failure.
                        await Task.Delay((seed + worker) % 4 + 1);
                        await using var retryDb = new VolleyDraftDbContext(options);
                        return await ZaloInboundCoordinator.TryClaimTrackedAsync(
                            retryDb,
                            NullLogger<ZaloInboundCoordinator>.Instance,
                            incoming);
                    }
                }))
                .ToArray();

            start.SetResult();
            var results = await Task.WhenAll(claims);
            var winners = results.Count(result => result.IsTracked && !result.IsDuplicate);
            var duplicates = results.Count(result => result.IsDuplicate);

            Assert.True(
                winners == 1 && duplicates == 2,
                $"seed={seed}; fingerprint=idempotency:inbound-concurrent-duplicate-multiwinner; winners={winners}; duplicates={duplicates}");

            await using var verifier = new VolleyDraftDbContext(options);
            var rows = await verifier.ZaloGroupMessages
                .AsNoTracking()
                .Where(message => message.ZaloConnectionId == "conn" && message.MessageId == incoming.MessageId)
                .ToListAsync();
            Assert.Single(rows);
            Assert.False(string.IsNullOrWhiteSpace(rows[0].ProcessingToken));
        }
    }

    private static ZaloIncomingMessageEvent Incoming(
        string messageId,
        string senderId,
        string content,
        DateTimeOffset sentAt) =>
        new(
            "bot-account",
            "bot-account",
            "g1",
            messageId,
            senderId,
            senderId,
            content,
            [],
            false,
            sentAt.ToUnixTimeMilliseconds());

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
            Email = $"inbound-fuzz-{seed}@example.test",
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
