using System.Net;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Infrastructure;

public sealed class ZaloListenerRetryPolicyTests
{
    [Fact]
    public void ShouldRetryImmediately_RetriesOnlyShortLivedTransportFailures()
    {
        Assert.True(ZaloListenerRetryPolicy.ShouldRetryImmediately(new TaskCanceledException()));
        Assert.True(ZaloListenerRetryPolicy.ShouldRetryImmediately(new HttpRequestException("network")));
        Assert.True(ZaloListenerRetryPolicy.ShouldRetryImmediately(
            new HttpRequestException("timeout", null, HttpStatusCode.RequestTimeout)));
        Assert.True(ZaloListenerRetryPolicy.ShouldRetryImmediately(
            new HttpRequestException("upstream", null, HttpStatusCode.BadGateway)));

        Assert.False(ZaloListenerRetryPolicy.ShouldRetryImmediately(
            new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests)));
        Assert.False(ZaloListenerRetryPolicy.ShouldRetryImmediately(
            new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized)));
        Assert.False(ZaloListenerRetryPolicy.ShouldRetryImmediately(new System.Text.Json.JsonException("malformed")));
    }

    [Fact]
    public void DelayForAttempt_UsesBoundedExponentialDelayWithJitter()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(5_000), ZaloListenerRetryPolicy.DelayForAttempt(1, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(10_500), ZaloListenerRetryPolicy.DelayForAttempt(2, 500));
        Assert.Equal(TimeSpan.FromMilliseconds(21_000), ZaloListenerRetryPolicy.DelayForAttempt(3, 5_000));
        Assert.Equal(TimeSpan.FromMilliseconds(5_000), ZaloListenerRetryPolicy.DelayForAttempt(-5, -10));
    }
}
