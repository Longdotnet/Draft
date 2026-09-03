using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPassSlotSessionScopeIntegrationTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [Fact]
    public async Task Sunday_current_open_query_does_not_leak_another_future_sessions_offer()
    {
        var referenceNow = new DateTimeOffset(2026, 9, 2, 15, 0, 0, VietnamOffset);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VolleyDraftDbContext(new DbContextOptionsBuilder<VolleyDraftDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Id = "admin",
            DisplayName = "Admin",
            Email = $"pass-scope-{Guid.NewGuid():n}@example.test",
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
        db.MatchSessions.AddRange(
            Session("fri-04", "T6 4/9", new DateTimeOffset(2026, 9, 4, 17, 30, 0, VietnamOffset), admin, zalo),
            Session("sun-06", "CN 6/9", new DateTimeOffset(2026, 9, 6, 17, 30, 0, VietnamOffset), admin, zalo));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new ZaloOpenSlotOfferStore(db);
        await OpenAsync(store, "friday-owner", "Hoàng Nguyễn", "fri-04", "T6 4/9", "m-friday");
        await OpenAsync(store, "sunday-owner", "Pin", "sun-06", "CN 6/9", "m-sunday");

        var reply = await new ZaloPassSlotHistoryFactService(db).TryBuildAsync(
            "conn",
            "g1",
            Message("q-cn-open", "CN này còn slot nào đang mở chưa ai nhận?"),
            referenceNow);

        Assert.NotNull(reply);
        Assert.Contains("1 slot", reply!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pin", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("6/9", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hoàng Nguyễn", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4/9", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static MatchSession Session(
        string id,
        string name,
        DateTimeOffset start,
        User admin,
        ZaloConnection zalo) => new()
    {
        Id = id,
        Name = name,
        AdminUserId = admin.Id,
        AdminUser = admin,
        ZaloConnectionId = zalo.Id,
        ZaloConnection = zalo,
        ZaloGroupId = "g1",
        BotEnabled = true,
        Status = SessionStatus.Setup,
        StartTime = start
    };

    private static Task<ZaloOpenSlotOfferSnapshot> OpenAsync(
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

    private static ZaloIncomingMessageEvent Message(string id, string content) => new(
        accountId: "bot-account",
        botId: "bot-account",
        groupId: "g1",
        messageId: id,
        senderId: "client",
        senderName: "Long",
        content: content,
        mentions: [],
        mentionedBot: false,
        sentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
