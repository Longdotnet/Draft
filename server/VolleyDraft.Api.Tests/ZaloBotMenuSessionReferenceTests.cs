using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloBotMenuSessionReferenceTests
{
    [Theory]
    [InlineData("8 T4", 8, "t4", ZaloBotIntent.SyncPoll)]
    [InlineData("8 thứ 4", 8, "thu 4", ZaloBotIntent.SyncPoll)]
    [InlineData("9 T4", 9, "t4", ZaloBotIntent.AutoDraft)]
    [InlineData("10 T4", 10, "t4", ZaloBotIntent.TeamImage)]
    [InlineData("10 CN", 10, "cn", ZaloBotIntent.TeamImage)]
    [InlineData("10 thứ 4", 10, "thu 4", ZaloBotIntent.TeamImage)]
    [InlineData("10 ngày mai", 10, "ngay mai", ZaloBotIntent.TeamImage)]
    [InlineData("10 2/9", 10, "2/9", ZaloBotIntent.TeamImage)]
    [InlineData("@bot 10 T4", 10, "t4", ZaloBotIntent.TeamImage)]
    public void Numeric_menu_command_can_carry_session_reference(
        string input,
        int expectedCommand,
        string expectedReference,
        ZaloBotIntent expectedIntent)
    {
        Assert.True(ZaloBotIntelligence.TryGetMenuCommand(input, out var command, out var reference));
        Assert.Equal(expectedCommand, command);
        Assert.Equal(expectedReference, reference);

        var decision = ZaloBotIntelligence.ClassifyDeterministically(input);
        Assert.Equal(expectedIntent, decision.Intent);
        Assert.Equal(expectedReference, decision.SessionReference);
        Assert.Equal("numeric_command_with_session_reference", decision.Reason);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("9")]
    public void Existing_exact_numeric_commands_keep_their_contract(string input)
    {
        Assert.True(ZaloBotIntelligence.TryGetExactCommand(input, out _));
        Assert.True(ZaloBotIntelligence.TryGetMenuCommand(input, out _, out var reference));
        Assert.Null(reference);
        Assert.Equal("exact_numeric_command", ZaloBotIntelligence.ClassifyDeterministically(input).Reason);
    }

    [Theory]
    [InlineData("10abc")]
    [InlineData("10 team 1")]
    [InlineData("10 abc")]
    public void Non_session_numeric_suffixes_do_not_become_menu_commands(string input)
    {
        Assert.False(ZaloBotIntelligence.TryGetMenuCommand(input, out _, out _));
    }
}