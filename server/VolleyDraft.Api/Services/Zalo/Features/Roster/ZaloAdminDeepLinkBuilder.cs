using VolleyDraft.Api.Contracts;

namespace VolleyDraft.Api.Services;

/// <summary>
/// Builds a direct admin URL only for lifecycle states that genuinely require the
/// website. Production prefers an explicit AdminWeb base URL and falls back to the
/// configured frontend CORS origin already required by the Render deployment.
/// </summary>
internal sealed class ZaloAdminDeepLinkBuilder(IConfiguration configuration)
{
    internal string? Build(MatchLifecycleResponse lifecycle)
    {
        var origins = configuration.GetSection("Cors:Origins")
            .GetChildren()
            .Select(item => item.Value);
        return Build(lifecycle, configuration["AdminWeb:BaseUrl"], origins);
    }

    /// <summary>
    /// Proactive reminder formatters do not own IConfiguration. Render exposes the
    /// same settings as environment variables, so this keeps old reminder plumbing
    /// untouched while still producing a one-tap exception link in production.
    /// </summary>
    internal static string? BuildFromEnvironment(MatchLifecycleResponse lifecycle) =>
        Build(
            lifecycle,
            Environment.GetEnvironmentVariable("AdminWeb__BaseUrl"),
            [Environment.GetEnvironmentVariable("Cors__Origins__0")]);

    private static string? Build(
        MatchLifecycleResponse lifecycle,
        string? explicitBase,
        IEnumerable<string?> fallbackOrigins)
    {
        if (!lifecycle.NeedsWebsite || string.IsNullOrWhiteSpace(lifecycle.WebTarget))
            return null;

        var origin = NormalizePublicOrigin(explicitBase);
        if (origin is null)
        {
            foreach (var value in fallbackOrigins)
            {
                origin = NormalizePublicOrigin(value);
                if (origin is not null) break;
            }
        }
        if (origin is null) return null;

        var focus = Uri.EscapeDataString(lifecycle.WebTarget.Trim());
        var sessionId = Uri.EscapeDataString(lifecycle.SessionId);
        return $"{origin}/app?focus={focus}&sessionId={sessionId}#{focus}";
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
