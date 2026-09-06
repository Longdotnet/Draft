using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloInboundCoordinatorTests
{
    [Fact]
    public async Task Overbook_winner_is_terminal_and_bot_lane_is_not_invoked()
    {
        var overbookCalls = 0;
        var botCalls = 0;

        var result = await ZaloInboundCoordinator.DispatchAsync(
            Incoming("single-owner-overbook"),
            (_, _) =>
            {
                overbookCalls++;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                botCalls++;
                return Task.CompletedTask;
            });

        Assert.True(result.Accepted);
        Assert.Equal("overbook-confirmation", result.HandledBy);
        Assert.Equal(1, overbookCalls);
        Assert.Equal(0, botCalls);
    }

    [Fact]
    public async Task Bot_lane_runs_once_only_when_overbook_declines_turn()
    {
        var overbookCalls = 0;
        var botCalls = 0;

        var result = await ZaloInboundCoordinator.DispatchAsync(
            Incoming("single-owner-bot"),
            (_, _) =>
            {
                overbookCalls++;
                return Task.FromResult(false);
            },
            (_, _) =>
            {
                botCalls++;
                return Task.CompletedTask;
            });

        Assert.True(result.Accepted);
        Assert.Equal("bot", result.HandledBy);
        Assert.Equal(1, overbookCalls);
        Assert.Equal(1, botCalls);
    }

    [Fact]
    public async Task Duplicate_delivery_is_terminal_before_any_pre_route_or_bot_side_effect()
    {
        var overbookCalls = 0;
        var botCalls = 0;
        var completeCalls = 0;
        var releaseCalls = 0;

        var result = await ZaloInboundCoordinator.DispatchClaimedAsync(
            Incoming("duplicate-before-routing"),
            (_, _) => Task.FromResult(ZaloInboundClaim.Duplicate),
            (_, _) =>
            {
                overbookCalls++;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                botCalls++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                completeCalls++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                releaseCalls++;
                return Task.CompletedTask;
            });

        Assert.True(result.Accepted);
        Assert.Equal("duplicate", result.HandledBy);
        Assert.Equal(0, overbookCalls);
        Assert.Equal(0, botCalls);
        Assert.Equal(0, completeCalls);
        Assert.Equal(0, releaseCalls);
    }

    [Fact]
    public async Task Pre_route_winner_marks_ingress_claim_terminal_without_releasing_to_bot()
    {
        var claim = new ZaloInboundClaim(true, false, "row-1", "token-1");
        var completed = new List<ZaloInboundClaim>();
        var released = new List<ZaloInboundClaim>();
        var botCalls = 0;

        var result = await ZaloInboundCoordinator.DispatchClaimedAsync(
            Incoming("claimed-pre-route"),
            (_, _) => Task.FromResult(claim),
            (_, _) => Task.FromResult(true),
            (_, _) =>
            {
                botCalls++;
                return Task.CompletedTask;
            },
            (current, _) =>
            {
                completed.Add(current);
                return Task.CompletedTask;
            },
            (current, _) =>
            {
                released.Add(current);
                return Task.CompletedTask;
            });

        Assert.Equal("overbook-confirmation", result.HandledBy);
        Assert.Single(completed);
        Assert.Equal(claim, completed[0]);
        Assert.Empty(released);
        Assert.Equal(0, botCalls);
    }

    [Fact]
    public async Task Declined_pre_route_releases_ingress_claim_before_bot_owns_message_lease()
    {
        var claim = new ZaloInboundClaim(true, false, "row-2", "token-2");
        var sequence = new List<string>();

        var result = await ZaloInboundCoordinator.DispatchClaimedAsync(
            Incoming("claimed-bot"),
            (_, _) => Task.FromResult(claim),
            (_, _) =>
            {
                sequence.Add("pre-route");
                return Task.FromResult(false);
            },
            (_, _) =>
            {
                sequence.Add("bot");
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                sequence.Add("complete");
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                sequence.Add("release");
                return Task.CompletedTask;
            });

        Assert.Equal("bot", result.HandledBy);
        Assert.Equal(["pre-route", "release", "bot"], sequence);
    }

    [Fact]
    public async Task Failed_or_cancelled_turn_releases_claim_with_non_cancelled_cleanup_token()
    {
        var claim = new ZaloInboundClaim(true, false, "row-3", "token-3");
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();
        CancellationToken cleanupToken = requestCancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ZaloInboundCoordinator.DispatchClaimedAsync(
                Incoming("cancelled-turn"),
                (_, _) => Task.FromResult(claim),
                (_, token) => Task.FromCanceled<bool>(token),
                (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                (_, token) =>
                {
                    cleanupToken = token;
                    return Task.CompletedTask;
                },
                requestCancellation.Token));

        Assert.False(cleanupToken.CanBeCanceled);
    }

    [Fact]
    public async Task Tracked_group_without_match_session_is_claimed_and_persisted_for_activity()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTrackedGroupAsync("g-configured");

        var claim = await ZaloInboundCoordinator.TryClaimTrackedAsync(
            fixture.Db,
            NullLogger<ZaloInboundCoordinator>.Instance,
            Incoming("tracked-no-session", "bot-account_0", "g-configured_0"));

        Assert.True(claim.IsTracked);
        Assert.False(claim.IsDuplicate);
        Assert.Empty(await fixture.Db.MatchSessions.ToListAsync());
        var stored = await fixture.Db.ZaloGroupMessages.SingleAsync();
        Assert.Equal("conn-1", stored.ZaloConnectionId);
        Assert.Equal("g-configured", stored.GroupId);
        Assert.Equal("tracked-no-session", stored.MessageId);
    }

    [Fact]
    public async Task Duplicate_delivery_remains_suppressed_for_tracked_group_without_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTrackedGroupAsync("g-configured");
        var incoming = Incoming("tracked-duplicate", "bot-account", "g-configured");

        var first = await ZaloInboundCoordinator.TryClaimTrackedAsync(
            fixture.Db,
            NullLogger<ZaloInboundCoordinator>.Instance,
            incoming);
        var second = await ZaloInboundCoordinator.TryClaimTrackedAsync(
            fixture.Db,
            NullLogger<ZaloInboundCoordinator>.Instance,
            incoming);

        Assert.True(first.IsTracked);
        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.Single(await fixture.Db.ZaloGroupMessages.ToListAsync());
    }

    [Fact]
    public async Task Tracked_group_id_on_another_account_is_not_claimed_or_persisted()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTrackedGroupAsync("g-configured");

        var claim = await ZaloInboundCoordinator.TryClaimTrackedAsync(
            fixture.Db,
            NullLogger<ZaloInboundCoordinator>.Instance,
            Incoming("wrong-account", "different-account", "g-configured"));

        Assert.False(claim.IsTracked);
        Assert.False(claim.IsDuplicate);
        Assert.Empty(await fixture.Db.ZaloGroupMessages.ToListAsync());
    }

    private static ZaloIncomingMessageEvent Incoming(
        string messageId,
        string accountId = "bot-account",
        string groupId = "g1") => new(
        accountId: accountId,
        botId: accountId,
        groupId: groupId,
        messageId: messageId,
        senderId: "user-1",
        senderName: "Long",
        content: "@Npc test",
        mentions: [],
        mentionedBot: true,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection sqlite, VolleyDraftDbContext db, User admin, ZaloConnection connection)
        {
            Sqlite = sqlite;
            Db = db;
            Admin = admin;
            Connection = connection;
        }

        public SqliteConnection Sqlite { get; }
        public VolleyDraftDbContext Db { get; }
        public User Admin { get; }
        public ZaloConnection Connection { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(sqlite)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin-1",
                DisplayName = "Admin",
                Email = $"inbound-{Guid.NewGuid():n}@example.test",
                PasswordHash = "test"
            };
            var connection = new ZaloConnection
            {
                Id = "conn-1",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "test",
                Status = ZaloConnectionStatus.Connected
            };
            db.Users.Add(admin);
            db.ZaloConnections.Add(connection);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return new Fixture(sqlite, db, admin, connection);
        }

        public async Task AddTrackedGroupAsync(string groupId)
        {
            await new ZaloAutoSessionStore(Db).EnsureAsync();
            var now = DateTimeOffset.UtcNow.ToString("O");
            await Db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "ZaloTrackedGroups" (
                    "Id", "AdminUserId", "ZaloConnectionId", "GroupId", "GroupName", "CreatedAt", "UpdatedAt")
                VALUES (
                    {{Guid.NewGuid().ToString("n")}}, {{Admin.Id}}, {{Connection.Id}}, {{groupId}}, {{groupId}}, {{now}}, {{now}});
                """);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Sqlite.DisposeAsync();
        }
    }
}
