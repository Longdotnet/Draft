using System.Security.Cryptography;
using System.Text;

namespace VolleyDraft.Api.Security;

public static class TrendMusicInternalAuthorization
{
    public const string HeaderName = "x-trend-music-key";

    public static bool IsConfigured(IConfiguration configuration)
    {
        var expected = configuration["ZaloBot:Ambient:TrendMusic:CallbackKey"] ?? string.Empty;
        return !string.IsNullOrWhiteSpace(expected) && Encoding.UTF8.GetByteCount(expected) >= 32;
    }

    public static bool IsAuthorized(HttpContext httpContext, IConfiguration configuration)
    {
        var expected = configuration["ZaloBot:Ambient:TrendMusic:CallbackKey"] ?? string.Empty;
        var supplied = httpContext.Request.Headers[HeaderName].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return !string.IsNullOrWhiteSpace(expected) &&
               expectedBytes.Length >= 32 &&
               suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
