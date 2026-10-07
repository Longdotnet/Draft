using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloTeamPreferenceMentionBindingTests
{
    [Fact]
    public void Single_partner_mention_binds_uid_without_guessing_self_uid()
    {
        const string question = "tui muốn chơi chung team với @To An hôm nay";
        Assert.True(ZaloNaturalCommandParser.TryParseTeamPreference(question, out var parsed));

        var command = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(
            [new ZaloMentionedUser("to-an-id", "To An")],
            parsed);

        Assert.NotNull(command);
        Assert.Equal(["tui", "To An"], command!.PlayerReferences);
        Assert.NotNull(command.PlayerZaloUserIds);
        Assert.Equal(2, command.PlayerZaloUserIds!.Count);
        Assert.Null(command.PlayerZaloUserIds[0]);
        Assert.Equal("to-an-id", command.PlayerZaloUserIds[1]);
        Assert.Equal("hôm nay", command.SessionReference, ignoreCase: true);
    }

    [Fact]
    public void Single_mention_that_does_not_match_a_reference_is_not_guessed()
    {
        var original = new ZaloTeamPreferenceCommand(
            ["tui", "To An"],
            SessionReference: "T6");

        var command = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(
            [new ZaloMentionedUser("other-id", "Người Khác")],
            original);

        Assert.Same(original, command);
        Assert.Null(command!.PlayerZaloUserIds);
    }

    [Fact]
    public void Single_mention_preserves_existing_uid_bindings_on_other_references()
    {
        var original = new ZaloTeamPreferenceCommand(
            ["Long", "To An", "Anh Duy"],
            ["long-id", null, "anh-duy-id"],
            "T6");

        var command = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(
            [new ZaloMentionedUser("to-an-id", "To An")],
            original);

        Assert.NotNull(command);
        Assert.Equal(["Long", "To An", "Anh Duy"], command!.PlayerReferences);
        Assert.Equal(["long-id", "to-an-id", "anh-duy-id"], command.PlayerZaloUserIds);
        Assert.Equal("T6", command.SessionReference);
    }

    [Fact]
    public void Duplicate_display_name_mentions_do_not_bind_an_arbitrary_uid()
    {
        var original = new ZaloTeamPreferenceCommand(
            ["tui", "To An"],
            Relation: ZaloTeamRelationshipKind.Apart);

        var command = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(
            [
                new ZaloMentionedUser("to-an-1", "To An"),
                new ZaloMentionedUser("to-an-2", "To An")
            ],
            original);

        Assert.NotNull(command);
        Assert.Equal(["tui", "To An"], command!.PlayerReferences);
        Assert.True(command.PlayerZaloUserIds is null || command.PlayerZaloUserIds[1] is null);
        Assert.True(command.NeedsClarification);
        Assert.False(string.IsNullOrWhiteSpace(command.ClarificationQuestion));
    }

    [Fact]
    public void Repeated_mention_of_the_same_uid_is_not_ambiguous()
    {
        var original = new ZaloTeamPreferenceCommand(
            ["tui", "To An"],
            Relation: ZaloTeamRelationshipKind.Apart);

        var command = ZaloNaturalCommandParser.BindExplicitTeamPreferenceMentions(
            [
                new ZaloMentionedUser("to-an-id", "To An"),
                new ZaloMentionedUser("to-an-id", "To An")
            ],
            original);

        Assert.NotNull(command);
        Assert.Equal("to-an-id", command!.PlayerZaloUserIds![1]);
        Assert.False(command.NeedsClarification);
    }
}
