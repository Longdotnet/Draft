using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloReminderDateBoundaryTests
{
    private static readonly ZaloReminderCommand BasicSchedule =
        new(ZaloReminderCommandKind.Schedule, null, false);

    [Fact]
    public void Yearless_date_after_new_year_rolls_to_the_next_calendar_year()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "đặt lịch nhắc 5h chiều 2/1 nhắc mọi người nhớ vote",
            BasicSchedule,
            new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(new DateOnly(2027, 1, 2), command.ExplicitLocalDate);
    }

    [Fact]
    public void Yearless_future_date_in_the_current_year_stays_in_the_current_year()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "đặt lịch nhắc 5h chiều 15/7 nhắc mọi người nhớ vote",
            BasicSchedule,
            new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(new DateOnly(2026, 7, 15), command.ExplicitLocalDate);
    }

    [Fact]
    public void Yearless_leap_day_resolves_to_the_next_valid_occurrence()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "đặt lịch nhắc 5h chiều 29/2 nhắc mọi người nhớ vote",
            BasicSchedule,
            new DateTimeOffset(2027, 3, 1, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(new DateOnly(2028, 2, 29), command.ExplicitLocalDate);
    }

    [Fact]
    public void Explicit_year_is_authoritative_even_when_the_date_is_in_the_past()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "đặt lịch nhắc 5h chiều 2/1/2026 nhắc mọi người nhớ vote",
            BasicSchedule,
            new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(new DateOnly(2026, 1, 2), command.ExplicitLocalDate);
    }

    [Fact]
    public void Invalid_calendar_date_remains_unresolved()
    {
        var command = ZaloNaturalCommandParser.EnrichReminder(
            "đặt lịch nhắc 5h chiều 31/2 nhắc mọi người nhớ vote",
            BasicSchedule,
            new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(7)));

        Assert.Null(command.ExplicitLocalDate);
    }
}
