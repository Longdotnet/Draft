using VolleyDraft.Api.Contracts;
using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests;

public sealed class ZaloProfileUpdatePreRouteNormalizationTests
{
    [Fact]
    public void Structured_bot_prefix_is_removed_before_profile_update_classification()
    {
        const string content = "@Npc cập nhật @Hiệp Hoàng Phạm : nam, trung bình, full-stack thứ 6";
        const string target = "@Hiệp Hoàng Phạm";
        var incoming = new ZaloIncomingMessageEvent(
            "bot-account",
            "bot-account",
            "g1",
            "profile-update-normalization",
            "admin-zalo",
            "Admin",
            content,
            [
                new ZaloBridgeMention("bot-account", 0, "@Npc".Length),
                new ZaloBridgeMention("uid-hiep", content.IndexOf(target, StringComparison.Ordinal), target.Length)
            ],
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var question = ZaloBotService.ExtractQuestion(incoming);
        var decision = ZaloBotIntelligence.ClassifyDeterministically(question);

        Assert.StartsWith("cập nhật @Hiệp", question, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ZaloBotIntent.UpdatePlayerProfile, decision.Intent);
    }
}
