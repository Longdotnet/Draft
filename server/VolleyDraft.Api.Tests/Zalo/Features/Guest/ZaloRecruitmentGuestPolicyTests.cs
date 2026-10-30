using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloRecruitmentGuestPolicyTests
{
    [Theory]
    [InlineData("+1", 1)]
    [InlineData("+2 bạn tui", 2)]
    [InlineData("tui kéo thêm 2 đứa", 2)]
    [InlineData("cho tui +1", 1)]
    [InlineData("tui + bạn tui", 1)]
    [InlineData("thêm bạn tui vô", 1)]
    public void AddParser_AcceptsShortNaturalForms(string text, int quantity)
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse(text);

        Assert.NotNull(command);
        Assert.Equal(ZaloRecruitmentGuestCommandKind.Add, command!.Kind);
        Assert.Equal(quantity, command.Quantity);
    }

    [Theory]
    [InlineData("+1 bạn tui")]
    [InlineData("+1 bạn mình")]
    [InlineData("+1 bạn T7")]
    public void AddParser_DoesNotMistakePronounOrSessionForGuestName(string text)
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse(text);

        Assert.NotNull(command);
        Assert.Single(command!.Guests!);
        Assert.Null(command.Guests[0].DisplayName);
    }

    [Fact]
    public void AddParser_PreservesMinhAsARealName()
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse("+1 Minh");

        Assert.NotNull(command);
        Assert.Equal("Minh", Assert.Single(command!.Guests!).DisplayName);
    }

    [Theory]
    [InlineData("@Npc +1 bạn @Hmy Bk hôm nay", 1, "Hmy Bk")]
    [InlineData("+2 bạn của @Hmy Bk", 2, "Hmy Bk")]
    public void AddParser_TreatsMentionedGroupMemberAsDelegatedSponsor(
        string text,
        int quantity,
        string sponsor)
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse(text);

        Assert.NotNull(command);
        Assert.Equal(ZaloRecruitmentGuestCommandKind.Add, command!.Kind);
        Assert.Equal(quantity, command.Quantity);
        Assert.Equal(sponsor, command.SponsorReference);
        Assert.All(command.Guests!, guest => Assert.Null(guest.DisplayName));
    }

    [Fact]
    public void SponsorPolicy_UsesOnlyCurrentMemberWithExactNormalizedDisplayName()
    {
        var members = new[]
        {
            new ZaloGroupMember
            {
                Id = "hmy-current",
                ZaloConnectionId = "conn",
                GroupId = "group",
                ZaloUserId = "hmy-id",
                DisplayName = "Hmy Bk",
                IsCurrentMember = true
            },
            new ZaloGroupMember
            {
                Id = "hmy-old",
                ZaloConnectionId = "conn",
                GroupId = "group",
                ZaloUserId = "hmy-old-id",
                DisplayName = "HMY BK",
                IsCurrentMember = false
            },
            new ZaloGroupMember
            {
                Id = "other",
                ZaloConnectionId = "conn",
                GroupId = "group",
                ZaloUserId = "other-id",
                DisplayName = "Hmy",
                IsCurrentMember = true
            }
        };

        var matches = ZaloRecruitmentGuestSponsorPolicy.FindCurrentMembers("hmy bk", members);

        var match = Assert.Single(matches);
        Assert.Equal("hmy-id", match.ZaloUserId);
    }

    [Fact]
    public void AddParser_ReadsTwoNames()
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse("+2 Minh với Huy");

        Assert.NotNull(command);
        Assert.Equal(2, command!.Guests!.Count);
        Assert.Equal("Minh", command.Guests[0].DisplayName);
        Assert.Equal("Huy", command.Guests[1].DisplayName);
    }

    [Fact]
    public void AddParser_ReadsOneMaleOneFemale()
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse("+2 bạn tui, 1 nam 1 nữ");

        Assert.NotNull(command);
        Assert.Equal(PlayerGender.Male, command!.Guests![0].Gender);
        Assert.Equal(PlayerGender.Female, command.Guests[1].Gender);
    }

    [Theory]
    [InlineData("1 bạn tui nghỉ", 1)]
    [InlineData("2 bạn tui nghỉ hết", 2)]
    public void CancelParser_AcceptsSponsorLanguage(string text, int quantity)
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse(text);

        Assert.NotNull(command);
        Assert.Equal(ZaloRecruitmentGuestCommandKind.Cancel, command!.Kind);
        Assert.Equal(quantity, command.Quantity);
    }

    [Fact]
    public void ProfileParser_AllowsRenameBySponsorSequence()
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse("bạn #1 tên Minh");

        Assert.NotNull(command);
        Assert.Equal(ZaloRecruitmentGuestCommandKind.UpdateProfile, command!.Kind);
        Assert.Equal(1, command.SponsorSequence);
        Assert.Equal("Minh", command.RenameTo);
    }

    [Fact]
    public void ProfileParser_AllowsGenderByGuestName()
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse("Minh nam nha");

        Assert.NotNull(command);
        Assert.Equal(ZaloRecruitmentGuestCommandKind.UpdateProfile, command!.Kind);
        Assert.Equal("minh", command.GuestReference);
        Assert.Equal(PlayerGender.Male, command.Gender);
    }

    [Fact]
    public void ProfileParser_AllowsBothGuestsGenderAtOnce()
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse("2 bạn tui đều nam");

        Assert.NotNull(command);
        Assert.True(command!.ApplyAll);
        Assert.Equal(PlayerGender.Male, command.Gender);
    }
}
