namespace VolleyDraft.Api.Services;

public enum TeamPosterTemplate
{
    // Legacy enum names retained for persisted template id compatibility.
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
    public const int ActiveCount = 4;

    public static readonly IReadOnlyList<int> AllIds =
        Enumerable.Range(1, Count).ToArray();

    // Active collection: the four redesigned poster directions are available to @bot 9/@bot 10.
    // Posters 5-10 remain implemented but disabled from new assignment until they receive their redesign pass.
    public static readonly IReadOnlyList<int> ActiveIds =
        [
            (int)TeamPosterTemplate.NeonArena,
            (int)TeamPosterTemplate.ChampionshipGold,
            (int)TeamPosterTemplate.CyberStorm,
            (int)TeamPosterTemplate.MonolithBroadcast
        ];

    public static bool IsValid(int templateId) => templateId is >= 1 and <= Count;

    public static bool IsActive(int templateId) => templateId is >= 1 and <= 4;

    public static string GetDisplayName(int templateId) => (TeamPosterTemplate)templateId switch
    {
        TeamPosterTemplate.NeonArena => "Court Index",
        TeamPosterTemplate.ChampionshipGold => "Hall of Champions",
        TeamPosterTemplate.CyberStorm => "Orbit League",
        TeamPosterTemplate.MonolithBroadcast => "Monolith Broadcast",
        TeamPosterTemplate.InfernoClash => "Inferno Clash",
        TeamPosterTemplate.RetroArcade => "Retro Arcade",
        TeamPosterTemplate.TitaniumLeague => "Titanium League",
        TeamPosterTemplate.VelocityWave => "Velocity Wave",
        TeamPosterTemplate.NoirSpotlight => "Noir Spotlight",
        TeamPosterTemplate.StreetClash => "Street Clash",
        _ => "Court Index"
    };
}
