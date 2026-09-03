using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloShareAndTeamPreferenceAuthorizationPolicyTests
{
    [Fact]
    public void Team_preference_parser_preserves_self_reference_with_single_partner_mention()
    {
        const string text = "xếp tui chung team với To An ở CN 16/8 đi";
        Assert.True(ZaloNaturalCommandParser.TryParseTeamPreference(text, out var parsed));

        var bound = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(
            [new ZaloMentionedUser("user-toan", "To An")],
            parsed);

        Assert.NotNull(bound);
        Assert.Equal("tui", bound!.PlayerReferences[0], ignoreCase: true);
        Assert.Null(bound.PlayerZaloUserIds![0]);
        Assert.Equal("user-toan", bound.PlayerZaloUserIds[1]);
    }

    [Theory]
    [InlineData("tui share slot với To An T6")]
    [InlineData("mình share slot cho To An T6")]
    public void Share_parser_keeps_sender_as_anchor_for_self_service_phrasing(string text)
    {
        Assert.True(ZaloNaturalCommandParser.TryParseShareSlot(text, out var command));
        Assert.Contains(
            ZaloBotIntelligence.Normalize(command.Anchor),
            new[] { "tui", "minh" });
        Assert.Single(command.Partners);
    }
}
