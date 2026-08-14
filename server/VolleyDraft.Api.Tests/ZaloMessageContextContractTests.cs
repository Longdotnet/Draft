using System.Text.Json;
using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
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
        Assert.False(incoming.MentionedBot);
    }

    [Fact]
    public void Direct_reply_to_bot_is_treated_as_explicit_address_without_changing_question_text()
    {
        const string json = """
            {
              "accountId":"bot-account",
              "botId":"bot-account",
              "groupId":"group-1",
              "messageId":"m3",
              "senderId":"u1",
              "senderName":"Long",
              "content":"T6",
              "mentions":[],
              "mentionedBot":false,
              "sentAtUnixMs":1720000002000,
              "quote":{
                "messageId":"bot-message-1",
                "senderId":"bot-account",
                "senderName":"Volley Bot",
                "content":"Bạn muốn trận nào?",
                "messageType":"chat",
                "sentAtUnixMs":1720000001000,
                "attachment":null
              }
            }
            """;

        var incoming = JsonSerializer.Deserialize<ZaloIncomingMessageEvent>(json, JsonOptions);

        Assert.NotNull(incoming);
        Assert.True(incoming.MentionedBot);
        var marker = Assert.Single(incoming.Mentions, mention => mention.Uid == "bot-account");
        Assert.Equal(0, marker.Len);
        Assert.Equal("T6", ZaloBotService.ExtractQuestion(incoming));
    }

    [Fact]
    public void Reply_to_another_member_does_not_address_bot()
    {
        const string json = """
            {
              "accountId":"bot-account",
              "botId":"bot-account",
              "groupId":"group-1",
              "messageId":"m4",
              "senderId":"u1",
              "senderName":"Long",
              "content":"T6",
              "mentions":[],
              "mentionedBot":false,
              "sentAtUnixMs":1720000002000,
              "quote":{
                "messageId":"member-message-1",
                "senderId":"u2",
                "senderName":"Tùng",
                "content":"T6 đi",
                "messageType":"chat",
                "sentAtUnixMs":1720000001000,
                "attachment":null
              }
            }
            """;

        var incoming = JsonSerializer.Deserialize<ZaloIncomingMessageEvent>(json, JsonOptions);

        Assert.NotNull(incoming);
        Assert.False(incoming.MentionedBot);
        Assert.DoesNotContain(incoming.Mentions, mention => mention.Uid == "bot-account");
        Assert.Equal("T6", ZaloBotService.ExtractQuestion(incoming));
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
        Assert.True(incoming.MentionedBot);
    }
}
