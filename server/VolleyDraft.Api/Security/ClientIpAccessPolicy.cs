using System.Net;

namespace VolleyDraft.Api.Security;

public sealed class ClientIpAccessPolicy(IConfiguration configuration)
{
    private readonly bool _enabled = configuration.GetValue<bool>("AccessControl:Enabled");
    private readonly string[] _allowedCidrs = configuration
        .GetSection("AccessControl:AllowedCidrs")
        .Get<string[]>()
        ?? [];

    public bool IsAllowed(HttpContext httpContext)
    {
        if (!_enabled)
        {
            return true;
        }

        var clientIp = GetClientIp(httpContext);
        return clientIp is not null && _allowedCidrs.Any(cidr => MatchesCidr(clientIp, cidr));
    }

    public static IPAddress? GetClientIp(HttpContext httpContext)
    {
        // Render public web services are fronted by Cloudflare. Cloudflare overwrites
        // CF-Connecting-IP with the connecting client's address, unlike X-Forwarded-For
        // which can contain values supplied by the caller before proxies append to it.
        var cloudflareClientIp = httpContext.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cloudflareClientIp) &&
            IPAddress.TryParse(cloudflareClientIp.Trim(), out var forwardedIp))
        {
            return Normalize(forwardedIp);
        }

        return httpContext.Connection.RemoteIpAddress is { } remoteIp
            ? Normalize(remoteIp)
            : null;
    }

    public static bool IsInfrastructureRequest(HttpContext httpContext)
    {
        var path = httpContext.Request.Path;
        if (path.StartsWithSegments("/api/settlement-access", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (HttpMethods.IsGet(httpContext.Request.Method) && IsSettlementWebEntry(httpContext))
        {
            return true;
        }

        if (HttpMethods.IsGet(httpContext.Request.Method) &&
            path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase))
        {
            // The settlement mobile entry is served by the same compiled React bundle as
            // the admin UI. Static assets contain no protected state; data/API access is
            // still enforced independently below the IP gate.
            return true;
        }

        if (HttpMethods.IsGet(httpContext.Request.Method) &&
            (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/health/scheduler", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (HttpMethods.IsPost(httpContext.Request.Method) &&
            (path.Equals("/api/internal/scheduler/tick", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/internal/zalo/events", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/internal/zalo/membership-events", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/internal/zalo/presence-events", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/internal/zalo/poll-events", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/internal/trend-music/render-claim", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/internal/trend-music/render-progress", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/internal/trend-music/render-complete", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/internal/trend-music/recover-daily-greeting", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/internal/zyro-mapping/profiles/sync", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (HttpMethods.IsGet(httpContext.Request.Method) && IsZyroMappingGroupReadPath(path))
        {
            return true;
        }

        if (!HttpMethods.IsGet(httpContext.Request.Method))
        {
            return false;
        }

        var value = path.Value ?? string.Empty;
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var isBotImage = segments.Length == 4 &&
                         segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                         segments[1].Equals("public", StringComparison.OrdinalIgnoreCase) &&
                         segments[2].Equals("bot-images", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(segments[3]);
        if (isBotImage)
        {
            return true;
        }

        return segments.Length == 5 &&
               segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
               segments[1].Equals("public", StringComparison.OrdinalIgnoreCase) &&
               segments[2].Equals("sessions", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(segments[3]) &&
               segments[4].Equals("team-card.png", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesCidr(IPAddress address, string cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
        {
            return false;
        }

        var parts = cidr.Trim().Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(parts[0], out var networkAddress))
        {
            return false;
        }

        address = Normalize(address);
        networkAddress = Normalize(networkAddress);

        if (address.AddressFamily != networkAddress.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();
        var maxBits = addressBytes.Length * 8;
        var prefixLength = maxBits;

        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], out prefixLength) || prefixLength < 0 || prefixLength > maxBits))
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var index = 0; index < fullBytes; index++)
        {
            if (addressBytes[index] != networkBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsSettlementWebEntry(HttpContext httpContext)
    {
        if (!httpContext.Request.Path.Equals("/app", StringComparison.OrdinalIgnoreCase))
            return false;

        var focus = httpContext.Request.Query["focus"].FirstOrDefault();
        var sessionId = httpContext.Request.Query["sessionId"].FirstOrDefault();
        return string.Equals(focus, "match-settlement", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(sessionId);
    }

    private static bool IsZyroMappingGroupReadPath(PathString path)
    {
        if (path.Equals("/api/internal/zyro-mapping/groups", StringComparison.OrdinalIgnoreCase))
            return true;

        var segments = (path.Value ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 7 &&
               segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
               segments[1].Equals("internal", StringComparison.OrdinalIgnoreCase) &&
               segments[2].Equals("zyro-mapping", StringComparison.OrdinalIgnoreCase) &&
               segments[3].Equals("groups", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(segments[4]) &&
               !string.IsNullOrWhiteSpace(segments[5]) &&
               segments[6].Equals("members", StringComparison.OrdinalIgnoreCase);
    }
}
