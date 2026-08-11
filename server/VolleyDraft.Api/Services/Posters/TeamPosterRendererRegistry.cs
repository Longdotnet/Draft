using VolleyDraft.Api.Services.Avatars;

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
        IReadOnlyList<TeamCardTeam> teams)
    {
        var enhancedTeams = CaptainAvatarSuperResolution.Apply(teams);
        return (TeamPosterTemplate)templateId switch
        {
            TeamPosterTemplate.NeonArena => CourtIndexCrispPortraitPosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            TeamPosterTemplate.ChampionshipGold => HallOfChampionsPosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            TeamPosterTemplate.CyberStorm => OrbitLeaguePosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            TeamPosterTemplate.MonolithBroadcast => ClashNightPosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            TeamPosterTemplate.InfernoClash => InfernoClashPosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            TeamPosterTemplate.RetroArcade => RetroArcadePosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            TeamPosterTemplate.TitaniumLeague => TitaniumLeaguePosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            TeamPosterTemplate.VelocityWave => VelocityWavePosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            TeamPosterTemplate.NoirSpotlight => NoirSpotlightPosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            TeamPosterTemplate.StreetClash => StreetClashPosterRenderer.Render(sessionName, startTime, location, enhancedTeams),
            _ => CourtIndexCrispPortraitPosterRenderer.Render(sessionName, startTime, location, enhancedTeams)
        };
    }
}
