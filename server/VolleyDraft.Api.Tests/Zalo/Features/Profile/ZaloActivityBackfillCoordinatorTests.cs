using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Contracts;
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

    [Fact]
    public async Task Requeue_does_not_steal_running_lease_from_concurrent_worker()
    {
        await using var fixture = await BackfillFixture.CreateAsync();
        var job = await fixture.Coordinator.QueueGroupAsync("connection", "group", true);
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        job.Status = ZaloActivityBackfillStatus.Completed;
        job.Stage = ZaloActivityBackfillStage.Completed;
        job.IsFullBackfill = false;
        job.BackfillStartedAt = completedAt.AddMinutes(-5);
        job.BackfillCompletedAt = completedAt;
        job.LastIncrementalSyncAt = completedAt;
        await fixture.Db.SaveChangesAsync();

        fixture.Db.ChangeTracker.Clear();
        var staleCompleted = await fixture.Db.ZaloActivityBackfillJobs.SingleAsync();
        Assert.Equal(ZaloActivityBackfillStatus.Completed, staleCompleted.Status);

        await using var peer = fixture.CreatePeerDbContext();
        var leaseUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        var claimed = await peer.ZaloActivityBackfillJobs
            .Where(item => item.Id == job.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, ZaloActivityBackfillStatus.Running)
                .SetProperty(item => item.Stage, ZaloActivityBackfillStage.SyncingMembers)
                .SetProperty(item => item.LeaseToken, "peer-lease")
                .SetProperty(item => item.LeaseUntil, leaseUntil));
        Assert.Equal(1, claimed);

        var result = await fixture.Coordinator.QueueGroupAsync("connection", "group", true);

        Assert.Equal(ZaloActivityBackfillStatus.Running, result.Status);
        Assert.Equal("peer-lease", result.LeaseToken);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.ZaloActivityBackfillJobs.SingleAsync();
        Assert.Equal(ZaloActivityBackfillStatus.Running, persisted.Status);
        Assert.Equal(ZaloActivityBackfillStage.SyncingMembers, persisted.Stage);
        Assert.Equal("peer-lease", persisted.LeaseToken);
        Assert.Equal(leaseUntil, persisted.LeaseUntil);
        Assert.Equal(completedAt, persisted.BackfillCompletedAt);
        Assert.False(persisted.IsFullBackfill);
    }

    [Fact]
    public async Task Missing_backfill_is_discovered_from_durable_tracked_group_without_active_session()
    {
        await using var fixture = await BackfillFixture.CreateAsync();
        var settingsStore = new ZaloAutoSessionSettingsStore(fixture.Db);
        await settingsStore.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin",
            ZaloConnectionId = "connection",
            GroupId = "tracked-only",
            GroupName = "Nhóm được theo dõi lâu dài",
            AutoSessionEnabled = false
        });

        var queued = await fixture.Coordinator.QueueMissingLinkedGroupsAsync();

        Assert.Equal(1, queued);
        var job = await fixture.Db.ZaloActivityBackfillJobs.SingleAsync();
        Assert.Equal("connection", job.ZaloConnectionId);
        Assert.Equal("tracked-only", job.GroupId);
        Assert.True(job.IsFullBackfill);
        Assert.Equal(ZaloActivityBackfillStatus.Queued, job.Status);
    }

    [Fact]
    public async Task Durable_and_legacy_discovery_of_same_group_queues_exactly_once()
    {
        await using var fixture = await BackfillFixture.CreateAsync();
        var session = await fixture.Db.MatchSessions.SingleAsync();
        session.BotEnabled = true;
        await fixture.Db.SaveChangesAsync();
        var settingsStore = new ZaloAutoSessionSettingsStore(fixture.Db);
        await settingsStore.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin",
            ZaloConnectionId = "connection",
            GroupId = "group",
            GroupName = "CLB Bóng Chuyền Newbie"
        });

        var firstQueued = await fixture.Coordinator.QueueMissingLinkedGroupsAsync();
        var secondQueued = await fixture.Coordinator.QueueMissingLinkedGroupsAsync();

        Assert.Equal(1, firstQueued);
        Assert.Equal(0, secondQueued);
        Assert.Equal(1, await fixture.Db.ZaloActivityBackfillJobs.CountAsync());
    }

    [Fact]
    public async Task Stale_tracked_group_whose_connection_no_longer_exists_is_not_queued()
    {
        await using var fixture = await BackfillFixture.CreateAsync();
        var settingsStore = new ZaloAutoSessionSettingsStore(fixture.Db);
        await settingsStore.InsertIfMissingAsync(new ZaloTrackedGroupData
        {
            AdminUserId = "admin",
            ZaloConnectionId = "deleted-connection",
            GroupId = "orphan-group",
            GroupName = "Nhóm cấu hình cũ"
        });

        var queued = await fixture.Coordinator.QueueMissingLinkedGroupsAsync();

        Assert.Equal(0, queued);
        Assert.Empty(await fixture.Db.ZaloActivityBackfillJobs.ToListAsync());
    }

    [Fact]
    public async Task Desktop_backup_import_is_group_scoped_and_idempotent()
    {
        await using var fixture = await BackfillFixture.CreateAsync();
        var timestamp = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var request = new ImportZaloDesktopHistoryRequest(
            "CLB Bóng Chuyền Newbie",
            [
                new ZaloDesktopMessageImportItem(
                    "desktop-1",
                    "u1",
                    "Nguyễn A",
                    "desktop-webchat",
                    timestamp,
                    "Mình tham gia nha"),
                new ZaloDesktopMessageImportItem(
                    "desktop-2",
                    "u2",
                    "Trần B",
                    "desktop-photo",
                    timestamp + 60_000)
            ],
            true);

        var first = await fixture.Coordinator.ImportDesktopHistoryForSessionAsync(
            "admin",
            "session",
            request,
            CancellationToken.None);
        var second = await fixture.Coordinator.ImportDesktopHistoryForSessionAsync(
            "admin",
            "session",
            request with
            {
                Messages =
                [
                    request.Messages[0] with { SenderZaloUserId = "u1-current" },
                    request.Messages[1]
                ]
            },
            CancellationToken.None);
        var wrongGroup = await fixture.Coordinator.ImportDesktopHistoryForSessionAsync(
            "admin",
            "session",
            request with { SourceGroupName = "Nhóm khác" },
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(2, first.Value!.InsertedCount);
        Assert.True(second.IsSuccess);
        Assert.Equal(0, second.Value!.InsertedCount);
        Assert.Equal(2, second.Value.UpdatedCount);
        Assert.False(wrongGroup.IsSuccess);
        Assert.Equal(2, await fixture.Db.ZaloGroupMessages.CountAsync(message =>
            message.ObservationSource == "DesktopBackupImport"));
        Assert.Equal(
            "u1-current",
            (await fixture.Db.ZaloGroupMessages.SingleAsync(message =>
                message.MessageId == "desktop-1")).SenderId);
        Assert.Equal(
            "Mình tham gia nha",
            (await fixture.Db.ZaloGroupMessages.SingleAsync(message =>
                message.MessageId == "desktop-1")).Content);
        var job = await fixture.Db.ZaloActivityBackfillJobs.SingleAsync();
        Assert.Equal(ZaloMessageHistoryCapability.PartialHistoricalBackfill, job.MessageHistoryCapability);
        Assert.Equal(2, job.MessagesImported);
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
            db.MatchSessions.Add(new MatchSession
            {
                Id = "session",
                AdminUserId = "admin",
                Name = "Buổi test",
                ZaloConnectionId = "connection",
                ZaloGroupId = "group",
                ZaloGroupName = "CLB Bóng Chuyền Newbie"
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

        public VolleyDraftDbContext CreatePeerDbContext()
        {
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            return new VolleyDraftDbContext(options);
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
                    isSupported = true,
                    limitationCode = (string?)null,
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
