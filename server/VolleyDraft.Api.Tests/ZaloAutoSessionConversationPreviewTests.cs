using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionConversationPreviewTests
{
    [Fact]
    public void OrganizerPreview_DoesNotExposeInternalSetConfiguration()
    {
        var poll = new BridgePoll(
            "poll-1",
            "Lịch tuần này",
            "captain",
            [new BridgePollOption("o1", "T6 17h30", 9, [])],
            true,
            false,
            false,
            false,
            9,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            0);
        var candidate = new ZaloAutoSessionCandidate(
            "o1",
            "T6 17h30",
            "T6",
            new DateTimeOffset(2026, 8, 21, 17, 30, 0, TimeSpan.FromHours(7)),
            9);

        var preview = ZaloAutoSessionV2Service.BuildOrganizerPreview(
            poll,
            [candidate],
            3,
            6,
            4,
            "Sân UTE",
            ZaloAutoSessionRolloutMode.Live);

        Assert.DoesNotContain("4 set", preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" set", preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("đội ×", preview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9/18 người", preview);
        Assert.Contains("Sân UTE", preview);
        Assert.Contains("CHƯA", preview, StringComparison.OrdinalIgnoreCase);
    }
}
