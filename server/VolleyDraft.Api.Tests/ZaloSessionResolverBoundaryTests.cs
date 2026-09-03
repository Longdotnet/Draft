using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSessionResolverBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 8, 40, 0, TimeSpan.Zero); // 15:40 VN

    private static readonly IReadOnlyList<ZaloSessionReference> SameDaySessions =
    [
        new("early", "T4 02/09 17:30", new DateTimeOffset(2026, 9, 2, 10, 30, 0, TimeSpan.Zero)),
        new("late", "T4 02/09 19:00", new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero))
    ];

    [Theory]
    [InlineData("chia team kèo 02/09 lúc 19:00", "late")]
    [InlineData("xem kèo 02/09 lúc 17h30 giúp tui", "early")]
    public void Date_and_time_inside_natural_text_disambiguates_same_day_sessions(string text, string expectedId)
    {
        var result = ZaloConversationCore.ResolveSessionReference(text, SameDaySessions, Now);

        Assert.Equal([expectedId], result);
    }
}
