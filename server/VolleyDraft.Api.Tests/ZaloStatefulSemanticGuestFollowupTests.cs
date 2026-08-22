using Microsoft.Extensions.Configuration;
using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloStatefulSemanticGuestFollowupTests
{
    private static readonly ZaloSemanticActionSettings Settings = new(
        Enabled: true,
        MinimumConfidence: .85,
        MaxContextMessages: 12,
        MaxUserCallsPerMinute: 4,
        MaxGroupCallsPerMinute: 20);

    [Theory]
    [InlineData("semantic_guest_quantity_ambiguous", ZaloStatefulGuestPendingKind.AddQuantity)]
    [InlineData("semantic_guest_update_target_ambiguous", ZaloStatefulGuestPendingKind.UpdateTarget)]
    [InlineData("semantic_guest_profile_fields_ambiguous", ZaloStatefulGuestPendingKind.UpdateFields)]
    [InlineData("semantic_guest_cancel_target_ambiguous", ZaloStatefulGuestPendingKind.CancelTarget)]
    public void PendingOutcome_IsClassified(string outcome, ZaloStatefulGuestPendingKind expected)
    {
        Assert.Equal(expected, ZaloStatefulGuestFollowupPolicy.PendingKind(outcome));
        Assert.NotEmpty(ZaloStatefulGuestFollowupPolicy.MissingFields(expected));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("+1", 1)]
    [InlineData("một", 1)]
    [InlineData("2", 2)]
    [InlineData("+2", 2)]
    [InlineData("hai bạn", 2)]
    public void PendingQuantity_SupportsShortNaturalAnswers(string text, int expected)
    {
        Assert.Equal(expected, ZaloStatefulGuestFollowupPolicy.TryParsePendingQuantity(text));
    }

    [Theory]
    [InlineData("thôi")]
    [InlineData("bỏ qua")]
    [InlineData("không thêm nữa")]
    public void PendingAction_CanBeAbandonedNaturally(string text)
    {
        Assert.True(ZaloStatefulGuestFollowupPolicy.IsPendingAbandon(text));
    }

    [Fact]
    public void PendingAddQuantity_ResumesWithoutReplyingRecruitmentAgain()
    {
        var snapshot = Snapshot(
            ZaloSemanticGuestAnchorKind.PendingGuestAction,
            recruitmentMessageId: "recruitment-1",
            pendingMissingFields: ["pendingAction:AddGuests", "quantity"]);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.AddGuests,
            .99,
            2,
            .99,
            [
                EmptyAddItem(),
                EmptyAddItem()
            ],
            false,
            string.Empty,
            "resumed quantity");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.True(validation.Accepted);
        Assert.Equal(2, validation.Quantity);
    }

    [Fact]
    public void PendingAddWithoutOriginalRecruitmentAuthority_FailsClosed()
    {
        var snapshot = Snapshot(
            ZaloSemanticGuestAnchorKind.PendingGuestAction,
            recruitmentMessageId: null,
            pendingMissingFields: ["pendingAction:AddGuests", "quantity"]);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.AddGuests,
            .99,
            1,
            .99,
            [EmptyAddItem()],
            false,
            string.Empty,
            "resumed quantity");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.False(validation.Accepted);
        Assert.Equal("semantic_guest_add_requires_recruitment_reply", validation.Reason);
    }

    [Fact]
    public void RecentMutation_AllowsCorrectionOnlyForGroundedGuest()
    {
        var grounded = new ZaloSemanticGuestGroundingGuest(
            "reservation-real",
            4,
            "Bạn của Tấn Chí #4",
            PlayerGender.Male,
            PlayerLevel.New,
            null,
            ZaloGuestReservationStatus.Active.ToString());
        var snapshot = Snapshot(
            ZaloSemanticGuestAnchorKind.RecentGuestMutation,
            guests: [grounded],
            pendingMissingFields: ["recentMutationCorrection"]);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.UpdateGuestProfiles,
            .99,
            1,
            .99,
            [new ZaloSemanticGuestPlanItem(
                "bạn đó",
                grounded.ReservationId,
                grounded.SponsorSequence,
                null,
                0,
                PlayerGender.Female,
                .99,
                null,
                0,
                null,
                0,
                .99)],
            false,
            string.Empty,
            "correction");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.True(validation.Accepted);
        Assert.Single(validation.Items);
        Assert.Equal("reservation-real", validation.Items[0].ReservationId);
        Assert.Equal(PlayerGender.Female, validation.Items[0].Gender);
    }

    [Fact]
    public void RecentMutation_RejectsFabricatedCorrectionTarget()
    {
        var grounded = new ZaloSemanticGuestGroundingGuest(
            "reservation-real",
            1,
            "Bạn của Nick #1",
            null,
            null,
            null,
            ZaloGuestReservationStatus.Active.ToString());
        var snapshot = Snapshot(
            ZaloSemanticGuestAnchorKind.RecentGuestMutation,
            guests: [grounded],
            pendingMissingFields: ["recentMutationCorrection"]);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.CancelGuests,
            .99,
            1,
            .99,
            [new ZaloSemanticGuestPlanItem(
                "#1",
                "reservation-fake",
                1,
                null,
                0,
                null,
                0,
                null,
                0,
                null,
                0,
                .99)],
            false,
            string.Empty,
            "undo");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.False(validation.Accepted);
        Assert.Equal("semantic_guest_cancel_target_ambiguous", validation.Reason);
    }

    [Fact]
    public void FreshnessWindow_IsBounded()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(ZaloStatefulGuestFollowupPolicy.IsFresh(now.AddMinutes(-9), now, 10));
        Assert.False(ZaloStatefulGuestFollowupPolicy.IsFresh(now.AddMinutes(-11), now, 10));
    }

    private static ZaloSemanticGuestPlanItem EmptyAddItem() => new(
        string.Empty,
        null,
        null,
        null,
        0,
        null,
        0,
        null,
        0,
        null,
        0,
        1);

    private static ZaloSemanticGuestGroundingSnapshot Snapshot(
        ZaloSemanticGuestAnchorKind anchor,
        string? recruitmentMessageId = null,
        IReadOnlyList<ZaloSemanticGuestGroundingGuest>? guests = null,
        IReadOnlyList<string>? pendingMissingFields = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ZaloSemanticGuestGroundingSnapshot(
            "session-1",
            "T7",
            now.AddHours(1),
            17,
            18,
            true,
            "sender-1",
            "Nick",
            anchor,
            recruitmentMessageId,
            guests ?? [],
            pendingMissingFields ?? [],
            now,
            now.ToOffset(TimeSpan.FromHours(7)));
    }
}
