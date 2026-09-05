using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionConversationPreviewTests
{
    [Fact]
    public void OrganizerPreview_UsesExplicitResolvedDateFromProductionPoll()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, TimeSpan.FromHours(7));
        var poll = new BridgePoll(
            "poll-ute-next-week",
            "Vote sân UTE tuần sau. Max 18 slots/sân. 28k/slot. 17:45-22:00",
            "captain",
            [new BridgePollOption("o1", "Chủ nhật 13/9", 2, [])],
            true,
            false,
            false,
            false,
            2,
            created.ToUnixTimeMilliseconds(),
            created.ToUnixTimeMilliseconds(),
            0);
        var candidates = ZaloPollScheduleParser.ExtractCandidates(
            poll,
            new ZaloTrackedGroupData(),
            new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.FromHours(7)));

        var preview = ZaloAutoSessionV2Service.BuildOrganizerPreview(
            poll,
            candidates,
            3,
            6,
            4,
            "Sân UTE",
            ZaloAutoSessionRolloutMode.Live);

        Assert.Contains("CN 13/09 17:45", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("CN 06/09 17:45", preview, StringComparison.Ordinal);
        Assert.Contains("2/18 người", preview, StringComparison.Ordinal);
    }

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