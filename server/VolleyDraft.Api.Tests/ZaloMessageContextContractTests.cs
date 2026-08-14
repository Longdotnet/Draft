using System.Text.Json;
using VolleyDraft.Api.Contracts;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMessageContextContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Incoming_message_deserializes_quote_context()
    {
        const string json = """
            {
              "accountId":"bot-account",
              "botId":"bot-account",
              "groupId":"group-1",
              "messageId":"m2",
              "senderId":"u2",
              "senderName":"Tùng",
              "content":"ông này hả?",
              "mentions":[],
              "mentionedBot":false,
              "sentAtUnixMs":1720000001000,
              "quote":{
                "messageId":"m1",
                "senderId":"u1",
                "senderName":"Long",
                "content":"Long đánh libero nha",
                "messageType":"1",
                "sentAtUnixMs":1720000000000,
                "attachment":null
              }
            }
            """;

        var incoming = JsonSerializer.Deserialize<ZaloIncomingMessageEvent>(json, JsonOptions);

        Assert.NotNull(incoming);
        Assert.NotNull(incoming.Quote);
        Assert.Equal("m1", incoming.Quote.MessageId);
        Assert.Equal("u1", incoming.Quote.SenderId);
        Assert.Equal("Long", incoming.Quote.SenderName);
        Assert.Equal("Long đánh libero nha", incoming.Quote.Content);
    }

    [Fact]
    public void Legacy_incoming_message_without_quote_remains_compatible()
    {
        const string json = """
            {
              "accountId":"bot-account",
              "botId":"bot-account",
              "groupId":"group-1",
              "messageId":"m1",
              "senderId":"u1",
              "senderName":"Long",
              "content":"@bot help",
              "mentions":[],
              "mentionedBot":true,
              "sentAtUnixMs":1720000000000
            }
            """;

        var incoming = JsonSerializer.Deserialize<ZaloIncomingMessageEvent>(json, JsonOptions);

        Assert.NotNull(incoming);
        Assert.Null(incoming.Quote);
    }
}
