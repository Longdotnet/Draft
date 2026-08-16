using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistNoneKindTests
{
    [Fact]
    public void None_kind_is_distinct_from_pass_slot()
    {
        Assert.NotEqual(ZaloMemberAssistKind.None, ZaloMemberAssistKind.PassSlotHelp);
    }
}
