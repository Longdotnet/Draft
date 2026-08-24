using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloNaturalProfileReplyTests
{
    [Theory]
    [InlineData("nam", PlayerGender.Male)]
    [InlineData("nữ", PlayerGender.Female)]
    [InlineData("tui là nam nha", PlayerGender.Male)]
    [InlineData("con gái", PlayerGender.Female)]
    [InlineData("nam 😆", PlayerGender.Male)]
    public void Parser_AcceptsNaturalGenderReplies(string text, PlayerGender expected)
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(text, true, false, false);

        Assert.True(parsed.LooksLikeProfileAnswer);
        Assert.False(parsed.HasConflict);
        Assert.Equal(expected, parsed.Gender);
    }

    [Theory]
    [InlineData("công", PlayerRole.Attack)]
    [InlineData("tui đánh chủ công", PlayerRole.Attack)]
    [InlineData("thủ nha", PlayerRole.Defense)]
    [InlineData("libero", PlayerRole.Defense)]
    [InlineData("chuyền 2", PlayerRole.Setter)]
    [InlineData("toàn diện", PlayerRole.FullStack)]
    [InlineData("công nha 😎", PlayerRole.Attack)]
    public void Parser_AcceptsNaturalRoleReplies(string text, PlayerRole expected)
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(text, false, true, false);

        Assert.True(parsed.LooksLikeProfileAnswer);
        Assert.False(parsed.HasConflict);
        Assert.Equal(expected, parsed.Role);
    }

    [Theory]
    [InlineData("mới", PlayerLevel.New)]
    [InlineData("mới chơi", PlayerLevel.New)]
    [InlineData("tầm trung bình", PlayerLevel.Average)]
    [InlineData("khá", PlayerLevel.Good)]
    [InlineData("chơi tốt", PlayerLevel.Good)]
    public void Parser_AcceptsNaturalLevelReplies(string text, PlayerLevel expected)
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(text, false, false, true);

        Assert.True(parsed.LooksLikeProfileAnswer);
        Assert.False(parsed.HasConflict);
        Assert.Equal(expected, parsed.Level);
    }

    [Theory]
    [InlineData("tui nam, đánh công, tầm trung bình thôi")]
    [InlineData("nam 😆, công nha; tầm trung bình")]
    [InlineData("Nam - chủ công - khá nha")]
    public void Parser_AcceptsWholeSentenceWithoutCommandTemplate(string text)
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(text, true, true, true);

        Assert.True(parsed.LooksLikeProfileAnswer);
        Assert.False(parsed.HasConflict);
        Assert.Equal(PlayerGender.Male, parsed.Gender);
        Assert.Equal(PlayerRole.Attack, parsed.Role);
        Assert.NotNull(parsed.Level);
    }

    [Fact]
    public void Parser_OnlyReturnsFieldsCurrentPromptStillNeeds()
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(
            "nam, công, tốt",
            false,
            true,
            false);

        Assert.Null(parsed.Gender);
        Assert.Equal(PlayerRole.Attack, parsed.Role);
        Assert.Null(parsed.Level);
    }

    [Theory]
    [InlineData("nam nữ")]
    [InlineData("công với thủ")]
    [InlineData("mới mà cũng trung bình")]
    public void Parser_FailsClosedOnConflictingValues(string text)
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(text, true, true, true);

        Assert.True(parsed.LooksLikeProfileAnswer);
        Assert.True(parsed.HasConflict);
    }

    [Theory]
    [InlineData("để sau")]
    [InlineData("chưa biết")]
    [InlineData("bỏ qua đi")]
    public void Parser_AllowsMemberToDeclineWithoutPressure(string text)
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(text, true, true, true);

        Assert.True(parsed.WantsToSkip);
    }

    [Theory]
    [InlineData("draft đi")]
    [InlineData("kiếm thêm 1 người")]
    [InlineData("pass slot")]
    [InlineData("hello ae")]
    [InlineData("thứ 6 tui đi nha")]
    [InlineData("công ty nay vui ghê")]
    public void Parser_DoesNotHijackOtherConversation(string text)
    {
        var parsed = ZaloNaturalProfileReplyParser.Parse(text, true, true, true);

        Assert.False(parsed.LooksLikeProfileAnswer);
    }

    [Fact]
    public async Task PromptStore_IsIndependentAndKeepsExactUidContext()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ZaloMissingProfilePromptStore(fixture.Db);
        var now = DateTimeOffset.UtcNow;

        var saved = await store.UpsertAsync(
            "connection-1",
            "group-1",
            "session-1",
            "player-1",
            "uid-123",
            "Long",
            true,
            true,
            false,
            "provider-message-1",
            now,
            now.AddMinutes(30));

        var active = await store.GetActiveAsync(now.AddMinutes(1));
        var prompt = Assert.Single(active);
        Assert.Equal(saved.Id, prompt.Id);
        Assert.Equal("uid-123", prompt.ZaloUserId);
        Assert.Equal("player-1", prompt.SessionPlayerId);
        Assert.True(prompt.MissingGender);
        Assert.True(prompt.MissingRole);
        Assert.False(prompt.MissingLevel);
        Assert.Equal("provider-message-1", prompt.PromptMessageId);

        await store.UpdateProgressAsync(
            prompt.Id,
            false,
            true,
            false,
            now.AddMinutes(2),
            false);
        var updated = Assert.Single(await store.GetActiveAsync(now.AddMinutes(3)));
        Assert.False(updated.MissingGender);
        Assert.True(updated.MissingRole);

        await store.CompleteAsync(updated.Id, now.AddMinutes(4));
        Assert.Empty(await store.GetActiveAsync(now.AddMinutes(5)));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, VolleyDraftDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }
        public VolleyDraftDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VolleyDraftDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new VolleyDraftDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
