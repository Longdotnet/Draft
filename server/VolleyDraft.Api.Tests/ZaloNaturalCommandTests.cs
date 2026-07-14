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
}
