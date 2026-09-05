using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloOpenSlotPendingTopicSwitchTests
{
    [Fact]
    public async Task Fresh_cancel_reminder_is_not_consumed_by_pending_open_slot()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreatePendingClaimAsync();

        Assert.Equal(
            ZaloBotIntent.CancelReminder,
            ZaloBotIntelligence.ClassifyDeterministically("hủy lịch nhắc").Intent);

        var result = await fixture.Service.TryHandleAsync(
            "conn",
            "g1",
            Message("m-reminder", "claimant", "Long", "hủy lịch nhắc"));

        Assert.False(result.Handled);
        var pending = await fixture.Store.LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.NotNull(pending);
        Assert.Equal(ZaloOpenSlotOfferStatus.ClaimPending, pending!.Status);
    }

    [Fact]
    public async Task Cancel_owned_pass_does_not_release_an_unrelated_pending_claim()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreatePendingClaimAsync();
        await fixture.Store.OpenAsync(
            "conn",
            "g1",
            "claimant",
            "Long",
            "session-owned",
            "T6",
            "owner-message",
            DateTimeOffset.UtcNow.AddHours(2),
            DateTimeOffset.UtcNow.AddMinutes(45));

        var result = await fixture.Service.TryHandleAsync(
            "conn",
            "g1",
            Message("m-cancel-owned", "claimant", "Long", "hủy pass"));

        Assert.True(result.Handled);
        Assert.Contains("huỷ pass slot T6", result.Response, StringComparison.OrdinalIgnoreCase);

        var pending = await fixture.Store.LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.NotNull(pending);
        Assert.Equal("session-pending", pending!.SessionId);
        Assert.Equal(ZaloOpenSlotOfferStatus.ClaimPending, pending.Status);

        var owned = await fixture.Store.ListOwnedActiveAsync("conn", "g1", "claimant");
        Assert.Empty(owned);
    }

    [Fact]
    public async Task Waitlist_confirmation_phrase_is_not_stolen_by_pending_open_slot()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreatePendingClaimAsync();

        Assert.Equal(
            ZaloBotIntent.WaitlistAccept,
            ZaloBotIntelligence.ClassifyDeterministically("chốt slot").Intent);

        var result = await fixture.Service.TryHandleAsync(
            "conn",
            "g1",
            Message("m-waitlist", "claimant", "Long", "chốt slot"));

        Assert.False(result.Handled);
        var pending = await fixture.Store.LoadPendingClaimAsync("conn", "g1", "claimant");
        Assert.NotNull(pending);
        Assert.Equal(ZaloOpenSlotOfferStatus.ClaimPending, pending!.Status);
    }

    [Theory]
    [InlineData("hủy")]
    [InlineData("thôi")]
    [InlineData("không cần nữa")]
    [InlineData("hủy nhận")]
    [InlineData("không nhận nữa")]
    public void Reservation_cancel_vocabulary_remains_owned_by_open_slot(string text)
    {
        Assert.True(ZaloOpenSlotOfferService.IsPendingClaimCancellation(text));
    }

    [Theory]
    [InlineData("hủy reminder")]
    [InlineData("hủy lịch nhắc")]
    [InlineData("hủy share slot")]
    [InlineData("hủy chờ slot")]
    [InlineData("hủy pass")]
    public void Domain_qualified_cancel_vocabulary_is_not_generic_pending_cancel(string text)
    {
        Assert.False(ZaloOpenSlotOfferService.IsPendingClaimCancellation(text));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("chốt")]
    [InlineData("xong")]
    [InlineData("vote xong")]
    [InlineData("xác nhận")]
    public void Reservation_confirmation_vocabulary_remains_owned_by_open_slot(string text)
    {
        Assert.True(ZaloOpenSlotOfferService.IsPendingClaimConfirmation(text));
    }

    [Theory]
    [InlineData("chốt slot")]
    [InlineData("đồng ý nhận")]
    [InlineData("xác nhận tham gia")]
    public void Domain_qualified_confirmation_is_not_generic_pending_confirmation(string text)
    {
        Assert.False(ZaloOpenSlotOfferService.IsPendingClaimConfirmation(text));
    }

    private static ZaloIncomingMessageEvent Message(
        string messageId,
        string senderId,
        string senderName,
        string content) => new(
            "bot-account",
            "bot-account",
            "g1",
            messageId,
            senderId,
            senderName,
            content,
            [],
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
            Store = new ZaloOpenSlotOfferStore(db);
            Service = new ZaloOpenSlotOfferService(db);
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }
        public ZaloOpenSlotOfferStore Store { get; }
        public ZaloOpenSlotOfferService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new VolleyDraftDbContext(
                new DbContextOptionsBuilder<VolleyDraftDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async Task CreatePendingClaimAsync()
        {
            var offer = await Store.OpenAsync(
                "conn",
                "g1",
                "owner",
                "Trí",
                "session-pending",
                "T4",
                "pass-message",
                DateTimeOffset.UtcNow.AddHours(2),
                DateTimeOffset.UtcNow.AddMinutes(45));
            Assert.True(await Store.TryClaimAsync(
                offer,
                "claimant",
                "Long",
                "claim-message",
                DateTimeOffset.UtcNow.AddMinutes(20)));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
