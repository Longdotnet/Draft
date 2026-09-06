using System.Net;
using System.Text;
using System.Text.Json;
using VolleyDraft.Api.Services;

namespace VolleyDraft.Api.Tests.Zalo.Infrastructure;

public sealed class ZaloBridgeFailureProvenanceTests
{
    [Fact]
    public async Task Edge429_IsSanitized_AndMarkedAsNotReachingBridge()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("<html>proxy secret body</html>", Encoding.UTF8, "text/html")
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<ZaloBridgeRequestException>(() => client.GetGroupsAsync(Credentials()));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.False(exception.BridgeReached);
        Assert.Contains("ZALO-BRIDGE-EDGE-429", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy secret body", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bridge429_PreservesBridgeRequestId_WithoutEchoingProviderDetails()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"Zalo upstream request was rate-limited.\"}", Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("x-volley-bridge-response", "1");
            response.Headers.TryAddWithoutValidation("x-volley-bridge-request-id", "bridge-request-42");
            return response;
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<ZaloBridgeRequestException>(() => client.GetGroupsAsync(Credentials()));

        Assert.True(exception.BridgeReached);
        Assert.Equal("bridge-request-42", exception.RequestId);
        Assert.Contains("ZALO-BRIDGE-APP-429", exception.Message, StringComparison.Ordinal);
    }

    private static ZaloBridgeClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://bridge.test/") });

    private static JsonElement Credentials() => JsonDocument.Parse("""
        {"imei":"imei","userAgent":"agent","cookie":[]}
        """).RootElement.Clone();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responseFactory(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
