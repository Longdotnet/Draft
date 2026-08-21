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

    [Fact]
    public void Assemble_keeps_local_reference_chain_for_slot_do_question()
    {
        var start = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var messages = Enumerable.Range(0, 28)
            .Select(index => new ZaloAiMessage(
                "user",
                $"noise-{index}",
                $"Noise {index}",
                $"chuyện ngoài lề {index}",
                start.AddMinutes(index)))
            .ToList();

        messages[14] = new ZaloAiMessage("user", "a", "An", "Long tối nay đi không?", start.AddMinutes(14));
        messages[15] = new ZaloAiMessage("user", "long", "Long", "Chắc tui nghỉ", start.AddMinutes(15));
        messages[16] = new ZaloAiMessage("user", "b", "Bình", "vậy slot đó Nam vô được không?", start.AddMinutes(16));

        var assembled = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("b", "Bình"),
            "vậy slot đó Nam vô được không?",
            messages,
            8);

        Assert.Contains(assembled, message => message.Content == "Long tối nay đi không?");
        Assert.Contains(assembled, message => message.Content == "Chắc tui nghỉ");
        Assert.Contains(assembled, message => message.Content == "vậy slot đó Nam vô được không?");
    }

    [Fact]
    public void Assemble_prioritizes_named_participant_when_question_mentions_them()
    {
        var start = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var messages = Enumerable.Range(0, 30)
            .Select(index => new ZaloAiMessage(
                "user",
                $"u-{index}",
                $"Member {index}",
                $"nội dung {index}",
                start.AddMinutes(index)))
            .ToList();

        messages[5] = new ZaloAiMessage("user", "nam", "Nam", "tui đang chờ có slot", start.AddMinutes(5));
        messages[7] = new ZaloAiMessage("user", "long", "Long", "tui có slot nhưng có thể nghỉ", start.AddMinutes(7));

        var assembled = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("b", "Bình"),
            "Nam có vào slot đó được không?",
            messages,
            8);

        Assert.Contains(assembled, message => message.SenderId == "nam");
    }

    [Fact]
    public void Assemble_never_exceeds_requested_budget_even_for_referential_questions()
    {
        var start = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var messages = Enumerable.Range(0, 40)
            .Select(index => new ZaloAiMessage(
                "user",
                index % 3 == 0 ? "long" : $"other-{index}",
                index % 3 == 0 ? "Long" : $"Other {index}",
                $"slot team trận chuyện {index}",
                start.AddMinutes(index)))
            .ToList();

        var assembled = ZaloConversationContextAssembler.Assemble(
            new ZaloAiSender("long", "Long"),
            "vậy cái slot đó thì sao?",
            messages,
            7);

        Assert.Equal(7, assembled.Count);
        Assert.Equal(assembled.OrderBy(message => message.SentAt).ToList(), assembled);
    }
}
