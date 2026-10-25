using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Security;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ClientIpAccessPolicyTests
{
    [Fact]
    public void AllowedIpFromCloudflareHeaderIsAccepted()
    {
        var policy = CreatePolicy("203.0.113.10/32");
        var context = new DefaultHttpContext();
        context.Request.Headers["CF-Connecting-IP"] = "203.0.113.10";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        Assert.True(policy.IsAllowed(context));
    }

    [Fact]
    public void DifferentCloudflareIpIsRejectedEvenWhenCallerSpoofsXForwardedFor()
    {
        var policy = CreatePolicy("203.0.113.10/32");
        var context = new DefaultHttpContext();
        context.Request.Headers["CF-Connecting-IP"] = "198.51.100.44";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        Assert.False(policy.IsAllowed(context));
    }

    [Fact]
    public void SpoofedXForwardedForWithoutCloudflareHeaderIsRejected()
    {
        var policy = CreatePolicy("203.0.113.10/32");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        Assert.False(policy.IsAllowed(context));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/scheduler")]
    [InlineData("/api/public/bot-images/asset-1")]
    [InlineData("/api/public/sessions/session-1/team-card.png")]
    public void InfrastructureGetPathsBypassBrowserIpGate(string path)
    {
        var context = CreateRequest(path, HttpMethods.Get);
        Assert.True(ClientIpAccessPolicy.IsInfrastructureRequest(context));
    }

    [Theory]
    [InlineData("/api/internal/scheduler/tick")]
    [InlineData("/api/internal/zalo/events")]
    [InlineData("/api/internal/zalo/membership-events")]
    [InlineData("/api/internal/zalo/presence-events")]
    [InlineData("/api/internal/zalo/poll-events")]
    public void ExactInternalPostPathsBypassBrowserIpGate(string path)
    {
        var context = CreateRequest(path, HttpMethods.Post);
        Assert.True(ClientIpAccessPolicy.IsInfrastructureRequest(context));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/register")]
    [InlineData("/api/public/sessions")]
    [InlineData("/api/public/sessions/session-1/start-draft")]
    [InlineData("/api/sessions")]
    [InlineData("/api/zalo/connections")]
    [InlineData("/api/internal/unknown")]
    public void UserFacingGetPathsRequireAllowedIp(string path)
    {
        var context = CreateRequest(path, HttpMethods.Get);
        Assert.False(ClientIpAccessPolicy.IsInfrastructureRequest(context));
    }

    [Theory]
    [InlineData("/api/public/bot-images/asset-1")]
    [InlineData("/api/public/sessions/session-1/team-card.png")]
    public void PublicMediaWriteMethodsDoNotBypassIpGate(string path)
    {
        var context = CreateRequest(path, HttpMethods.Post);
        Assert.False(ClientIpAccessPolicy.IsInfrastructureRequest(context));
    }

    [Fact]
    public void UnknownInternalPostPathDoesNotBypassIpGate()
    {
        var context = CreateRequest("/api/internal/unknown", HttpMethods.Post);
        Assert.False(ClientIpAccessPolicy.IsInfrastructureRequest(context));
    }

    private static ClientIpAccessPolicy CreatePolicy(string allowedCidr)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AccessControl:Enabled"] = "true",
                ["AccessControl:AllowedCidrs:0"] = allowedCidr
            })
            .Build();

        return new ClientIpAccessPolicy(configuration);
    }

    private static DefaultHttpContext CreateRequest(string path, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        return context;
    }
}
