using VolleyDraft.Api.Services.Zalo.Features.PassSlot;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloPassSlotQueryPolicyTests
{
    [Theory]
    [InlineData("CN này còn slot nào đang mở chưa ai nhận?")]
    [InlineData("T6 còn slot pass đang mở không?")]
    [InlineData("kèo 06/09 còn suất nào đang mở?")]
    public void Current_open_question_with_session_selector_stays_session_scoped(string text)
    {
        var scope = ZaloPassSlotQueryPolicy.ResolveScope(text);

        Assert.Equal(ZaloPassSlotHistoryScope.SessionCurrentOpen, scope);
    }

    [Fact]
    public void Current_open_question_without_session_selector_remains_group_wide()
    {
        var scope = ZaloPassSlotQueryPolicy.ResolveScope("còn slot nào đang mở chưa ai nhận?");

        Assert.Equal(ZaloPassSlotHistoryScope.CurrentOpen, scope);
    }
}
