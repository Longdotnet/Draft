using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistReplyTests
{
    [Fact]
    public void Reply_can_reference_resolved_session()
    {
        var reply = new ZaloMemberAssistReply(ZaloMemberAssistKind.PassSlotHelp, "ok", "session");
        Assert.Equal("session", reply.SessionId);
    }
}
