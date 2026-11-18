namespace VolleyDraft.Api.Models;

public sealed class ZaloMemberPhotoAssignment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ZaloConnectionId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string ZaloUserId { get; set; } = string.Empty;
    public string ZaloBotImageAssetId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ZaloConnection ZaloConnection { get; set; } = null!;
    public ZaloBotImageAsset ImageAsset { get; set; } = null!;
}
