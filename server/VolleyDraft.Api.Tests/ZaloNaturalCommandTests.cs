using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloNaturalCommandTests
{
    [Theory]
    [InlineData("có thể đưa danh sách lịch nhắc cho tui coi được không?")]
    [InlineData("liệt kê các lịch nhắc hiện tại")]
    [InlineData("bot đang có lịch nhắc nào vậy?")]
    [InlineData("lịch nhắc cho thứ 6 cách 8h hiện tại đâu?")]
    public void Reminder_parser_recognizes_natural_status_questions(string question)
    {
        Assert.True(ZaloBotIntelligence.TryParseReminderCommand(question, out var command));
        Assert.Equal(ZaloReminderCommandKind.Status, command.Kind);
    }

    [Fact]
    public void Reminder_parser_recognizes_update_instead_of_creating_another_schedule()
    {
        Assert.True(ZaloBotIntelligence.TryParseReminderCommand(
            "thay đổi lịch nhắc thứ 4 5h chiều ngày mai, thay vì nhắc cả nhóm chỉ nhắc cho những người tham gia vote",
            out var basic));
        Assert.Equal(ZaloReminderCommandKind.Update, basic.Kind);

        var command = ZaloNaturalCommandParser.EnrichReminder(
            "thay đổi lịch nhắc thứ 4 5h chiều ngày mai, thay vì nhắc cả nhóm chỉ nhắc cho những người tham gia vote",
            basic,
            new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(ZaloReminderAudience.Roster, command.Audience);
        Assert.Equal(new TimeOnly(17, 0), command.LocalTime);
        Assert.Equal(new DateOnly(2026, 7, 15), command.ExplicitLocalDate);
        Assert.Null(command.CustomMessage);
    }

    [Fact]
    public void Reminder_parser_reads_clock_three_sessions_and_custom_message()
    {
        var basic = new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, 300, true);

        var command = ZaloNaturalCommandParser.EnrichReminder(
            "hãy thực hiện thông báo 5h chiều cho T4, T6, CN nhắc nhở mọi người nhớ lên sân và đem theo nước",
            basic,
            new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(new TimeOnly(17, 0), command.LocalTime);
        Assert.Null(command.DelayMinutes);
        Assert.False(command.Repeats);
        Assert.True(command.UseSessionDate);
        Assert.Equal(["t4", "t6", "cn"], command.SessionReferences);
        Assert.Equal("nhớ lên sân và đem theo nước", command.CustomMessage);
        Assert.Equal(ZaloReminderAudience.All, command.Audience);
        Assert.False(command.OnlyIfMissingSlots);
    }

    [Fact]
    public void Reminder_parser_targets_roster_and_understands_tomorrow()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "mai lúc 5h chiều nhắc những người trong team nhớ mang nước",
            new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, 300, true),
            new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(new DateOnly(2026, 7, 15), command.ExplicitLocalDate);
        Assert.Equal(new TimeOnly(17, 0), command.LocalTime);
        Assert.Equal(ZaloReminderAudience.Roster, command.Audience);
        Assert.Equal("nhớ mang nước", command.CustomMessage);
        Assert.False(command.OnlyIfMissingSlots);
    }

    [Fact]
    public void Reminder_parser_removes_time_and_audience_preamble_from_custom_message()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "tạo lịch nhắc nhở 5h chiều mai cho thứ 4, nhắc những người vote và share slot nhớ tham gia và mang nước theo",
            new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, null, false),
            new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(new TimeOnly(17, 0), command.LocalTime);
        Assert.Equal(new DateOnly(2026, 7, 15), command.ExplicitLocalDate);
        Assert.Equal(ZaloReminderAudience.Roster, command.Audience);
        Assert.Equal("nhớ tham gia và mang nước theo", command.CustomMessage);
    }

    [Fact]
    public void Reminder_parser_removes_wrapping_quotes_from_message()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "đặt lịch nhắc 5h chiều ngày mai cho thứ 4 nhắc mọi người \" nhớ mang nước và tham gia \"",
            new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, null, false),
            new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal("nhớ mang nước và tham gia", command.CustomMessage);
    }

    [Fact]
    public void Reminder_parser_targets_participants_and_not_the_whole_group()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "tạo lịch nhắc ngày mai T4 5h chiều, tag thành viên đã tham gia vào ngày đó thôi",
            new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, null, false),
            new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(ZaloReminderAudience.Roster, command.Audience);
        Assert.Equal(new TimeOnly(17, 0), command.LocalTime);
        Assert.Equal(new DateOnly(2026, 7, 15), command.ExplicitLocalDate);
    }

    [Fact]
    public void Payment_qr_reminder_can_run_after_the_match_and_never_depends_on_missing_slots()
    {
        var question = "lên schedular vào lúc 9h tối thứ 4, bạn sẽ gửi QR thanh toán của thứ 4 vào nhóm này, và tag những người đã tham gia";
        Assert.True(ZaloBotIntelligence.TryParseReminderCommand(question, out var basic));
        var command = ZaloNaturalCommandParser.EnrichReminder(
            question,
            basic,
            new DateTimeOffset(2026, 7, 15, 0, 10, 0, TimeSpan.FromHours(7)));

        Assert.Equal(new TimeOnly(21, 0), command.LocalTime);
        Assert.Equal(ZaloReminderAudience.Roster, command.Audience);
        Assert.True(command.UseSessionDate);
        Assert.True(command.IncludePaymentQr);
        Assert.True(command.AllowAfterSessionStart);
        Assert.False(command.OnlyIfMissingSlots);
        Assert.False(command.StopWhenFull);
    }

    [Fact]
    public void Reminder_parser_targets_the_whole_group_when_user_says_everyone()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "đặt scheduler cứ cách 6h tag mỗi người trong nhóm để vote thứ 6",
            new ZaloReminderCommand(ZaloReminderCommandKind.Schedule, 360, true),
            new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(ZaloReminderAudience.All, command.Audience);
        Assert.True(command.Repeats);
        Assert.Equal(360, command.DelayMinutes);
    }

    [Fact]
    public void Vote_reminder_stops_when_full_and_does_not_keep_scheduling_words_as_message()
    {
        var question = "tạo lịch nhắc vote thứ 6 cho mn cách 6h, khi nào đủ vote hoặc qua ngày thì thôi";
        Assert.True(ZaloBotIntelligence.TryParseReminderCommand(question, out var basic));

        var command = ZaloNaturalCommandParser.EnrichReminder(
            question,
            basic,
            new DateTimeOffset(2026, 7, 14, 20, 43, 0, TimeSpan.FromHours(7)));
        var message = ZaloNaturalCommandParser.SanitizeReminderMessage(command.CustomMessage, question);

        Assert.True(command.Repeats);
        Assert.Equal(360, command.DelayMinutes);
        Assert.True(command.OnlyIfMissingSlots);
        Assert.True(command.StopWhenFull);
        Assert.Equal("Mọi người vào vote giúp để buổi chơi sớm đủ người nhé!", message);
        Assert.DoesNotContain("cách 6h", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_raw_vote_message_is_cleaned_before_display_or_send()
    {
        var raw = "vote thứ 6 cho mọi người cách 6h, khi nào đủ vote hoặc qua ngày thì thôi";

        var result = ZaloNaturalCommandParser.SanitizeReminderMessage(raw, raw);

        Assert.Equal("Mọi người vào vote giúp để buổi chơi sớm đủ người nhé!", result);
        Assert.True(ZaloNaturalCommandParser.RequestsStopWhenFull(raw));
    }

    [Fact]
    public void Share_parser_requires_two_names_for_plus_two()
    {
        Assert.True(ZaloNaturalCommandParser.TryParseShareSlot(
            "Nick Tran xin +2 cho An và Bình",
            out var valid));
        Assert.Equal("Nick Tran", valid.Anchor);
        Assert.Equal(2, valid.RequestedPartnerCount);
        Assert.Equal(["An", "Bình"], valid.Partners);

        Assert.True(ZaloNaturalCommandParser.TryParseShareSlot(
            "Nick Tran xin +2 cho An",
            out var incomplete));
        Assert.Equal(2, incomplete.RequestedPartnerCount);
        Assert.Single(incomplete.Partners);
    }

    [Theory]
    [InlineData("@Nguyễn Thanh Tâm muốn rút nhường cho @Sin", "Nguyễn Thanh Tâm", "Sin")]
    [InlineData("@Nguyen Thanh Tam muốn hủy nhường cho @Sin", "Nguyen Thanh Tam", "Sin")]
    [InlineData("Thanh Tâm pass slot cho Sin", "Thanh Tâm", "Sin")]
    [InlineData("đội trưởng Thanh Tuyền báo bận muốn nhường slot cho Vivian", "Thanh Tuyền", "Vivian")]
    [InlineData("đội trưởng Nick Tran có việc muốn pass slot cho Sin", "Nick Tran", "Sin")]
    public void Slot_transfer_parser_extracts_the_player_giving_and_receiving_the_slot(
        string question,
        string fromPlayer,
        string toPlayer)
    {
        Assert.True(ZaloNaturalCommandParser.TryParseSlotTransfer(question, out var command));
        Assert.Equal(fromPlayer, command.FromPlayer);
        Assert.Equal(toPlayer, command.ToPlayer);
    }

    [Fact]
    public void Explicit_slot_transfer_mentions_bind_in_message_order()
    {
        var command = ZaloNaturalCommandParser.BindExplicitSlotTransferMentions(
            [
                new ZaloMentionedUser("tam-id", "Nguyễn Thanh Tâm"),
                new ZaloMentionedUser("sin-id", "Sin")
            ],
            null);

        Assert.NotNull(command);
        Assert.Equal("Nguyễn Thanh Tâm", command!.FromPlayer);
        Assert.Equal("Sin", command.ToPlayer);
        Assert.Equal("tam-id", command.FromZaloUserId);
        Assert.Equal("sin-id", command.ToZaloUserId);
    }

    [Theory]
    [InlineData("sửa share slot của Vivian từ Thanh Long sang Vinh cho T4", "Vivian", "Thanh Long", "Vinh", "t4")]
    [InlineData("đổi share slot Vivian từ Thanh Long sang Vinh Thứ 4", "Vivian", "Thanh Long", "Vinh", "thu 4")]
    public void Repair_share_parser_extracts_old_and_new_anchor(
        string question,
        string partner,
        string wrongAnchor,
        string correctAnchor,
        string sessionReference)
    {
        Assert.True(ZaloNaturalCommandParser.TryParseRepairShareSlot(question, out var command));
        Assert.Equal(partner, command.Partner);
        Assert.Equal(wrongAnchor, command.WrongAnchor);
        Assert.Equal(correctAnchor, command.CorrectAnchor);
        Assert.Equal(sessionReference, command.SessionReference);
    }
}
