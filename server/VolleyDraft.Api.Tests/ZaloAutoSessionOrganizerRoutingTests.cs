using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloAutoSessionOrganizerRoutingTests
{
    [Fact]
    public void ActiveOrganizer_KeepsOwnership_EvenWithoutFallbackTrust()
    {
        var route = ZaloAutoSessionOrganizerRouting.Evaluate(
            "admin-a",
            "admin-a",
            activeOrganizerStillAuthorized: true,
            senderTrustedForTakeover: false,
            stronglyAddressed: false,
            takeoverEscalated: false,
            "ok");

        Assert.Equal(ZaloAutoSessionOrganizerRoute.ActiveOrganizer, route);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("ừ")]
    [InlineData("T6 thôi")]
    [InlineData("sân A")]
    public void OtherAdmin_BeforeEscalation_DoesNotStealConversation(string message)
    {
        var route = ZaloAutoSessionOrganizerRouting.Evaluate(
            "admin-b",
            "admin-a",
            activeOrganizerStillAuthorized: true,
            senderTrustedForTakeover: true,
            stronglyAddressed: true,
            takeoverEscalated: false,
            message);

        Assert.Equal(ZaloAutoSessionOrganizerRoute.IgnoreBystander, route);
    }

    [Fact]
    public void TrustedFallback_ExplicitEarlyTakeover_IsRejectedDeterministically()
    {
        var route = ZaloAutoSessionOrganizerRouting.Evaluate(
            "admin-b",
            "admin-a",
            activeOrganizerStillAuthorized: true,
            senderTrustedForTakeover: true,
            stronglyAddressed: true,
            takeoverEscalated: false,
            "để tui xử lý");

        Assert.Equal(ZaloAutoSessionOrganizerRoute.RejectEarlyTakeover, route);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("ừ")]
    [InlineData("haha")]
    [InlineData("đông quá")]
    public void TrustedFallback_AfterEscalation_ChatterStillDoesNotClaim(string message)
    {
        var route = ZaloAutoSessionOrganizerRouting.Evaluate(
            "admin-b",
            "admin-a",
            activeOrganizerStillAuthorized: true,
            senderTrustedForTakeover: true,
            stronglyAddressed: true,
            takeoverEscalated: true,
            message);

        Assert.Equal(ZaloAutoSessionOrganizerRoute.IgnoreBystander, route);
    }

    [Theory]
    [InlineData("T6 thôi")]
    [InlineData("T6 18h")]
    [InlineData("sân A")]
    [InlineData("tạo đi")]
    [InlineData("bỏ CN")]
    [InlineData("nhận xử lý")]
    public void TrustedFallback_AfterEscalation_SubstantiveReplyMayTakeOver(string message)
    {
        var route = ZaloAutoSessionOrganizerRouting.Evaluate(
            "admin-b",
            "admin-a",
            activeOrganizerStillAuthorized: true,
            senderTrustedForTakeover: true,
            stronglyAddressed: true,
            takeoverEscalated: true,
            message);

        Assert.Equal(ZaloAutoSessionOrganizerRoute.AllowTakeover, route);
    }

    [Theory]
    [InlineData("T6 thôi")]
    [InlineData("tạo đi")]
    [InlineData("nhận xử lý")]
    public void UntrustedZaloAdmin_AfterEscalation_CannotTakeOver(string message)
    {
        var route = ZaloAutoSessionOrganizerRouting.Evaluate(
            "admin-b",
            "admin-a",
            activeOrganizerStillAuthorized: true,
            senderTrustedForTakeover: false,
            stronglyAddressed: true,
            takeoverEscalated: true,
            message);

        Assert.Equal(ZaloAutoSessionOrganizerRoute.IgnoreBystander, route);
    }

    [Fact]
    public void OtherAdmin_WithoutAddressingBot_NeverTakesOver()
    {
        var route = ZaloAutoSessionOrganizerRouting.Evaluate(
            "admin-b",
            "admin-a",
            activeOrganizerStillAuthorized: true,
            senderTrustedForTakeover: true,
            stronglyAddressed: false,
            takeoverEscalated: true,
            "T6 thôi");

        Assert.Equal(ZaloAutoSessionOrganizerRoute.IgnoreBystander, route);
    }

    [Fact]
    public void OwnerLostAdminRole_TrustedFallbackCanTakeOverImmediately()
    {
        var route = ZaloAutoSessionOrganizerRouting.Evaluate(
            "admin-b",
            "admin-a",
            activeOrganizerStillAuthorized: false,
            senderTrustedForTakeover: true,
            stronglyAddressed: true,
            takeoverEscalated: false,
            "T6 thôi");

        Assert.Equal(ZaloAutoSessionOrganizerRoute.AllowTakeover, route);
    }

    [Fact]
    public void OwnerLostAdminRole_UntrustedAdminStillCannotTakeOver()
    {
        var route = ZaloAutoSessionOrganizerRouting.Evaluate(
            "admin-b",
            "admin-a",
            activeOrganizerStillAuthorized: false,
            senderTrustedForTakeover: false,
            stronglyAddressed: true,
            takeoverEscalated: true,
            "T6 thôi");

        Assert.Equal(ZaloAutoSessionOrganizerRoute.IgnoreBystander, route);
    }
}
