using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistPassSlotKindTests
{
    [Fact]
    public void Pass_slot_assist_kind_is_stable()
    {
        Assert.Equal(ZaloMemberAssistKind.PassSlotHelp, new ZaloMemberAssistReply(ZaloMemberAssistKind.PassSlotHelp, "x").Kind);
    }
}
