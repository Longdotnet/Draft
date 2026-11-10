using System.Net;
using System.Text;
using System.Text.Json;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Infrastructure;

public sealed class ZaloBridgeClientVideoTests
{
    [Fact]
    public async Task SendGroupVideo_serializes_native_video_contract_without_changing_idempotency_key()
    {
        const string videoBase64 = "ZmFrZS1tcDQ=";
        const string thumbnailBase64 = "ZmFrZS1qcGVn";
        const string idempotencyKey = "trend-music:connection-1:group-1:2026-11-10:morning";
        var handler = new RecordingHandler();
        var client = new ZaloBridgeClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });

        var result = await client.SendGroupVideoAsync(
            "bot-account",
            "group-1",
            "NPC PICK",
            thumbnailBase64: thumbnailBase64,
            thumbnailFileName: "trend-thumb.jpg",
            videoBase64: videoBase64,
            videoFileName: "trend.mp4",
            durationMs: 20_000,
            width: 720,
            height: 1280,
            idempotencyKey: idempotencyKey);

        Assert.True(result.Sent);
        Assert.Equal("/v1/group-videos", handler.Path);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal(videoBase64, root.GetProperty("videoBase64").GetString());
        Assert.Equal("video/mp4", root.GetProperty("videoContentType").GetString());
        Assert.Equal(thumbnailBase64, root.GetProperty("thumbnailBase64").GetString());
        Assert.Equal("image/jpeg", root.GetProperty("thumbnailContentType").GetString());
        Assert.Equal(20_000, root.GetProperty("durationMs").GetInt32());
        Assert.Equal(720, root.GetProperty("width").GetInt32());
        Assert.Equal(1280, root.GetProperty("height").GetInt32());
        Assert.Equal(idempotencyKey, root.GetProperty("idempotencyKey").GetString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"sent\":true,\"mock\":true,\"messageId\":\"provider-video-42\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
