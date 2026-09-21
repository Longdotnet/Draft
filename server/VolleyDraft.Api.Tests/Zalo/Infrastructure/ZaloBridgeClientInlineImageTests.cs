using System.Net;
using System.Text;
using System.Text.Json;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Infrastructure;

public sealed class ZaloBridgeClientInlineImageTests
{
    [Fact]
    public async Task SendGroupMessage_serializes_inline_image_contract_without_changing_idempotency_key()
    {
        const string base64 = "iVBORw0KGgoAAAANSUhEUg==";
        const string idempotencyKey = "bot-account:message-42";
        var handler = new RecordingHandler();
        var client = new ZaloBridgeClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });

        var result = await client.SendGroupMessageAsync(
            "bot-account",
            "group-1",
            "Đội hình đã draft xong.",
            [],
            imageUrl: null,
            idempotencyKey: idempotencyKey,
            imageBase64: base64,
            imageContentType: "image/png",
            imageFileName: "court-index.png");

        Assert.True(result.Sent);
        Assert.NotNull(handler.Body);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal("/v1/group-messages", handler.Path);
        Assert.Equal(base64, root.GetProperty("imageBase64").GetString());
        Assert.Equal("image/png", root.GetProperty("imageContentType").GetString());
        Assert.Equal("court-index.png", root.GetProperty("imageFileName").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("imageUrl").ValueKind);
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
                    "{\"sent\":true,\"mock\":true,\"messageId\":\"provider-42\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
