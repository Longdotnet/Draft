using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloRecruitmentGuestGatePolicyTests
{
    [Fact]
    public void SignupWindow_DefaultsToTwoHoursAndIsBounded()
    {
        var defaults = new ConfigurationBuilder().Build();
        var tooShort = Config("0");
        var custom = Config("3");
        var tooLong = Config("99");

        Assert.Equal(TimeSpan.FromHours(2), ZaloRecruitmentGuestGatePolicy.GetSignupWindow(defaults));
        Assert.Equal(TimeSpan.FromHours(1), ZaloRecruitmentGuestGatePolicy.GetSignupWindow(tooShort));
        Assert.Equal(TimeSpan.FromHours(3), ZaloRecruitmentGuestGatePolicy.GetSignupWindow(custom));
        Assert.Equal(TimeSpan.FromHours(6), ZaloRecruitmentGuestGatePolicy.GetSignupWindow(tooLong));
    }

    [Fact]
    public void AddWindow_OpensOnlyNearStartAndClosesAfterStart()
    {
        var configuration = new ConfigurationBuilder().Build();
        var start = new DateTimeOffset(2026, 8, 22, 18, 0, 0, TimeSpan.FromHours(7));

        Assert.False(ZaloRecruitmentGuestGatePolicy.IsAddWindowOpen(start, start.AddHours(-2).AddSeconds(-1), configuration));
        Assert.True(ZaloRecruitmentGuestGatePolicy.IsAddWindowOpen(start, start.AddHours(-2), configuration));
        Assert.True(ZaloRecruitmentGuestGatePolicy.IsAddWindowOpen(start, start.AddMinutes(-1), configuration));
        Assert.False(ZaloRecruitmentGuestGatePolicy.IsAddWindowOpen(start, start, configuration));
    }

    [Fact]
    public void AddCommand_RequiresRecruitmentBroadcastAnchor()
    {
        Assert.True(ZaloRecruitmentGuestGatePolicy.CanHandleFromAnchor(
            ZaloRecruitmentGuestCommandKind.Add,
            ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast));
        Assert.False(ZaloRecruitmentGuestGatePolicy.CanHandleFromAnchor(
            ZaloRecruitmentGuestCommandKind.Add,
            ZaloRecruitmentGuestReplyAnchorKind.GuestConversation));
        Assert.False(ZaloRecruitmentGuestGatePolicy.CanHandleFromAnchor(
            ZaloRecruitmentGuestCommandKind.Add,
            ZaloRecruitmentGuestReplyAnchorKind.None));
    }

    [Theory]
    [InlineData(ZaloRecruitmentGuestCommandKind.Cancel)]
    [InlineData(ZaloRecruitmentGuestCommandKind.UpdateProfile)]
    public void GuestFollowups_AllowGroundedBotConversationAnchors(ZaloRecruitmentGuestCommandKind kind)
    {
        Assert.True(ZaloRecruitmentGuestGatePolicy.CanHandleFromAnchor(
            kind,
            ZaloRecruitmentGuestReplyAnchorKind.RecruitmentBroadcast));
        Assert.True(ZaloRecruitmentGuestGatePolicy.CanHandleFromAnchor(
            kind,
            ZaloRecruitmentGuestReplyAnchorKind.GuestConversation));
        Assert.False(ZaloRecruitmentGuestGatePolicy.CanHandleFromAnchor(
            kind,
            ZaloRecruitmentGuestReplyAnchorKind.None));
    }

    [Fact]
    public void GuestSelectedIntent_RoundTripsSessionId()
    {
        var intent = ZaloRecruitmentGuestGatePolicy.GuestSelectedIntent("session-a");

        Assert.Equal("RecruitmentGuest:session-a", intent);
        Assert.Equal("session-a", ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId(intent));
        Assert.Null(ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId("RecruitmentGuest"));
        Assert.Null(ZaloRecruitmentGuestGatePolicy.TryReadGuestSessionId("KeepRecruiting:session-a"));
    }

    private static IConfiguration Config(string hours) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZaloBot:DraftAutopilot:GuestSignupHoursBeforeStart"] = hours
            })
            .Build();
}
