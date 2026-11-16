using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Social;

public sealed class ZaloBirthdayVideoTests
{
    [Fact]
    public void Birthday_video_uses_personalized_greeting_as_caption()
    {
        var request = new ZaloTrendMusicRenderCompleteRequest(
            "bot",
            "group",
            "connection",
            "2026-11-16",
            "trend-music:birthday:connection:group:20261116:member-1",
            "song",
            "Happy Birthday",
            "Artist",
            1,
            "video",
            "thumb",
            GreetingText: "Chúc mừng sinh nhật An! Cảm ơn bạn đã đồng hành cùng nhóm.");

        Assert.Equal(
            "🎂 Chúc mừng sinh nhật An! Cảm ơn bạn đã đồng hành cùng nhóm.",
            ZaloTrendMusicService.BuildCaption(request));
    }

    [Fact]
    public void Birthday_greeting_reads_like_a_friend_not_a_system_summary()
    {
        var text = ZaloBirthdayService.BuildFriendlyGreeting("Huyền Châu", "1928290555510447972");

        Assert.Contains("Huyền Châu", text);
        Assert.Contains("Sinh nhật", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bott nhớ", text);
        Assert.DoesNotContain("roster", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("poll", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tin nhắn", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Birthday_digest_groups_today_next_seven_days_and_soon()
    {
        var today = Birthday("today", "Huyền Châu", new DateOnly(2026, 11, 16), 0);
        var upcoming = Birthday("next", "Minh", new DateOnly(2026, 11, 20), 4);
        var soon = Birthday("soon", "An", new DateOnly(2026, 12, 1), 15);

        var text = ZaloBirthdayService.BuildDigestText([today], [upcoming], [soon], 7, 30);

        Assert.Contains("🎂 Hôm nay có sinh nhật nè 🎉", text);
        Assert.Contains("Huyền Châu — 16/11", text);
        Assert.Contains("Chúc Huyền Châu có một ngày sinh nhật thật vui nha ❤️", text);
        Assert.Contains("📅 Trong 7 ngày tới", text);
        Assert.Contains("Minh — 20/11 (còn 4 ngày)", text);
        Assert.Contains("🔜 Sắp tới", text);
        Assert.Contains("An — 01/12", text);
        Assert.DoesNotContain("Bott sẽ tự chúc", text);
        Assert.DoesNotContain("Lịch sinh nhật trong nhóm", text);
    }

    [Theory]
    [InlineData("//s120-26-ava-talk.zadn.vn/avatar.jpg", "https://s120-26-ava-talk.zadn.vn/avatar.jpg")]
    [InlineData("https://example.com/avatar.jpg", "https://example.com/avatar.jpg")]
    public void Birthday_avatar_normalizes_protocol_relative_url(string input, string expected)
    {
        Assert.Equal(expected, ZaloBirthdayService.NormalizeArtworkUrl(input));
    }

    private static ZaloBirthdayService.BirthdayProfile Birthday(
        string id,
        string displayName,
        DateOnly occurrence,
        int daysUntil)
    {
        var member = new ZaloBirthdayService.BirthdayCurrentMember(id, displayName, null, DateTimeOffset.UtcNow);
        var profile = new BridgeUserProfile(id, displayName, null, null, $"1995-{occurrence.Month:00}-{occurrence.Day:00}", null, false);
        return new ZaloBirthdayService.BirthdayProfile(member, profile, occurrence, occurrence, daysUntil);
    }
}
