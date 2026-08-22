using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloSemanticConditionalGuestPlannerTests
{
    [Fact]
    public void ParsePlan_PreservesConditionalFields()
    {
        var plan = ZaloSemanticGuestPlanner.ParsePlan("""
            {
              "action":"ScheduleConditionalGuests",
              "confidence":0.99,
              "quantity":2,
              "quantityConfidence":0.99,
              "conditionalHour":19,
              "conditionalMinute":30,
              "conditionalEvening":false,
              "minimumMissingSlots":1,
              "guests":[
                {"referenceText":"guest1","reservationId":null,"sponsorSequence":null,"displayName":null,"nameConfidence":0,"gender":null,"genderConfidence":0,"level":null,"levelConfidence":0,"role":null,"roleConfidence":0,"confidence":0.99},
                {"referenceText":"guest2","reservationId":null,"sponsorSequence":null,"displayName":null,"nameConfidence":0,"gender":null,"genderConfidence":0,"level":null,"levelConfidence":0,"role":null,"roleConfidence":0,"confidence":0.99}
              ],
              "needsClarification":false,
              "clarificationReason":"",
              "reason":"conditional"
            }
            """);

        Assert.Equal(ZaloSemanticGuestActionKind.ScheduleConditionalGuests, plan.Action);
        Assert.Equal(19, plan.ConditionalHour);
        Assert.Equal(30, plan.ConditionalMinute);
        Assert.Equal(1, plan.MinimumMissingSlots);
        Assert.Equal(2, plan.Quantity);
    }
}
