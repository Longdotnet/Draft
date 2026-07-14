using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloNaturalCommandTests
{
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
