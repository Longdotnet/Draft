using SkiaSharp;
using VolleyDraft.Api.Services.Avatars;

namespace VolleyDraft.Api.Services.Posters;

public static class TeamPosterRendererRegistry
{
    public const int Width = PosterDrawing.Width;
    public const int Height = PosterDrawing.Height;
    internal const int DeliveryWidth = 1080;
    internal const int DeliveryHeight = 1350;

    public static byte[] Render(
        int templateId,
        string sessionName,
        DateTimeOffset? startTime,
        string? location,
        IReadOnlyList<TeamCardTeam> teams)
    {
        var enhancedTeams = CaptainAvatarSuperResolution.Apply(teams);
        var rendered = (TeamPosterTemplate)templateId switch
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

        return OptimizePngForDelivery(rendered);
    }

    internal static byte[] OptimizePngForDelivery(byte[] rendered)
    {
        using var source = SKBitmap.Decode(rendered)
            ?? throw new InvalidOperationException("Could not decode rendered team poster.");

        if (source.Width <= DeliveryWidth && source.Height <= DeliveryHeight)
            return rendered;

        using var surface = SKSurface.Create(
            new SKImageInfo(DeliveryWidth, DeliveryHeight, SKColorType.Rgba8888, SKAlphaType.Opaque))
            ?? throw new InvalidOperationException("Could not create team-poster delivery surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium
        };
        canvas.DrawBitmap(source, new SKRect(0, 0, DeliveryWidth, DeliveryHeight), paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90)
            ?? throw new InvalidOperationException("Could not encode optimized team poster.");
        return data.ToArray();
    }
}
