using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloExplicitDateSessionSelectionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 16, 35, 0, TimeSpan.FromHours(7));

    private static readonly ZaloSessionReference[] Sessions =
    [
        new("cn-30", "CN 30/8", new DateTimeOffset(2026, 8, 30, 17, 30, 0, TimeSpan.FromHours(7))),
        new("cn-23", "CN 23/8", new DateTimeOffset(2026, 8, 23, 17, 30, 0, TimeSpan.FromHours(7))),
        new("cn-16", "CN 16/8", new DateTimeOffset(2026, 8, 16, 17, 30, 0, TimeSpan.FromHours(7)))
    ];

    [Fact]
    public void Explicit_calendar_date_only_keeps_the_matching_session()
    {
        var result = ZaloBotIntelligence.SelectOperationalSessionCandidateIds("30/8", Sessions, Now);

        Assert.Equal(["cn-30"], result);
    }

    [Fact]
    public void Explicit_historical_date_still_allows_history_lookup()
    {
        var result = ZaloBotIntelligence.SelectOperationalSessionCandidateIds("16/8", Sessions, Now);

        Assert.Equal(["cn-16"], result);
    }
}
