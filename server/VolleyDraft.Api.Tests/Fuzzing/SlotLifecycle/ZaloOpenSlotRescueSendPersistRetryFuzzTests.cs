using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloOpenSlotRescueSendPersistRetryFuzzTests
{
    [Fact]
    public async Task Accepted_rescue_send_reuses_one_logical_idempotency_key_after_local_persistence_failure()
    {
        for (var seed = 1; seed <= 64; seed += 1)
        {
            var connectionString = $"Data Source=slot-rescue-send-persist-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var now = DateTimeOffset.UtcNow.AddHours(1).AddMinutes(seed * 7);

            await SeedDueOfferAsync(options, now, seed);
            await InstallOneShotPersistenceFailureTriggerAsync(options);

            var handler = new IdempotentRecordingHandler();
            ZaloOpenSlotRescueRunResult first;
            await using (var firstDb = new VolleyDraftDbContext(options))
            {
                first = await CreateService(firstDb, handler).RunDueAsync(now);
            }

            Assert.True(
                first.FailedCount == 1 && first.NudgedCount == 0,
                $"seed={seed}; fingerprint=idempotency:rescue-post-send-persist-failure-not-retryable; " +
                $"failed={first.FailedCount}; nudged={first.NudgedCount}; attempts={handler.HttpAttemptCount}");
            Assert.Equal(1, handler.HttpAttemptCount);
            Assert.Equal(1, handler.LogicalSendCount);

            await DropPersistenceFailureTriggerAsync(options);

            var retryAt = now.AddMinutes(11).AddMilliseconds(seed % 5);
            ZaloOpenSlotRescueRunResult second;
            await using (var secondDb = new VolleyDraftDbContext(options))
            {
                second = await CreateService(secondDb, handler).RunDueAsync(retryAt);
            }

            Assert.True(
                second.NudgedCount == 1 && second.FailedCount == 0,
                $"seed={seed}; fingerprint=idempotency:rescue-post-send-retry-did-not-converge; " +
                $"failed={second.FailedCount}; nudged={second.NudgedCount}; attempts={handler.HttpAttemptCount}");
            Assert.Equal(2, handler.HttpAttemptCount);
            Assert.True(
                handler.LogicalSendCount == 1,
                $"seed={seed}; fingerprint=idempotency:rescue-post-send-retry-changed-key; " +
                $"logicalSends={handler.LogicalSendCount}; keys=[{string.Join(',', handler.IdempotencyKeys)}]");
            Assert.Single(handler.IdempotencyKeys);

            await using var verifier = new VolleyDraftDbContext(options);
            var offer = Assert.Single(await new ZaloOpenSlotOfferStore(verifier)
                .ListClaimableAsync("conn", "g1", "observer"));
            Assert.Equal(1, offer.NudgeCount);
            Assert.NotNull(offer.LastNudgeAt);
            Assert.True(offer.NextNudgeAt is null || offer.NextNudgeAt > retryAt);

            var persistedBotMessages = await verifier.ZaloGroupMessages
                .AsNoTracking()
                .Where(message =>
                    message.ZaloConnectionId == "conn" &&
                    message.GroupId == "g1" &&
                    message.IsFromBot &&
                    message.ReplyOutcome == "open_slot_rescue")
                .ToListAsync();
            Assert.Single(persistedBotMessages);
            Assert.Equal(handler.ProviderMessageId, persistedBotMessages[0].MessageId);
        }
    }

    private static ZaloOpenSlotRescueService CreateService(
        VolleyDraftDbContext db,
        IdempotentRecordingHandler handler)
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
            Email = $"slot-rescue-send-persist-{seed}@example.test",
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

    private static async Task InstallOneShotPersistenceFailureTriggerAsync(
        DbContextOptions<VolleyDraftDbContext> options)
    {
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER fuzz_fail_rescue_message_insert
            BEFORE INSERT ON ZaloGroupMessages
            WHEN NEW.ReplyOutcome = 'open_slot_rescue'
            BEGIN
                SELECT RAISE(FAIL, 'fuzz injected post-send persistence failure');
            END;
            """);
    }

    private static async Task DropPersistenceFailureTriggerAsync(
        DbContextOptions<VolleyDraftDbContext> options)
    {
        await using var db = new VolleyDraftDbContext(options);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fuzz_fail_rescue_message_insert;");
    }

    private sealed class IdempotentRecordingHandler : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly Dictionary<string, string> accepted = new(StringComparer.Ordinal);
        private int httpAttemptCount;

        public int HttpAttemptCount => Volatile.Read(ref httpAttemptCount);
        public int LogicalSendCount
        {
            get
            {
                lock (gate) return accepted.Count;
            }
        }

        public string ProviderMessageId => "provider-rescue-1";

        public IReadOnlyList<string> IdempotencyKeys
        {
            get
            {
                lock (gate) return accepted.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref httpAttemptCount);
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var key = document.RootElement.GetProperty("idempotencyKey").GetString();
            Assert.False(string.IsNullOrWhiteSpace(key));

            lock (gate)
            {
                accepted.TryAdd(key!, ProviderMessageId);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"sent\":true,\"mock\":true,\"messageId\":\"{ProviderMessageId}\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
