using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloOutboundPartialFailureRecoveryFuzzTests
{
    [Fact]
    public async Task Provider_receipt_survives_restart_shaped_retry_and_prevents_second_authoritative_claim()
    {
        for (var seed = 1; seed <= 64; seed += 1)
        {
            var connectionString = $"Data Source=outbound-partial-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await SeedTrackedGroupAsync(options, seed);
            var incoming = Incoming(
                $"m-partial-{seed}",
                $"u-{seed}",
                seed % 2 == 0 ? "@Npc 9" : "claim slot",
                new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero).AddSeconds(seed));

            // First delivery owns the authoritative ingress row. The scenario then
            // injects the real partial-failure boundary: provider accepted the reply
            // and its independent receipt committed, but ZaloGroupMessage finalization
            // never completed before the process/request died.
            await using (var firstDb = new VolleyDraftDbContext(options))
            {
                var first = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                    firstDb,
                    NullLogger<ZaloInboundCoordinator>.Instance,
                    incoming);
                Assert.True(
                    first.IsTracked && !first.IsDuplicate,
                    $"seed={seed}; fingerprint=idempotency:partial-failure-first-claim-missing");

                await new ZaloOutboundReceiptStore(firstDb).RememberAsync(
                    "conn",
                    "g1",
                    $"provider-reply-{seed}",
                    incoming.MessageId,
                    $"reply-{seed}");
            }

            // Fresh context models API restart / retry after the caller never observed
            // final persistence. Recovery must happen from durable provider evidence
            // before any feature lane can mutate or send again.
            await using (var retryDb = new VolleyDraftDbContext(options))
            {
                var retry = await ZaloInboundCoordinator.TryClaimTrackedAsync(
                    retryDb,
                    NullLogger<ZaloInboundCoordinator>.Instance,
                    incoming with
                    {
                        Content = incoming.Content + " duplicate retry",
                        SenderName = "mutated retry sender",
                        SentAtUnixMs = incoming.SentAtUnixMs + 999
                    });

                Assert.True(
                    retry.IsDuplicate,
                    $"seed={seed}; fingerprint=idempotency:provider-receipt-retry-reclaimed-authority");
            }

            await using var verifier = new VolleyDraftDbContext(options);
            var row = await verifier.ZaloGroupMessages
                .AsNoTracking()
                .SingleAsync(message => message.ZaloConnectionId == "conn" && message.MessageId == incoming.MessageId);
            Assert.True(
                row.BotReplySentAt is not null,
                $"seed={seed}; fingerprint=idempotency:provider-receipt-not-promoted-terminal");
            Assert.Equal("sent_recovered", row.ReplyOutcome);
            Assert.Null(row.ProcessingStartedAt);
            Assert.Null(row.ProcessingToken);

            var receipt = await new ZaloOutboundReceiptStore(verifier)
                .LoadLatestByParentAsync("conn", "g1", incoming.MessageId);
            Assert.NotNull(receipt);
            Assert.Equal($"provider-reply-{seed}", receipt.ProviderMessageId);
        }
    }

    [Fact]
    public async Task Provider_receipt_scope_does_not_leak_across_parent_message_identity()
    {
        var connectionString = $"Data Source=outbound-partial-scope-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await SeedTrackedGroupAsync(options, 9001);
        await using (var db = new VolleyDraftDbContext(options))
        {
            await new ZaloOutboundReceiptStore(db).RememberAsync(
                "conn",
                "g1",
                "provider-other",
                "other-message",
                "already sent reply");
        }

        var incoming = Incoming(
            "current-message",
            "u-current",
            "@Npc 10",
            new DateTimeOffset(2026, 9, 11, 4, 0, 0, TimeSpan.Zero));
        await using var currentDb = new VolleyDraftDbContext(options);
        var claim = await ZaloInboundCoordinator.TryClaimTrackedAsync(
            currentDb,
            NullLogger<ZaloInboundCoordinator>.Instance,
            incoming);

        Assert.True(
            claim.IsTracked && !claim.IsDuplicate,
            "fingerprint=idempotency:provider-receipt-cross-parent-leak");
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
            true,
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
            Email = $"outbound-partial-{seed}@example.test",
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
