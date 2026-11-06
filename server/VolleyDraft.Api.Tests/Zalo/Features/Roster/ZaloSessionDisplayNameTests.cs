using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Features.Roster;

public sealed class ZaloSessionDisplayNameTests
{
    [Fact]
    public void WithCanonicalStartTime_ReplacesStaleClockEmbeddedInSessionName()
    {
        var startTime = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        var result = ZaloSessionDisplayName.WithCanonicalStartTime(
            "T5 05/11 17:30 - Thứ 5",
            startTime);

        Assert.Equal("T5 05/11 19:00 - Thứ 5", result);
    }

    [Fact]
    public void WithCanonicalStartTime_LeavesNamesWithoutEmbeddedClockUnchanged()
    {
        var startTime = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        var result = ZaloSessionDisplayName.WithCanonicalStartTime("T5 05/11 - Thứ 5", startTime);

        Assert.Equal("T5 05/11 - Thứ 5", result);
    }

    [Fact]
    public void WithCanonicalStartTime_LeavesNameUnchangedWhenStartTimeIsMissing()
    {
        var result = ZaloSessionDisplayName.WithCanonicalStartTime("T5 05/11 17:30 - Thứ 5", null);

        Assert.Equal("T5 05/11 17:30 - Thứ 5", result);
    }
}
