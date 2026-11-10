using System.Net;
using System.Text.Json;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Infrastructure;

public sealed class ZaloTrendMusicRenderDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_owns_github_transport_and_preserves_render_inputs()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler);
        var dispatcher = new ZaloTrendMusicRenderDispatcher(new SingleClientFactory(client));

        await dispatcher.DispatchAsync(
            "github-token",
            "Longdotnet/Draft",
            "trend-music-render.yml",
            "main",
            "account-1",
            "group-1",
            "connection-1",
            "2026-11-10",
            "trend-music:connection-1:group-1:2026-11-10",
            "[{\"id\":\"candidate-1\"}]",
            CancellationToken.None);

        Assert.Equal(
            "/repos/Longdotnet/Draft/actions/workflows/trend-music-render.yml/dispatches",
            handler.Path);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("github-token", handler.AuthorizationParameter);
        Assert.Equal("2022-11-28", handler.ApiVersion);

        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal("main", root.GetProperty("ref").GetString());
        var inputs = root.GetProperty("inputs");
        Assert.Equal("account-1", inputs.GetProperty("account_id").GetString());
        Assert.Equal("group-1", inputs.GetProperty("group_id").GetString());
        Assert.Equal("connection-1", inputs.GetProperty("connection_id").GetString());
        Assert.Equal("2026-11-10", inputs.GetProperty("local_date").GetString());
        Assert.Equal(
            "trend-music:connection-1:group-1:2026-11-10",
            inputs.GetProperty("idempotency_key").GetString());
        Assert.Equal("[{\"id\":\"candidate-1\"}]", inputs.GetProperty("candidates_json").GetString());
    }

    [Fact]
    public async Task WakeQueueWorkerAsync_dispatches_without_direct_render_inputs()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler);
        var dispatcher = new ZaloTrendMusicRenderDispatcher(new SingleClientFactory(client));

        await dispatcher.WakeQueueWorkerAsync(
            "github-token",
            "Longdotnet/Draft",
            "trend-music-render.yml",
            "main",
            CancellationToken.None);

        Assert.Equal(
            "/repos/Longdotnet/Draft/actions/workflows/trend-music-render.yml/dispatches",
            handler.Path);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal("main", root.GetProperty("ref").GetString());
        Assert.False(root.TryGetProperty("inputs", out _));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("TrendMusicGitHub", name);
            return client;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? ApiVersion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            ApiVersion = request.Headers.TryGetValues("X-GitHub-Api-Version", out var values)
                ? Assert.Single(values)
                : null;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
