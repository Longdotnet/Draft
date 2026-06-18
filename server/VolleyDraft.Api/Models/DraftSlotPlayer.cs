namespace VolleyDraft.Api.Models;

public sealed class DraftSlotPlayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string DraftSlotId { get; set; } = string.Empty;
    public string SessionPlayerId { get; set; } = string.Empty;
    public int RotationOrder { get; set; } = 1;

    public DraftSlot DraftSlot { get; set; } = null!;
    public SessionPlayer SessionPlayer { get; set; } = null!;
}
