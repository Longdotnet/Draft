namespace VolleyDraft.Api.Services;

public sealed record ZaloAmbientFactPilotSettings(
    bool Enabled,
    int MinimumScore)
{
    public static ZaloAmbientFactPilotSettings FromConfiguration(IConfiguration configuration) => new(
        Enabled: configuration.GetValue("ZaloBot:Ambient:FactPilot:Enabled", false),
        MinimumScore: Math.Clamp(configuration.GetValue("ZaloBot:Ambient:FactPilot:MinimumScore", 85), 65, 100));
}
