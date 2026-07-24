using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloActivityBackfillCoordinatorTests
{
    [Fact]
    public async Task Full_backfill_is_resumable_normalized_and_idempotent()
    {
        await using var fixture = await BackfillFixture.CreateAsync();

        await fixture.Coordinator.QueueGroupAsync("connection", "group", true);
        Assert.True(await fixture.Coordinator.ProcessNextAsync(CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var firstJob = await fixture.Db.ZaloActivityBackfillJobs.SingleAsync();
        Assert.True(
            firstJob.Status == ZaloActivityBackfillStatus.CompletedWithLimitations,
            $"Unexpected status {firstJob.Status}: {firstJob.LastErrorSummary}");
        Assert.Equal(ZaloMessageHistoryCapability.PartialHistoricalBackfill, firstJob.MessageHistoryCapability);
        Assert.Equal(2, await fixture.Db.ZaloGroupMembers.CountAsync());
        Assert.Equal(2, await fixture.Db.ZaloPollSnapshots.CountAsync());
        Assert.Equal(1, await fixture.Db.ZaloPollSnapshots.CountAsync(item => item.IsAnalyticsEligible));
        Assert.Equal(2, await fixture.Db.ZaloPollVoteActivities.CountAsync());
        Assert.Equal(2, await fixture.Db.ZaloGroupMessages.CountAsync());

        await fixture.Coordinator.QueueGroupAsync("connection", "group", true);
        Assert.True(await fixture.Coordinator.ProcessNextAsync(CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(2, await fixture.Db.ZaloGroupMembers.CountAsync());
        Assert.Equal(2, await fixture.Db.ZaloPollSnapshots.CountAsync());
        Assert.Equal(2, await fixture.Db.ZaloPollVoteActivities.CountAsync());
        Assert.Equal(2, await fixture.Db.ZaloGroupMessages.CountAsync());
        Assert.Equal(1, await fixture.Db.ZaloActivityBackfillJobs.CountAsync());
    }

    private sealed class BackfillFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly HttpClient httpClient;
        public VolleyDraftDbContext Db { get; }
        public ZaloActivityBackfillCoordinator Coordinator { get; }

        private BackfillFixture(
            SqliteConnection connection,
            HttpClient httpClient,
            VolleyDraftDbContext db,
            ZaloActivityBackfillCoordinator coordinator)
        {
            this.connection = connection;
            this.httpClient = httpClient;
            Db = db;
            Coordinator = coordinator;
        }

        public static async Task<BackfillFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaPatch.EnsureLatestAsync(db);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = "backfill-test-key-that-is-long-enough",
                    ["ZaloActivitySync:BoardPageSize"] = "50",
                    ["ZaloActivitySync:MaxBoardPages"] = "10",
                    ["ZaloActivitySync:IncrementalBoardPages"] = "2",
                    ["ZaloActivitySync:MessageHistoryCount"] = "100",
                    ["ZaloActivitySync:RetryCount"] = "0",
                    ["ZaloActivitySync:PauseBetweenRequestsMs"] = "0",
                    ["ZaloActivitySync:IncrementalMinutes"] = "60"
                })
                .Build();
            var protector = new ZaloCredentialProtector(configuration);
            db.Users.Add(new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = "admin@backfill.test",
                PasswordHash = "hash"
            });
            db.ZaloConnections.Add(new ZaloConnection
            {
                Id = "connection",
                AdminUserId = "admin",
                AccountZaloId = "bot",
                DisplayName = "Bot",
                EncryptedCredentials = protector.Protect(
                    "{\"cookie\":[],\"imei\":\"test\",\"userAgent\":\"test\",\"language\":\"vi\"}"),
                Status = ZaloConnectionStatus.Connected
            });
            await db.SaveChangesAsync();

            var httpClient = new HttpClient(new BridgeFixtureHandler())
            {
                BaseAddress = new Uri("http://bridge.test/")
            };
            var bridge = new ZaloBridgeClient(httpClient);
            var coordinator = new ZaloActivityBackfillCoordinator(
                db,
                bridge,
                protector,
                configuration,
                NullLogger<ZaloActivityBackfillCoordinator>.Instance);
            return new BackfillFixture(connection, httpClient, db, coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            httpClient.Dispose();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class BridgeFixtureHandler : HttpMessageHandler
    {
        private static readonly long GroupCreated = new DateTimeOffset(
            2025, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        private static readonly long PollCreated = new DateTimeOffset(
            2026, 7, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            object body = path switch
            {
                "/v1/groups/group/members" => new
                {
                    groupId = "group",
                    groupName = "Nhóm test",
                    groupCreatedAtUnixMs = GroupCreated,
                    expectedMemberCount = 2,
                    isComplete = true,
                    members = new[]
                    {
                        new { zaloUserId = "u1", displayName = "Nguyễn A", zaloName = "Nguyễn A", avatarUrl = (string?)"https://example.test/a.jpg" },
                        new { zaloUserId = "u2", displayName = "Trần B", zaloName = "Trần B", avatarUrl = (string?)null }
                    }
                },
                "/v1/groups/group/board-pages" => new
                {
                    groupId = "group",
                    page = 1,
                    pageSize = 50,
                    totalCount = 2,
                    items = new[]
                    {
                        new { stableId = "poll:poll-1", boardType = 2, isPoll = true, pollId = "poll-1", poll = (object?)null },
                        new { stableId = "poll:poll-2", boardType = 2, isPoll = true, pollId = "poll-2", poll = (object?)null }
                    }
                },
                "/v1/polls/poll-1" => Poll(
                    "poll-1",
                    "Đăng ký sân Thứ 6",
                    false,
                    new[]
                    {
                        new { id = "o1", content = "T4", voteCount = 1, voterIds = new[] { "u1" } },
                        new { id = "o2", content = "T6", voteCount = 1, voterIds = new[] { "u1" } }
                    }),
                "/v1/polls/poll-2" => Poll(
                    "poll-2",
                    "Poll ẩn danh",
                    true,
                    new[]
                    {
                        new { id = "o3", content = "Có", voteCount = 1, voterIds = Array.Empty<string>() }
                    }),
                "/v1/groups/group/message-history" => new
                {
                    groupId = "group",
                    requestedCount = 100,
                    returnedCount = 2,
                    more = 1,
                    lastActionId = "last",
                    lastActionIdOther = (string?)null,
                    oldestMessageAtUnixMs = PollCreated,
                    newestMessageAtUnixMs = PollCreated + 60_000,
                    messages = new[]
                    {
                        new
                        {
                            messageId = "m1",
                            senderId = "u1",
                            senderName = "Nguyễn A",
                            content = "hello",
                            messageType = "chat",
                            isFromBot = false,
                            sentAtUnixMs = PollCreated
                        },
                        new
                        {
                            messageId = "m2",
                            senderId = "bot",
                            senderName = "Bot",
                            content = "reply",
                            messageType = "chat",
                            isFromBot = true,
                            sentAtUnixMs = PollCreated + 60_000
                        }
                    }
                },
                _ => throw new InvalidOperationException($"Unexpected bridge request: {request.Method} {path}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body)
            });
        }

        private static object Poll(
            string id,
            string question,
            bool isAnonymous,
            object options) =>
            new
            {
                id,
                question,
                creatorId = "u1",
                options,
                allowMultipleChoices = true,
                isAnonymous,
                isClosed = false,
                hideVotePreview = false,
                uniqueVoteCount = 1,
                createdAtUnixMs = PollCreated,
                updatedAtUnixMs = PollCreated,
                expiredAtUnixMs = 0
            };
    }
}
