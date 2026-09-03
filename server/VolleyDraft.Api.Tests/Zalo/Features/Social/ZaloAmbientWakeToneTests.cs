using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAmbientWakeToneTests
{
    [Fact]
    public void Plain_bot_call_uses_teammate_tone_not_customer_service_tone()
    {
        var reply = ZaloAmbientWakePhrase.BuildReply("Long");

        Assert.Contains("tui đây", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dạ", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("em đây", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cần em giúp", reply, StringComparison.OrdinalIgnoreCase);
    }
}
