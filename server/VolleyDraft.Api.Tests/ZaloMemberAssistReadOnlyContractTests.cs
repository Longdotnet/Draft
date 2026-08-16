using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloMemberAssistReadOnlyContractTests
{
    [Fact]
    public void Assist_reply_only_carries_help_text_and_session_reference()
    {
        var reply = new ZaloMemberAssistReply(ZaloMemberAssistKind.PassSlotHelp, "help", "s1");
        Assert.Equal("help", reply.Text);
        Assert.Equal("s1", reply.SessionId);
    }
}
