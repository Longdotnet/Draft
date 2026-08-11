namespace VolleyDraft.Api.Services.Posters;

public static class TeamPosterRendererRegistry
{
    public const int Width = PosterDrawing.Width;
    public const int Height = PosterDrawing.Height;

    public static byte[] Render(
        int templateId,
        string sessionName,
        DateTimeOffset? startTime,
        string? location,
        IReadOnlyList<TeamCardTeam> teams) => (TeamPosterTemplate)templateId switch
    {
        TeamPosterTemplate.NeonArena => CourtIndexCrispPortraitPosterRenderer.Render(sessionName, startTime, location, teams),
        TeamPosterTemplate.ChampionshipGold => HallOfChampionsPosterRenderer.Render(sessionName, startTime, location, teams),
        TeamPosterTemplate.CyberStorm => OrbitLeaguePosterRenderer.Render(sessionName, startTime, location, teams),
        TeamPosterTemplate.MonolithBroadcast => ClashNightPosterRenderer.Render(sessionName, startTime, location, teams),
        TeamPosterTemplate.InfernoClash => InfernoClashPosterRenderer.Render(sessionName, startTime, location, teams),
        TeamPosterTemplate.RetroArcade => RetroArcadePosterRenderer.Render(sessionName, startTime, location, teams),
        TeamPosterTemplate.TitaniumLeague => TitaniumLeaguePosterRenderer.Render(sessionName, startTime, location, teams),
        TeamPosterTemplate.VelocityWave => VelocityWavePosterRenderer.Render(sessionName, startTime, location, teams),
        TeamPosterTemplate.NoirSpotlight => NoirSpotlightPosterRenderer.Render(sessionName, startTime, location, teams),
        TeamPosterTemplate.StreetClash => StreetClashPosterRenderer.Render(sessionName, startTime, location, teams),
        _ => CourtIndexCrispPortraitPosterRenderer.Render(sessionName, startTime, location, teams)
    };
}
