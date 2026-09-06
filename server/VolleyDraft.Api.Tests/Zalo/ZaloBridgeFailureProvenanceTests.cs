using System.Net;
using System.Text;
using System.Text.Json;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo;

public sealed class ZaloBridgeFailureProvenanceTests
{
    [Fact]
    public async Task GetGroupsAsync_429WithoutBridgeMarker_IsClassifiedAsEdgeAndDoesNotLeakBody()
    {
        var handler = new StubHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StringContent("Too Many Requests at secret-edge-host.internal", Encoding.UTF8, "text/plain")
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "12");
            return response;
        });
        var client = new ZaloBridgeClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });

        var exception = await Assert.ThrowsAsync<ZaloBridgeRequestException>(
            () => client.GetGroupsAsync(Credentials()));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.False(exception.BridgeReached);
        Assert.Equal("12", exception.RetryAfter);
        Assert.Equal("/v1/groups", exception.Operation);
        Assert.Contains("ZALO-BRIDGE-EDGE-429", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-edge-host", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Too Many Requests", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetGroupsAsync_429WithBridgeMarker_IsClassifiedAsBridgeApplication()
    {
        var handler = new StubHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StringContent("{\"error\":\"internal rate detail\"}", Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("x-volley-bridge-response", "1");
            response.Headers.TryAddWithoutValidation("x-volley-bridge-request-id", "bridge-request-123");
            return response;
        });
        var client = new ZaloBridgeClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });

        var exception = await Assert.ThrowsAsync<ZaloBridgeRequestException>(
            () => client.GetGroupsAsync(Credentials()));

        Assert.True(exception.BridgeReached);
        Assert.Equal("bridge-request-123", exception.RequestId);
        Assert.Contains("ZALO-BRIDGE-APP-429", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("internal rate detail", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetGroupsAsync_ProxyFailureWithoutMarker_NeverEchoesArbitraryBody()
    {
        var handler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            RequestMessage = request,
            Content = new StringContent("<html>proxy secret deployment host</html>", Encoding.UTF8, "text/html")
        });
        var client = new ZaloBridgeClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });

        var exception = await Assert.ThrowsAsync<ZaloBridgeRequestException>(
            () => client.GetGroupsAsync(Credentials()));

        Assert.False(exception.BridgeReached);
        Assert.Contains("ZALO-BRIDGE-EDGE-502", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Credentials()
    {
        using var document = JsonDocument.Parse("{\"imei\":\"imei\",\"userAgent\":\"ua\",\"cookie\":[]}");
        return document.RootElement.Clone();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
