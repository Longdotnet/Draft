using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloOutboundReceiptConnectionResolutionFuzzTests
{
    [Fact]
    public async Task Tracked_group_without_match_session_still_persists_provider_receipt()
    {
        for (var seed = 1; seed <= 64; seed += 1)
        {
            var connectionString = $"Data Source=receipt-resolution-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await SeedTrackedOnlyGroupAsync(options, seed);
            using var provider = new ServiceCollection()
                .AddScoped(_ => new VolleyDraftDbContext(options))
                .BuildServiceProvider();
            var handler = new AcceptedSendHandler($"provider-{seed}");
            var client = new ZaloBridgeClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://bridge.test/") },
                provider.GetRequiredService<IServiceScopeFactory>());

            await client.SendGroupMessageAsync(
                "bot-account",
                "g1",
                $"reply-{seed}",
                [],
                idempotencyKey: $"bot-account:parent-{seed}");

            await using var verifier = new VolleyDraftDbContext(options);
            var receipt = await new ZaloOutboundReceiptStore(verifier)
                .LoadLatestByParentAsync("conn-target", "g1", $"parent-{seed}");

            Assert.True(
                receipt is not null && receipt.ProviderMessageId == $"provider-{seed}",
                $"seed={seed}; fingerprint=idempotency:tracked-group-provider-receipt-not-persisted");
        }
    }

    [Fact]
    public async Task Receipt_resolution_does_not_bind_same_account_to_newer_wrong_group_connection()
    {
        var connectionString = $"Data Source=receipt-resolution-scope-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await SeedTrackedOnlyGroupAsync(options, 9001);
        using var provider = new ServiceCollection()
            .AddScoped(_ => new VolleyDraftDbContext(options))
            .BuildServiceProvider();
        var client = new ZaloBridgeClient(
            new HttpClient(new AcceptedSendHandler("provider-scoped"))
            {
                BaseAddress = new Uri("https://bridge.test/")
            },
            provider.GetRequiredService<IServiceScopeFactory>());

        await client.SendGroupMessageAsync(
            "bot-account",
            "g1",
            "scoped reply",
            [],
            idempotencyKey: "bot-account:parent-scoped");

        await using var verifier = new VolleyDraftDbContext(options);
        var targetReceipt = await new ZaloOutboundReceiptStore(verifier)
            .LoadLatestByParentAsync("conn-target", "g1", "parent-scoped");
        var wrongReceipt = await new ZaloOutboundReceiptStore(verifier)
            .LoadLatestByParentAsync("conn-newer-wrong-group", "g1", "parent-scoped");

        Assert.NotNull(targetReceipt);
        Assert.Null(wrongReceipt);
    }

    private static async Task SeedTrackedOnlyGroupAsync(
        DbContextOptions<VolleyDraftDbContext> options,
        int seed)
    {
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = $"admin-{seed}",
            DisplayName = "Admin",
            Email = $"receipt-resolution-{seed}@example.test",
            PasswordHash = "x"
        };
        var target = new ZaloConnection
        {
            Id = "conn-target",
            AdminUserId = admin.Id,
            AdminUser = admin,
            AccountZaloId = "bot-account",
            DisplayName = "Npc target",
            EncryptedCredentials = "x",
            Status = ZaloConnectionStatus.Connected,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var newerWrongGroup = new ZaloConnection
        {
            Id = "conn-newer-wrong-group",
            AdminUserId = admin.Id,
            AccountZaloId = "bot-account",
            DisplayName = "Npc newer",
            EncryptedCredentials = "x",
            Status = ZaloConnectionStatus.Connected,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(admin, target, newerWrongGroup);
        await db.SaveChangesAsync();

        await new ZaloAutoSessionStore(db).EnsureAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ZaloTrackedGroups" (
                "Id", "AdminUserId", "ZaloConnectionId", "GroupId", "GroupName", "AutoSessionEnabled", "CreatedAt", "UpdatedAt")
            VALUES (
                {{Guid.NewGuid().ToString("n")}}, {{admin.Id}}, {{target.Id}}, {{"g1"}}, {{"g1"}}, {{1}}, {{now}}, {{now}}),
                ({{Guid.NewGuid().ToString("n")}}, {{admin.Id}}, {{newerWrongGroup.Id}}, {{"g-other"}}, {{"g-other"}}, {{1}}, {{now}}, {{now}});
            """);
        db.ChangeTracker.Clear();
    }

    private sealed class AcceptedSendHandler(string providerMessageId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    $"{{\"sent\":true,\"mock\":false,\"messageId\":\"{providerMessageId}\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
