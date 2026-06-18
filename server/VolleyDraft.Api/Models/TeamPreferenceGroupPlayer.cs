namespace VolleyDraft.Api.Models;

public sealed class TeamPreferenceGroupPlayer
{
    public string TeamPreferenceGroupId { get; set; } = string.Empty;
    public string SessionPlayerId { get; set; } = string.Empty;
    public int RotationOrder { get; set; }

    public TeamPreferenceGroup TeamPreferenceGroup { get; set; } = null!;
    public SessionPlayer SessionPlayer { get; set; } = null!;
}
