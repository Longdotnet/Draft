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
    public void Birthday_memory_uses_message_content_as_a_safe_topic_and_ignores_links_or_bot_commands()
    {
        var messages = new[]
        {
            "@Bott test nhạc trend",
            "https://example.com/san-bong",
            "Chiều nay kèo sân cũ nha anh em, đánh 3 set luôn"
        };

        Assert.True(ZaloBirthdayService.TrySelectBirthdayMemory(messages, out var memory));
        Assert.Equal("hay nhắc chuyện kèo/sân trong group", memory);
    }
}
