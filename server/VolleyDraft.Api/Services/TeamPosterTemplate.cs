namespace VolleyDraft.Api.Services;

public enum TeamPosterTemplate
{
    NeonArena = 1,
    ChampionshipGold = 2,
    CyberStorm = 3,
    MonolithBroadcast = 4,
    InfernoClash = 5,
    RetroArcade = 6,
    TitaniumLeague = 7,
    VelocityWave = 8,
    NoirSpotlight = 9,
    StreetClash = 10
}

public static class TeamPosterTemplateCatalog
{
    public const int Count = 10;
    public const int ActiveCount = 2;

    public static readonly IReadOnlyList<int> AllIds =
        Enumerable.Range(1, Count).ToArray();

    // Phase 1: keep every renderer available in code, but only Poster 3 and 4
    // participate in new poster assignment/rotation.
    public static readonly IReadOnlyList<int> ActiveIds =
        [(int)TeamPosterTemplate.CyberStorm, (int)TeamPosterTemplate.MonolithBroadcast];

    public static bool IsValid(int templateId) => templateId is >= 1 and <= Count;

    public static bool IsActive(int templateId) =>
        templateId is (int)TeamPosterTemplate.CyberStorm or (int)TeamPosterTemplate.MonolithBroadcast;

    public static string GetDisplayName(int templateId) => (TeamPosterTemplate)templateId switch
    {
        TeamPosterTemplate.NeonArena => "Neon Arena",
        TeamPosterTemplate.ChampionshipGold => "Championship Gold",
        TeamPosterTemplate.CyberStorm => "Cyber Storm",
        TeamPosterTemplate.MonolithBroadcast => "Monolith Broadcast",
        TeamPosterTemplate.InfernoClash => "Inferno Clash",
        TeamPosterTemplate.RetroArcade => "Retro Arcade",
        TeamPosterTemplate.TitaniumLeague => "Titanium League",
        TeamPosterTemplate.VelocityWave => "Velocity Wave",
        TeamPosterTemplate.NoirSpotlight => "Noir Spotlight",
        TeamPosterTemplate.StreetClash => "Street Clash",
        _ => "Neon Arena"
    };
}
