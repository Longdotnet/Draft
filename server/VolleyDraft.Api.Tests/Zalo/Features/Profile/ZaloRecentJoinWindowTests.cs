using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VolleyDraft.Api.Data;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Profile;

public sealed class ZaloRecentJoinWindowTests
{
    [Theory]
    [InlineData("những ai vào nhóm 45 ngày đổ lại?", 45)]
    [InlineData("7 ngày qua có ai mới vào?", 7)]
    [InlineData("thành viên mới +12 ngày qua", 12)]
    public void Valid_recent_join_windows_are_read_verbatim(string question, int expected)
    {
        Assert.True(ZaloMemberIntelligenceBotService.TryReadRecentJoinDays(question, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("những ai vào nhóm -2 ngày đổ lại?")]
    [InlineData("những ai vào nhóm 0 ngày đổ lại?")]
    [InlineData("thành viên mới 3651 ngày qua")]
    [InlineData("thành viên mới 999999999999999999999 ngày qua")]
    public void Invalid_recent_join_windows_are_never_silently_substituted(string question)
    {
        Assert.Equal(ZaloBotIntent.ListRecentlyJoinedMembers,
            ZaloBotIntelligence.ClassifyDeterministically(question).Intent);
        Assert.True(ZaloMemberIntelligenceBotService.TryReadRecentJoinDays(question, out var days));
        Assert.False(days is >= 1 and <= 3650);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task Backend_rejects_invalid_window_even_if_caller_bypasses_bot_parser(int days)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<VolleyDraftDbContext>().UseSqlite(connection).Options;
        await using var db = new VolleyDraftDbContext(options);
        var service = new ZaloMembershipHistoryService(db, NullLogger<ZaloMembershipHistoryService>.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.QueryRecentAsync("connection", "group", days, DateTimeOffset.UtcNow));
    }
}
