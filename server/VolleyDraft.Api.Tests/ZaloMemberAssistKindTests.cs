using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistKindTests
{
    [Fact]
    public void Pass_slot_help_has_explicit_assist_kind()
    {
        Assert.NotEqual(ZaloMemberAssistKind.None, ZaloMemberAssistKind.PassSlotHelp);
    }
}
