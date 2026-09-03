using VolleyDraft.Api.Models;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSemanticGuestPlannerTests
{
    private static readonly ZaloSemanticActionSettings Settings = new(
        Enabled: true,
        MinimumConfidence: .85,
        MaxContextMessages: 12,
        MaxUserCallsPerMinute: 4,
        MaxGroupCallsPerMinute: 20);

    [Fact]
    public void ParsePlan_PreservesStructuredNaturalGuestMeaning()
    {
        var plan = ZaloSemanticGuestPlanner.ParsePlan("""
            {
              "action":"AddGuests",
              "confidence":0.98,
              "quantity":2,
              "quantityConfidence":0.99,
              "guests":[
                {
                  "referenceText":"Minh",
                  "reservationId":null,
                  "sponsorSequence":null,
                  "displayName":"Minh",
                  "nameConfidence":0.99,
                  "gender":"Male",
                  "genderConfidence":0.98,
                  "level":"Good",
                  "levelConfidence":0.93,
                  "role":null,
                  "roleConfidence":0,
                  "confidence":0.98
                },
                {
                  "referenceText":"Huy",
                  "reservationId":null,
                  "sponsorSequence":null,
                  "displayName":"Huy",
                  "nameConfidence":0.99,
                  "gender":"Female",
                  "genderConfidence":0.98,
                  "level":"Average",
                  "levelConfidence":0.94,
                  "role":null,
                  "roleConfidence":0,
                  "confidence":0.98
                }
              ],
              "needsClarification":false,
              "clarificationReason":"",
              "reason":"clear two guests"
            }
            """);

        Assert.Equal(ZaloSemanticGuestActionKind.AddGuests, plan.Action);
        Assert.Equal(2, plan.Quantity);
        Assert.Equal("Minh", plan.Guests[0].DisplayName);
        Assert.Equal(PlayerGender.Male, plan.Guests[0].Gender);
        Assert.Equal(PlayerLevel.Good, plan.Guests[0].Level);
        Assert.Equal(PlayerGender.Female, plan.Guests[1].Gender);
        Assert.Equal(PlayerLevel.Average, plan.Guests[1].Level);
    }

    [Fact]
    public void ParsePlan_AcceptsTentativeConfirmAndReplaceDomainActions()
    {
        var tentative = ZaloSemanticGuestPlanner.ParsePlan("""
            {
              "action":"AddTentativeGuests",
              "confidence":0.97,
              "quantity":1,
              "quantityConfidence":0.98,
              "guests":[{
                "referenceText":"Minh",
                "reservationId":null,
                "sponsorSequence":null,
                "displayName":"Minh",
                "nameConfidence":0.99,
                "gender":null,
                "genderConfidence":0,
                "level":null,
                "levelConfidence":0,
                "role":null,
                "roleConfidence":0,
                "confidence":0.97
              }],
              "needsClarification":false,
              "clarificationReason":"",
              "reason":"possible guest"
            }
            """);
        var confirm = ZaloSemanticGuestPlanner.ParsePlan("""
            {
              "action":"ConfirmGuests",
              "confidence":0.98,
              "quantity":1,
              "quantityConfidence":0.98,
              "guests":[{
                "referenceText":"Minh",
                "reservationId":"g1",
                "sponsorSequence":1,
                "displayName":null,
                "nameConfidence":0,
                "gender":null,
                "genderConfidence":0,
                "level":null,
                "levelConfidence":0,
                "role":null,
                "roleConfidence":0,
                "confidence":0.99
              }],
              "needsClarification":false,
              "clarificationReason":"",
              "reason":"guest confirmed"
            }
            """);
        var replace = ZaloSemanticGuestPlanner.ParsePlan("""
            {
              "action":"ReplaceGuest",
              "confidence":0.99,
              "quantity":1,
              "quantityConfidence":0.99,
              "guests":[
                {
                  "referenceText":"Minh",
                  "reservationId":"g1",
                  "sponsorSequence":1,
                  "displayName":null,
                  "nameConfidence":0,
                  "gender":null,
                  "genderConfidence":0,
                  "level":null,
                  "levelConfidence":0,
                  "role":null,
                  "roleConfidence":0,
                  "confidence":0.99
                },
                {
                  "referenceText":"Huy",
                  "reservationId":null,
                  "sponsorSequence":null,
                  "displayName":"Huy",
                  "nameConfidence":0.99,
                  "gender":"Male",
                  "genderConfidence":0.95,
                  "level":null,
                  "levelConfidence":0,
                  "role":null,
                  "roleConfidence":0,
                  "confidence":0.99
                }
              ],
              "needsClarification":false,
              "clarificationReason":"",
              "reason":"replace old guest"
            }
            """);

        Assert.Equal(ZaloSemanticGuestActionKind.AddTentativeGuests, tentative.Action);
        Assert.Equal(ZaloSemanticGuestActionKind.ConfirmGuests, confirm.Action);
        Assert.Equal(ZaloSemanticGuestActionKind.ReplaceGuest, replace.Action);
        Assert.Equal("Huy", replace.Guests[1].DisplayName);
    }

    [Fact]
    public void AddValidation_DropsGenericFriendPhrase_ButKeepsSlotMutation()
    {
        var snapshot = Snapshot(ZaloSemanticGuestAnchorKind.RecruitmentBroadcast);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.AddGuests,
            .98,
            1,
            .99,
            [new ZaloSemanticGuestPlanItem(
                "+1 cho bạn nha",
                null,
                null,
                "cho bạn nha",
                .96,
                null,
                0,
                null,
                0,
                null,
                0,
                .98)],
            true,
            "profile wording ambiguous",
            "clear add");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.True(validation.Accepted);
        Assert.Equal(1, validation.Quantity);
        Assert.Null(Assert.Single(validation.Items).DisplayName);
    }

    [Fact]
    public void AddValidation_RequiresRecruitmentBroadcastAuthority()
    {
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.AddGuests,
            .99,
            1,
            .99,
            [new ZaloSemanticGuestPlanItem("+1", null, null, null, 0, null, 0, null, 0, null, 0, .99)],
            false,
            string.Empty,
            "clear add");

        var validation = ZaloSemanticGuestPlanValidator.Validate(
            plan,
            Snapshot(ZaloSemanticGuestAnchorKind.GuestConversation),
            Settings);

        Assert.False(validation.Accepted);
        Assert.Equal("semantic_guest_add_requires_recruitment_reply", validation.Reason);
    }

    [Fact]
    public void ConfirmValidation_OnlyAllowsGroundedTentativeGuest()
    {
        var snapshot = Snapshot(
            ZaloSemanticGuestAnchorKind.ActiveGuestConversation,
            [
                new ZaloSemanticGuestGroundingGuest(
                    "g1", 1, "Minh", null, null, null, ZaloGuestReservationStatus.Tentative.ToString()),
                new ZaloSemanticGuestGroundingGuest(
                    "g2", 2, "Huy", null, null, null, ZaloGuestReservationStatus.Active.ToString())
            ]);
        var confirm = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.ConfirmGuests,
            .99,
            1,
            .99,
            [new ZaloSemanticGuestPlanItem("Minh", "g1", 1, null, 0, null, 0, null, 0, null, 0, .99)],
            false,
            string.Empty,
            "confirmed");
        var wrong = confirm with
        {
            Guests = [new ZaloSemanticGuestPlanItem("Huy", "g2", 2, null, 0, null, 0, null, 0, null, 0, .99)]
        };

        Assert.True(ZaloSemanticGuestPlanValidator.Validate(confirm, snapshot, Settings).Accepted);
        var rejected = ZaloSemanticGuestPlanValidator.Validate(wrong, snapshot, Settings);
        Assert.False(rejected.Accepted);
        Assert.Equal("semantic_guest_confirm_target_ambiguous", rejected.Reason);
    }

    [Fact]
    public void ReplaceValidation_RequiresGroundedOldGuest_AndUngroundedNewGuest()
    {
        var snapshot = Snapshot(
            ZaloSemanticGuestAnchorKind.ActiveGuestConversation,
            [new ZaloSemanticGuestGroundingGuest(
                "g1", 1, "Minh", PlayerGender.Male, null, null, ZaloGuestReservationStatus.Active.ToString())]);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.ReplaceGuest,
            .99,
            1,
            .99,
            [
                new ZaloSemanticGuestPlanItem("Minh", "g1", 1, null, 0, null, 0, null, 0, null, 0, .99),
                new ZaloSemanticGuestPlanItem("Huy", null, null, "Huy", .99, PlayerGender.Male, .95, null, 0, null, 0, .99)
            ],
            false,
            string.Empty,
            "replace");

        var accepted = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);
        Assert.True(accepted.Accepted);
        Assert.Equal("g1", accepted.Items[0].ReservationId);
        Assert.Equal("Huy", accepted.Items[1].DisplayName);

        var fabricated = plan with
        {
            Guests =
            [
                plan.Guests[0],
                plan.Guests[1] with { ReservationId = "fake-new-id" }
            ]
        };
        var rejected = ZaloSemanticGuestPlanValidator.Validate(fabricated, snapshot, Settings);
        Assert.False(rejected.Accepted);
        Assert.Equal("semantic_guest_replace_new_target_fabricated", rejected.Reason);
    }

    [Fact]
    public void UpdateValidation_RejectsFabricatedReservationId()
    {
        var snapshot = Snapshot(
            ZaloSemanticGuestAnchorKind.ActiveGuestConversation,
            [new ZaloSemanticGuestGroundingGuest(
                "guest-real",
                1,
                "Bạn của Nick #1",
                null,
                null,
                null,
                ZaloGuestReservationStatus.Active.ToString())]);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.UpdateGuestProfiles,
            .99,
            1,
            .99,
            [new ZaloSemanticGuestPlanItem(
                "#1",
                "guest-fabricated",
                1,
                null,
                0,
                PlayerGender.Male,
                .99,
                null,
                0,
                null,
                0,
                .99)],
            false,
            string.Empty,
            "gender update");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.False(validation.Accepted);
        Assert.Equal("semantic_guest_invalid_guest_target", validation.Reason);
    }

    [Fact]
    public void LegacyFallback_DoesNotTreatGenericPhraseAsGuestName()
    {
        var command = ZaloRecruitmentGuestPolicy.TryParse("+1 cho bạn nha");

        Assert.NotNull(command);
        Assert.Equal(ZaloRecruitmentGuestCommandKind.Add, command!.Kind);
        Assert.Equal(1, command.Quantity);
        Assert.Null(Assert.Single(command.Guests!).DisplayName);
    }

    [Fact]
    public void UpdateValidation_CanApplyDifferentProfilesToTwoGroundedGuests()
    {
        var guests = new[]
        {
            new ZaloSemanticGuestGroundingGuest("g1", 1, "Bạn của Nick #1", null, null, null, "Active"),
            new ZaloSemanticGuestGroundingGuest("g2", 2, "Bạn của Nick #2", null, null, null, "Active")
        };
        var snapshot = Snapshot(ZaloSemanticGuestAnchorKind.ActiveGuestConversation, guests);
        var plan = new ZaloSemanticGuestPlan(
            ZaloSemanticGuestActionKind.UpdateGuestProfiles,
            .98,
            2,
            .98,
            [
                new ZaloSemanticGuestPlanItem("guest đầu", "g1", 1, null, 0, PlayerGender.Male, .99, PlayerLevel.Good, .95, null, 0, .98),
                new ZaloSemanticGuestPlanItem("guest sau", "g2", 2, null, 0, PlayerGender.Female, .99, PlayerLevel.New, .95, null, 0, .98)
            ],
            false,
            string.Empty,
            "two profile updates");

        var validation = ZaloSemanticGuestPlanValidator.Validate(plan, snapshot, Settings);

        Assert.True(validation.Accepted);
        Assert.Equal(2, validation.Items.Count);
        Assert.Equal(PlayerGender.Male, validation.Items[0].Gender);
        Assert.Equal(PlayerLevel.Good, validation.Items[0].Level);
        Assert.Equal(PlayerGender.Female, validation.Items[1].Gender);
    }

    private static ZaloSemanticGuestGroundingSnapshot Snapshot(
        ZaloSemanticGuestAnchorKind anchor,
        IReadOnlyList<ZaloSemanticGuestGroundingGuest>? guests = null)
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
            anchor == ZaloSemanticGuestAnchorKind.RecruitmentBroadcast ? "recruitment-1" : null,
            guests ?? [],
            [],
            now,
            now.ToOffset(TimeSpan.FromHours(7)));
    }
}
