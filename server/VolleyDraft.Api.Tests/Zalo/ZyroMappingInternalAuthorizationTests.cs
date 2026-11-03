using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Security;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo;

public sealed class ZyroMappingInternalAuthorizationTests
{
    [Fact]
    public void Accepts_only_the_configured_long_internal_key()
    {
        const string secret = "zyro-mapping-internal-secret-with-at-least-32-characters";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZyroMapping:InternalKey"] = secret
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[ZyroMappingInternalAuthorization.HeaderName] = secret;

        Assert.True(ZyroMappingInternalAuthorization.IsAuthorized(context, configuration));

        context.Request.Headers[ZyroMappingInternalAuthorization.HeaderName] = secret + "x";
        Assert.False(ZyroMappingInternalAuthorization.IsAuthorized(context, configuration));

        context.Request.Headers.Remove(ZyroMappingInternalAuthorization.HeaderName);
        Assert.False(ZyroMappingInternalAuthorization.IsAuthorized(context, configuration));
    }

    [Fact]
    public void Fails_closed_when_the_internal_key_is_not_configured_or_is_too_short()
    {
        var emptyConfiguration = new ConfigurationBuilder().Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[ZyroMappingInternalAuthorization.HeaderName] = "short";
        Assert.False(ZyroMappingInternalAuthorization.IsConfigured(emptyConfiguration));
        Assert.False(ZyroMappingInternalAuthorization.IsAuthorized(context, emptyConfiguration));

        var shortConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZyroMapping:InternalKey"] = "short"
            })
            .Build();
        Assert.False(ZyroMappingInternalAuthorization.IsConfigured(shortConfiguration));
        Assert.False(ZyroMappingInternalAuthorization.IsAuthorized(context, shortConfiguration));

        var validConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZyroMapping:InternalKey"] = new string('x', 32)
            })
            .Build();
        Assert.True(ZyroMappingInternalAuthorization.IsConfigured(validConfiguration));

        var whitespaceConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZyroMapping:InternalKey"] = new string(' ', 32)
            })
            .Build();
        Assert.False(ZyroMappingInternalAuthorization.IsConfigured(whitespaceConfiguration));
        context.Request.Headers[ZyroMappingInternalAuthorization.HeaderName] = new string(' ', 32);
        Assert.False(ZyroMappingInternalAuthorization.IsAuthorized(context, whitespaceConfiguration));
    }
}
