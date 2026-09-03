using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPassSlotHistoryFactServiceTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Fact]
    public async Task Today_summary_counts_distinct_people_and_lists_all_offers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await fixture.OpenAsync(store, "owner-a", "Anh Duy", "today", "CN", "m1");
        await fixture.OpenAsync(store, "owner-a", "Anh Duy", "tomorrow", "T2", "m2");
        await fixture.OpenAsync(store, "owner-b", "Pin", "today", "CN", "m3");

        var reply = await new ZaloMemberAssistService(fixture.Db).TryBuildAsync(
            "conn",
            "g1",
            Message("q1", "client", "Long", "hôm nay có bao nhiêu người pass slot?"));

        Assert.NotNull(reply);
        Assert.Equal(ZaloMemberAssistKind.PassSlotSummary, reply!.Kind);
        Assert.Contains("2 người", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 slot", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Anh Duy", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pin", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_today_is_not_the_same_as_pass_event_today()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await fixture.OpenAsync(store, "owner-a", "Anh Duy", "today", "CN", "m1");
        await fixture.OpenAsync(store, "owner-b", "Pin", "tomorrow", "T2", "m2");

        var reply = await new ZaloPassSlotHistoryFactService(fixture.Db).TryBuildAsync(
            "conn",
            "g1",
            Message("q2", "client", "Long", "kèo hôm nay có ai pass slot không?"),
            fixture.ReferenceNow);

        Assert.NotNull(reply);
        Assert.Contains("1 người", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Anh Duy", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pin", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Current_open_excludes_offer_that_someone_is_already_holding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        var first = await fixture.OpenAsync(store, "owner-a", "Anh Duy", "today", "CN", "m1");
        await fixture.OpenAsync(store, "owner-b", "Pin", "today", "CN", "m2");
        Assert.True(await store.TryClaimAsync(
            first,
            "claimant",
            "Vivian",
            "claim-1",
            DateTimeOffset.UtcNow.AddMinutes(20)));

        var reply = await new ZaloPassSlotHistoryFactService(fixture.Db).TryBuildAsync(
            "conn",
            "g1",
            Message("q3", "client", "Long", "còn slot nào đang mở chưa ai nhận?"),
            fixture.ReferenceNow);

        Assert.NotNull(reply);
        Assert.Contains("1 slot", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pin", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Anh Duy", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Current_open_excludes_stale_open_offer_for_past_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloOpenSlotOfferStore(fixture.Db);
        await fixture.AddPastSessionAsync("yesterday", "Thứ 7 22/8");
        await fixture.OpenAsync(store, "owner-old", "Tăng Minh Khang", "yesterday", "Thứ 7 22/8", "m-old");
        await fixture.OpenAsync(store, "owner-live", "Hoàng Nguyễn", "today", "CN 23/8", "m-live");

        var reply = await new ZaloPassSlotHistoryFactService(fixture.Db).TryBuildAsync(
            "conn",
            "g1",
            Message("q-stale", "client", "Long", "ai pass slot em lấy nha"),
            fixture.ReferenceNow);

        Assert.NotNull(reply);
        Assert.Contains("1 slot", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hoàng Nguyễn", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tăng Minh Khang", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("22/8", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cn_nay_availability_only_returns_upcoming_sunday_and_open_offer()
    {
        var referenceNow = new DateTimeOffset(2026, 9, 2, 15, 0, 0, VietnamOffset);
        await using var fixture = await Fixture.CreateIncidentAsync(referenceNow);
        var store = new ZaloOpenSlotOfferStore(fixture.Db);

        await fixture.OpenAsync(store, "old-1", "Anh Kha", "sun-23", "CN 23/8", "old-23");
        await fixture.OpenAsync(store, "old-2", "Ngọc Huyền", "sun-30", "CN 30/8", "old-30");
        await fixture.OpenAsync(store, "live", "Pin", "sun-06", "CN 6/9", "live-06");

        var reply = await new ZaloPassSlotHistoryFactService(fixture.Db).TryBuildAsync(
            "conn",
            "g1",
            Message("q-cn", "client", "Vũ", "cn này bạn e mới vô gr nên ko kịp vote, có ai pass ko ạ"),
            referenceNow);

        Assert.NotNull(reply);
        Assert.Contains("1 slot", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pin", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("6/9", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Anh Kha", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ngọc Huyền", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("23/8", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("30/8", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("đã hết hạn", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("hôm nay có bao nhiêu người pass slot?")]
    [InlineData("ai pass slot hnay vậy?")]
    [InlineData("danh sách người pass slot hôm nay")]
    [InlineData("còn slot nào đang mở chưa ai nhận?")]
    [InlineData("ai pass slot em lấy nha")]
    [InlineData("cn này bạn e mới vô gr nên ko kịp vote, có ai pass ko ạ")]
    public void Natural_pass_slot_fact_questions_are_detected(string text)
    {
        Assert.True(ZaloPassSlotHistoryFactService.LooksLikeQuery(text));
    }

    [Theory]
    [InlineData("tui pass slot T6 nha")]
    [InlineData("em nhường suất CN")]
    [InlineData("tui nhận")]
    [InlineData("pass bóng cho tui")]
    public void Mutation_or_unrelated_chat_is_not_summary_query(string text)
    {
        Assert.False(ZaloPassSlotHistoryFactService.LooksLikeQuery(text));
    }

    private static ZaloIncomingMessageEvent Message(
        string id,
        string senderId,
        string senderName,
        string content) => new(
            accountId: "bot-account",
            botId: "bot-account",
            groupId: "g1",
            messageId: id,
            senderId: senderId,
            senderName: senderName,
            content: content,
            mentions: [],
            mentionedBot: false,
            sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db, DateTimeOffset referenceNow)
        {
            Connection = connection;
            Db = db;
            ReferenceNow = referenceNow;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public DateTimeOffset ReferenceNow { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var nowLocal = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
            var referenceNow = new DateTimeOffset(
                nowLocal.Year,
                nowLocal.Month,
                nowLocal.Day,
                12,
                0,
                0,
                VietnamOffset);
            var todayStart = new DateTimeOffset(
                nowLocal.Year,
                nowLocal.Month,
                nowLocal.Day,
                20,
                0,
                0,
                VietnamOffset);
            return await CreateWithSessionsAsync(referenceNow,
            [
                ("today", "CN", todayStart),
                ("tomorrow", "T2", todayStart.AddDays(1))
            ]);
        }

        public static Task<Fixture> CreateIncidentAsync(DateTimeOffset referenceNow) =>
            CreateWithSessionsAsync(referenceNow,
            [
                ("sun-23", "CN 23/8", new DateTimeOffset(2026, 8, 23, 17, 30, 0, VietnamOffset)),
                ("sun-30", "CN 30/8", new DateTimeOffset(2026, 8, 30, 17, 30, 0, VietnamOffset)),
                ("sun-06", "CN 6/9", new DateTimeOffset(2026, 9, 6, 17, 30, 0, VietnamOffset))
            ]);

        private static async Task<Fixture> CreateWithSessionsAsync(
            DateTimeOffset referenceNow,
            IReadOnlyList<(string Id, string Name, DateTimeOffset Start)> sessions)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var admin = new User
            {
                Id = "admin",
                DisplayName = "Admin",
                Email = $"pass-history-{Guid.NewGuid():n}@example.test",
                PasswordHash = "x"
            };
            var zalo = new ZaloConnection
            {
                Id = "conn",
                AdminUserId = admin.Id,
                AdminUser = admin,
                AccountZaloId = "bot-account",
                DisplayName = "Npc",
                EncryptedCredentials = "x"
            };

            db.Users.Add(admin);
            db.ZaloConnections.Add(zalo);
            db.MatchSessions.AddRange(sessions.Select(item => new MatchSession
            {
                Id = item.Id,
                Name = item.Name,
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                BotEnabled = true,
                Status = SessionStatus.Setup,
                StartTime = item.Start
            }));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db, referenceNow);
        }

        public async Task AddPastSessionAsync(string sessionId, string sessionName)
        {
            var admin = await Db.Users.SingleAsync(user => user.Id == "admin");
            var zalo = await Db.ZaloConnections.SingleAsync(connection => connection.Id == "conn");
            Db.MatchSessions.Add(new MatchSession
            {
                Id = sessionId,
                Name = sessionName,
                AdminUserId = admin.Id,
                AdminUser = admin,
                ZaloConnectionId = zalo.Id,
                ZaloConnection = zalo,
                ZaloGroupId = "g1",
                BotEnabled = true,
                Status = SessionStatus.Setup,
                StartTime = ReferenceNow.AddHours(-2)
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public Task<ZaloOpenSlotOfferSnapshot> OpenAsync(
            ZaloOpenSlotOfferStore store,
            string ownerId,
            string ownerName,
            string sessionId,
            string sessionName,
            string messageId) => store.OpenAsync(
                "conn",
                "g1",
                ownerId,
                ownerName,
                sessionId,
                sessionName,
                messageId,
                DateTimeOffset.UtcNow.AddHours(12),
                DateTimeOffset.UtcNow.AddMinutes(45));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}