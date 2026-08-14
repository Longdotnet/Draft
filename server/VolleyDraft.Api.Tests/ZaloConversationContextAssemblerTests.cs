using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloConversationContextAssemblerTests
{
    [Fact]
    public void Assemble_prioritizes_same_sender_and_immediate_tail()
    {
        var start = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var messages = Enumerable.Range(0, 20)
            .Select(index => new ZaloAiMessage(
                "user",
                index is 2 or 7 or 12 ? "long" : $"other-{index}",
                index is 2 or 7 or 12 ? "Long" : $"Other {index}",
                index is 2 or 7 or 12 ? $"Long nói về T6 lần {index}" : $"chuyện không liên quan {index}",
                start.AddMinutes(index)))
            .ToList();

        var assembled = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("long", "Long"),
            "T6 này sao rồi?",
            messages,
            8);

        Assert.True(assembled.Count <= 8);
        Assert.Contains(assembled, message => message.SenderId == "long" && message.Content.Contains("lần 2"));
        Assert.Contains(assembled, message => message.SenderId == "long" && message.Content.Contains("lần 7"));
        Assert.Contains(assembled, message => message.SenderId == "long" && message.Content.Contains("lần 12"));
        Assert.Contains(assembled, message => message.Content == "chuyện không liên quan 19");
        Assert.Equal(assembled.OrderBy(message => message.SentAt).ToList(), assembled);
    }

    [Fact]
    public void Assemble_keeps_bot_reply_addressed_to_sender()
    {
        var start = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var messages = Enumerable.Range(0, 16)
            .Select(index => new ZaloAiMessage(
                "user",
                $"other-{index}",
                $"Other {index}",
                $"group chatter {index}",
                start.AddMinutes(index)))
            .ToList();
        messages[4] = new ZaloAiMessage("user", "long", "Long", "share slot với Tùng", start.AddMinutes(4));
        messages[5] = new ZaloAiMessage("assistant", "bot", "Bot", "@Long bạn muốn trận nào?", start.AddMinutes(5));

        var assembled = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("long", "Long"),
            "T6",
            messages,
            6);

        Assert.Contains(assembled, message => message.Content == "share slot với Tùng");
        Assert.Contains(assembled, message => message.Content == "@Long bạn muốn trận nào?");
    }

    [Fact]
    public void Assemble_isolates_old_context_between_two_users()
    {
        var start = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var messages = Enumerable.Range(0, 20)
            .Select(index => new ZaloAiMessage(
                "user",
                index % 2 == 0 ? "long" : "tung",
                index % 2 == 0 ? "Long" : "Tùng",
                index % 2 == 0 ? $"Long preference {index}" : $"Tùng preference {index}",
                start.AddMinutes(index)))
            .ToList();

        var forLong = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("long", "Long"),
            "preference",
            messages,
            6);
        var forTung = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("tung", "Tùng"),
            "preference",
            messages,
            6);

        Assert.True(forLong.Count(message => message.SenderId == "long") > forLong.Count(message => message.SenderId == "tung"));
        Assert.True(forTung.Count(message => message.SenderId == "tung") > forTung.Count(message => message.SenderId == "long"));
    }
}
