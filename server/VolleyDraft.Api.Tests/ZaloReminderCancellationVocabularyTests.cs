using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloReminderCancellationVocabularyTests
{
    [Theory]
    [InlineData("hủy reminder")]
    [InlineData("huỷ reminder")]
    [InlineData("dừng reminder")]
    [InlineData("bỏ reminder")]
    [InlineData("tắt reminder")]
    [InlineData("hủy nhắc")]
    [InlineData("dừng nhắc")]
    [InlineData("bỏ nhắc")]
    [InlineData("hủy lịch reminder")]
    [InlineData("bỏ lịch reminder")]
    [InlineData("hủy toàn bộ lịch nhắc")]
    [InlineData("không cần nhắc nữa")]
    public void Natural_cancel_phrases_parse_as_disable(string input)
    {
        Assert.True(ZaloBotIntelligence.TryParseReminderCommand(input, out var command));
        Assert.Equal(ZaloReminderCommandKind.Disable, command.Kind);
        Assert.False(command.Repeats);
    }

    [Theory]
    [InlineData("hủy reminder", ZaloBotIntent.CancelReminder)]
    [InlineData("huỷ reminder T6", ZaloBotIntent.CancelReminder)]
    [InlineData("dừng reminder CN", ZaloBotIntent.CancelReminder)]
    [InlineData("bỏ nhắc T4", ZaloBotIntent.CancelReminder)]
    public void Natural_cancel_phrases_route_deterministically_without_ai(string input, ZaloBotIntent expected)
    {
        Assert.Equal(expected, ZaloBotIntelligence.ClassifyDeterministically(input).Intent);
    }

    [Theory]
    [InlineData("không hủy reminder")]
    [InlineData("đừng tắt reminder")]
    [InlineData("không bỏ lịch nhắc")]
    public void Negated_cancel_only_phrases_fail_closed_instead_of_mutating(string input)
    {
        Assert.False(ZaloBotIntelligence.TryParseReminderCommand(input, out _));
        Assert.NotEqual(ZaloBotIntent.CancelReminder, ZaloBotIntelligence.ClassifyDeterministically(input).Intent);
        Assert.NotEqual(ZaloBotIntent.ScheduleReminder, ZaloBotIntelligence.ClassifyDeterministically(input).Intent);
    }

    [Theory]
    [InlineData("xem reminder T6", ZaloReminderCommandKind.Status)]
    [InlineData("reminder hiện tại đâu rồi", ZaloReminderCommandKind.Status)]
    [InlineData("còn reminder không", ZaloReminderCommandKind.Status)]
    [InlineData("đổi lịch nhắc T6", ZaloReminderCommandKind.Update)]
    [InlineData("đổi reminder T6", ZaloReminderCommandKind.Update)]
    [InlineData("sửa reminder CN", ZaloReminderCommandKind.Update)]
    [InlineData("không hủy reminder, đổi reminder T6", ZaloReminderCommandKind.Update)]
    [InlineData("nhắc nhóm sau 6 tiếng", ZaloReminderCommandKind.Schedule)]
    [InlineData("nhắc T6 ngay", ZaloReminderCommandKind.TriggerNow)]
    public void Neighboring_reminder_intents_keep_their_expected_kind(string input, ZaloReminderCommandKind expected)
    {
        Assert.True(ZaloBotIntelligence.TryParseReminderCommand(input, out var command));
        Assert.Equal(expected, command.Kind);
    }
}
