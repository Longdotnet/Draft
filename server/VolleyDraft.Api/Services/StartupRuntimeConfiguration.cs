namespace VolleyDraft.Api.Services;

/// <summary>
/// Normalizes deployment-facing settings before they reach ASP.NET Core CORS or
/// the typed Zalo HTTP client. Render can preserve blank values for manually
/// managed environment variables, so whitespace must not be treated as a valid URL/key.
/// </summary>
public static class StartupRuntimeConfiguration
{
    public const string ProductionWebOrigin = "https://volley-draft.onrender.com";
    public const string DevelopmentBridgeBaseUrl = "http://localhost:3000";
    public const string DevelopmentBridgeInternalKey = "development-zalo-bridge-key";

    public static string[] GetAllowedCorsOrigins(IConfiguration configuration)
    {
        var configured = configuration
            .GetSection("Cors:Origins")
            .Get<string[]>()
            ?? [];

        return configured
            .Concat([
                ProductionWebOrigin,
                "http://127.0.0.1:5173",
                "http://localhost:5173"
            ])
            .Select(NormalizeOrigin)
            .Where(origin => origin is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static Uri GetZaloBridgeBaseUri(IConfiguration configuration)
    {
        var configured = configuration["Zalo:BridgeBaseUrl"];
        var raw = string.IsNullOrWhiteSpace(configured)
            ? DevelopmentBridgeBaseUrl
            : configured.Trim();
        var candidate = raw.TrimEnd('/') + "/";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "Zalo:BridgeBaseUrl must be an absolute HTTP(S) URL. " +
                "Example: https://your-zalo-bridge.onrender.com");
        }

        return uri;
    }

    public static string GetZaloBridgeInternalKey(IConfiguration configuration)
    {
        var configured = configuration["Zalo:BridgeInternalKey"];
        return string.IsNullOrWhiteSpace(configured)
            ? DevelopmentBridgeInternalKey
            : configured.Trim();
    }

    private static string? NormalizeOrigin(string? value)
    {
        var origin = (value ?? string.Empty).Trim().TrimEnd('/');
        if (origin.Length == 0) return null;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.PathAndQuery?.Trim('/')))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
