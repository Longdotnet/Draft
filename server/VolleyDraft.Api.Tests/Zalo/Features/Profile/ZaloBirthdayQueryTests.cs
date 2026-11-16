using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Profile;

public sealed class ZaloBirthdayQueryTests
{
    [Theory]
    [InlineData("@bott chúc mừng sinh nhật ai tháng 11", 11)]
    [InlineData("birthday tháng 10 có ai", 10)]
    [InlineData("sinh nhật tháng 1", 1)]
    [InlineData("@bott ai sinh tháng 11", 11)]
    [InlineData("những ai sinh tháng 10", 10)]
    public void Birthday_month_is_read_from_natural_command(string question, int expectedMonth)
    {
        Assert.True(ZaloMemberIntelligenceBotService.TryParseBirthdayMonth(question, out var month));
        Assert.Equal(expectedMonth, month);
    }

    [Theory]
    [InlineData("ai hoạt động nhiều tháng 11")]
    [InlineData("sinh nhật tháng 13")]
    [InlineData("tháng 10 có ai")]
    public void Non_birthday_or_invalid_month_is_not_claimed(string question)
    {
        Assert.False(ZaloMemberIntelligenceBotService.TryParseBirthdayMonth(question, out _));
    }

    [Fact]
    public void Birthday_parser_accepts_string_date_for_non_friend_profile()
    {
        var profile = new BridgeUserProfile(
            "member-1",
            "Member 1",
            null,
            null,
            "1995-11-16",
            null,
            false);

        Assert.True(ZaloMemberIntelligenceBotService.TryParseBirthday(profile, out var birthday));
        Assert.Equal(new DateOnly(1995, 11, 16), birthday);
    }

    [Fact]
    public void Birthday_parser_accepts_unix_milliseconds()
    {
        var unix = new DateTimeOffset(1995, 10, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var profile = new BridgeUserProfile("member-2", "Member 2", null, null, null, unix, null);

        Assert.True(ZaloMemberIntelligenceBotService.TryParseBirthday(profile, out var birthday));
        Assert.Equal(10, birthday.Month);
        Assert.Equal(20, birthday.Day);
    }
}
