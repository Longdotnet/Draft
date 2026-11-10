using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Security;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo;

public sealed class TrendMusicInternalAuthorizationTests
{
    [Fact]
    public void Accepts_only_the_configured_long_callback_key()
    {
        const string secret = "trend-music-callback-secret-with-at-least-32-characters";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:TrendMusic:CallbackKey"] = secret
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[TrendMusicInternalAuthorization.HeaderName] = secret;

        Assert.True(TrendMusicInternalAuthorization.IsConfigured(configuration));
        Assert.True(TrendMusicInternalAuthorization.IsAuthorized(context, configuration));

        context.Request.Headers[TrendMusicInternalAuthorization.HeaderName] = secret + "x";
        Assert.False(TrendMusicInternalAuthorization.IsAuthorized(context, configuration));

        context.Request.Headers.Remove(TrendMusicInternalAuthorization.HeaderName);
        Assert.False(TrendMusicInternalAuthorization.IsAuthorized(context, configuration));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("                                ")]
    public void Fails_closed_when_callback_key_is_missing_short_or_whitespace(string secret)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:TrendMusic:CallbackKey"] = secret
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[TrendMusicInternalAuthorization.HeaderName] = secret;

        Assert.False(TrendMusicInternalAuthorization.IsConfigured(configuration));
        Assert.False(TrendMusicInternalAuthorization.IsAuthorized(context, configuration));
    }

    [Fact]
    public void Falls_back_to_existing_scheduler_key_when_dedicated_key_is_absent()
    {
        const string schedulerKey = "existing-scheduler-secret-with-at-least-32-characters";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduler:Key"] = schedulerKey
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[TrendMusicInternalAuthorization.HeaderName] = schedulerKey;

        Assert.True(TrendMusicInternalAuthorization.IsConfigured(configuration));
        Assert.True(TrendMusicInternalAuthorization.IsAuthorized(context, configuration));
    }

    [Fact]
    public void Dedicated_key_takes_precedence_over_scheduler_fallback()
    {
        const string dedicated = "dedicated-trend-secret-with-at-least-32-characters";
        const string schedulerKey = "existing-scheduler-secret-with-at-least-32-characters";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:Ambient:TrendMusic:CallbackKey"] = dedicated,
                ["Scheduler:Key"] = schedulerKey
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[TrendMusicInternalAuthorization.HeaderName] = schedulerKey;
        Assert.False(TrendMusicInternalAuthorization.IsAuthorized(context, configuration));

        context.Request.Headers[TrendMusicInternalAuthorization.HeaderName] = dedicated;
        Assert.True(TrendMusicInternalAuthorization.IsAuthorized(context, configuration));
    }
}
