using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistNoMentionPolicyTests
{
    [Fact]
    public void Pass_slot_help_detection_does_not_require_bot_word()
    {
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity("em pass sỉ lót tối nay á 🥺"));
        Assert.True(ZaloMemberAssistService.IsPassSlotHelpOpportunity("tui nhường slot T6 nha"));
    }
}
