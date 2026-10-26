using System.Net;
using System.Text;
using System.Text.Json;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Zalo.Infrastructure;

public sealed class ZaloBridgeClientLongMessageTests
{
    [Fact]
    public async Task SendGroupMessage_chunks_long_text_preserves_content_mentions_and_idempotency()
    {
        const string mentionLabel = "@Thanh Long";
        var bodyText = string.Join("\n", Enumerable.Range(1, 90)
            .Select(index => $"{index}. Đây là một đoạn tóm tắt câu chuyện đủ dài để kiểm tra việc chia tin nhắn Zalo mà không làm mất nội dung."));
        var message = $"{mentionLabel} {bodyText}";
        var handler = new RecordingHandler();
        var client = new ZaloBridgeClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });

        var result = await client.SendGroupMessageAsync(
            "bot-account",
            "group-1",
            message,
            [new BridgeOutgoingMention("sender-1", 0, mentionLabel.Length)],
            idempotencyKey: "bot-account:source-message");

        Assert.True(result.Sent);
        Assert.True(handler.Bodies.Count >= 3);

        var reconstructed = new StringBuilder();
        for (var index = 0; index < handler.Bodies.Count; index++)
        {
            using var document = JsonDocument.Parse(handler.Bodies[index]);
            var root = document.RootElement;
            var chunk = root.GetProperty("message").GetString()!;
            Assert.InRange(chunk.Length, 1, 1800);
            reconstructed.Append(chunk);

            var mentions = root.GetProperty("mentions");
            if (index == 0)
            {
                var mention = Assert.Single(mentions.EnumerateArray());
                Assert.Equal("sender-1", mention.GetProperty("uid").GetString());
                Assert.Equal(0, mention.GetProperty("pos").GetInt32());
                Assert.Equal(mentionLabel.Length, mention.GetProperty("len").GetInt32());
            }
            else
            {
                Assert.Empty(mentions.EnumerateArray());
            }

            Assert.Equal(
                $"bot-account:source-message:part:{index + 1}-of-{handler.Bodies.Count}",
                root.GetProperty("idempotencyKey").GetString());
        }

        Assert.Equal(message, reconstructed.ToString());
    }

    [Fact]
    public async Task SendGroupMessage_keeps_short_message_as_single_request_and_original_key()
    {
        const string idempotencyKey = "bot-account:short-message";
        var handler = new RecordingHandler();
        var client = new ZaloBridgeClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://bridge.test/")
        });

        var result = await client.SendGroupMessageAsync(
            "bot-account",
            "group-1",
            "@Long nội dung ngắn",
            [new BridgeOutgoingMention("sender-1", 0, 5)],
            idempotencyKey: idempotencyKey);

        Assert.True(result.Sent);
        var body = Assert.Single(handler.Bodies);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(idempotencyKey, document.RootElement.GetProperty("idempotencyKey").GetString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"sent\":true,\"mock\":true,\"messageId\":\"provider-{Bodies.Count}\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
