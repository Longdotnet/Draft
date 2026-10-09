namespace VolleyDraft.Api.Services.Zalo.Features.ShareSlot;

/// <summary>
/// Distinguishes generic guidance about sharing slots from a direct slot mutation request.
/// </summary>
internal static class ZaloShareSlotAnnouncementPolicy
{
    public static bool IsAnnouncement(string value)
    {
        var q = Zalo.Conversation.ZaloTextNormalizer.Normalize(value)
            .Replace("@", string.Empty, StringComparison.Ordinal);
        if (!Has(q, "share slot", "chung slot", "slot thay phien")) return false;
        var describesFutureGuidance = Has(q,
            "lan sau", "ai muon", "neu ai", "nguoi nao muon", "muon share thi", "can share thi");
        var directsPeopleToBot = Has(q,
            "noi voi npc", "noi voi bot", "nhan npc", "nhan bot", "tag npc", "tag bot", "bao npc", "bao bot");
        return describesFutureGuidance && directsPeopleToBot;
    }

    private static bool Has(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}
