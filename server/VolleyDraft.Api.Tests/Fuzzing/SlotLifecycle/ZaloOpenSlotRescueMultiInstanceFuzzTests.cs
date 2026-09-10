using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloOpenSlotRescueMultiInstanceFuzzTests
{
    [Fact]
    public async Task Concurrent_rescue_workers_emit_one_logical_nudge_for_one_due_offer()
    {
        for (var seed = 1; seed <= 128; seed += 1)
        {
            var connectionString = $"Data Source=slot-rescue-worker-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var now = new DateTimeOffset(2026, 9, 10, 2, 0, 0, TimeSpan.Zero).AddMinutes(seed * 5);

            await SeedDueOfferAsync(options, now, seed);

            await using var workerADb = new VolleyDraftDbContext(options);
            await using var workerBDb = new VolleyDraftDbContext(options);
            var handler = new RecordingHandler($"rescue-fuzz-{seed}");
            var workerA = CreateService(workerADb, handler);
            var workerB = CreateService(workerBDb, handler);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstDelay = seed % 4;
            var secondDelay = (seed / 4) % 4;

            var first = RunWorkerAsync(workerA, start.Task, now, firstDelay);
            var second = RunWorkerAsync(workerB, start.Task, now, secondDelay);
            start.SetResult();

            var outcomes = await Task.WhenAll(first, second);
            Assert.All(outcomes, outcome => Assert.Null(outcome.Exception));

            var totalNudged = outcomes.Sum(outcome => outcome.Result?.NudgedCount ?? 0);
            Assert.True(
                totalNudged == 1,
                $"seed={seed}; fingerprint=slot-lifecycle:rescue-workers-duplicate-logical-nudge; " +
                $"delays=[{firstDelay},{secondDelay}]; nudged={totalNudged}; sends={handler.SendCount}");
            Assert.True(
                handler.SendCount == 1,
                $"seed={seed}; fingerprint=idempotency:rescue-workers-duplicate-outbound-send; " +
                $"delays=[{firstDelay},{secondDelay}]; nudged={totalNudged}; sends={handler.SendCount}");

            await using var verifier = new VolleyDraftDbContext(options);
            var store = new ZaloOpenSlotOfferStore(verifier);
            var offer = Assert.Single(await store.ListClaimableAsync("conn", "g1", "observer"));
            Assert.Equal(1, offer.NudgeCount);
            Assert.NotNull(offer.LastNudgeAt);
            Assert.True(offer.NextNudgeAt is null || offer.NextNudgeAt > now);

            var persistedBotMessages = await verifier.ZaloGroupMessages
                .AsNoTracking()
                .CountAsync(message =>
                    message.ZaloConnectionId == "conn" &&
                    message.GroupId == "g1" &&
                    message.IsFromBot &&
                    message.ReplyOutcome == "open_slot_rescue");
            Assert.True(
                persistedBotMessages == 1,
                $"seed={seed}; fingerprint=idempotency:rescue-workers-duplicate-message-ledger; " +
                $"persisted={persistedBotMessages}; sends={handler.SendCount}");
        }
    }

    private static async Task<WorkerOutcome> RunWorkerAsync(
        ZaloOpenSlotRescueService service,
        Task start,
        DateTimeOffset now,
        int delayMilliseconds)
    {
        try
        {
            await start;
            if (delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds);
            return new(await service.RunDueAsync(now), null);
        }
        catch (Exception exception)
        {
            return new(null, exception);
        }
    }

    private static ZaloOpenSlotRescueService CreateService(
        VolleyDraftDbContext db,
        RecordingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:MemberAssist:Rescue:Enabled"] = "true",
                ["ZaloBot:Ambient:MemberAssist:Rescue:MaxNudges"] = "3",
                ["ZaloBot:Ambient:MemberAssist:Rescue:GroupCooldownMinutes"] = "10",
                ["ZaloBot:Ambient:MemberAssist:Rescue:RetryMinutes"] = "10"
            })
            .Build();
        return new ZaloOpenSlotRescueService(
            db,
            new ZaloBridgeClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }),
            configuration,
            NullLogger<ZaloOpenSlotRescueService>.Instance);
    }

    private static async Task SeedDueOfferAsync(
        DbContextOptions<VolleyDraftDbContext> options,
        DateTimeOffset now,
        int seed)
    {
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin",
            DisplayName = "Admin",
            Email = $"slot-rescue-race-{seed}@example.test",
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
        var profile = new PlayerProfile
        {
            Id = "owner-profile",
            ZaloUserId = "owner",
            DisplayName = "Owner",
            Gender = PlayerGender.Male,
            DefaultRole = PlayerRole.Attack,
            DefaultLevel = PlayerLevel.Average
        };
        var session = new MatchSession
        {
            Id = "session",
            Name = "T6",
            AdminUserId = admin.Id,
            AdminUser = admin,
            ZaloConnectionId = connection.Id,
            ZaloConnection = connection,
            ZaloGroupId = "g1",
            BotEnabled = true,
            StartTime = now.AddHours(3),
            Status = SessionStatus.Setup
        };
        var player = new SessionPlayer
        {
            Id = "owner-player",
            SessionId = session.Id,
            Session = session,
            PlayerProfileId = profile.Id,
            PlayerProfile = profile,
            DisplayName = "Owner",
            IsPresent = true,
            Gender = PlayerGender.Male,
            Role = PlayerRole.Attack,
            Level = PlayerLevel.Average
        };
        session.Players.Add(player);
        db.AddRange(admin, connection, profile, session, player);
        await db.SaveChangesAsync();

        await new ZaloOpenSlotOfferStore(db).OpenAsync(
            connection.Id,
            session.ZaloGroupId!,
            profile.ZaloUserId!,
            profile.DisplayName,
            session.Id,
            session.Name,
            $"source-{seed}",
            now.AddHours(2),
            now.AddSeconds(-1));
        db.ChangeTracker.Clear();
    }

    private sealed class RecordingHandler(string messageId) : HttpMessageHandler
    {
        private int sendCount;
        public int SendCount => Volatile.Read(ref sendCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sendCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"sent\":true,\"mock\":true,\"messageId\":\"{messageId}\"}}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed record WorkerOutcome(ZaloOpenSlotRescueRunResult? Result, Exception? Exception);
}
