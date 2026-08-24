using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Builds a direct admin URL only for lifecycle states that genuinely require the
/// website. Production prefers an explicit AdminWeb:BaseUrl and falls back to the
/// configured CORS origins, which already point at the deployed frontend on Render.
/// </summary>
internal sealed class ZaloAdminDeepLinkBuilder(IConfiguration configuration)
{
    internal string? Build(MatchLifecycleResponse lifecycle)
    {
        if (!lifecycle.NeedsWebsite || string.IsNullOrWhiteSpace(lifecycle.WebTarget))
            return null;

        var origin = ResolveFrontendOrigin();
        if (origin is null) return null;

        var focus = Uri.EscapeDataString(lifecycle.WebTarget.Trim());
        var sessionId = Uri.EscapeDataString(lifecycle.SessionId);
        return $"{origin}/app?focus={focus}&sessionId={sessionId}#{focus}";
    }

    private string? ResolveFrontendOrigin()
    {
        var explicitBase = NormalizePublicOrigin(configuration["AdminWeb:BaseUrl"]);
        if (explicitBase is not null) return explicitBase;

        foreach (var value in configuration.GetSection("Cors:Origins").GetChildren().Select(item => item.Value))
        {
            var normalized = NormalizePublicOrigin(value);
            if (normalized is not null) return normalized;
        }

        return null;
    }

    private static string? NormalizePublicOrigin(string? value)
    {
        var candidate = (value ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("https" or "http")) return null;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
            return null;

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
