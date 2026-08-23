using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloGreetingBackgroundPolicyTests
{
    [Fact]
    public void Morning_test_backgrounds_do_not_include_retired_five()
    {
        Assert.Equal([1, 2, 3, 4], ZaloGreetingTestPolicy.BackgroundIds(ZaloDailyGreetingKind.Morning));
    }

    [Fact]
    public void Night_test_backgrounds_keep_the_night_catalog()
    {
        Assert.Equal(
            ZaloNightGreetingBackgroundCatalog.ActiveIds,
            ZaloGreetingTestPolicy.BackgroundIds(ZaloDailyGreetingKind.Night));
    }
}
