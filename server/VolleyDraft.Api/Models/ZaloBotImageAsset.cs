namespace VolleyDraft.Api.Models;

public sealed class ZaloBotImageAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string AdminUserId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/jpeg";
    public long Size { get; set; }
    public byte[] Data { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User AdminUser { get; set; } = null!;
}
