using System.Security.Cryptography;
using System.Text;

namespace VolleyDraft.Api.Security;

public static class ZyroMappingInternalAuthorization
{
    public const string HeaderName = "x-zyro-mapping-key";

    public static bool IsConfigured(IConfiguration configuration)
    {
        var expected = configuration["ZyroMapping:InternalKey"] ?? string.Empty;
        return !string.IsNullOrWhiteSpace(expected) && Encoding.UTF8.GetByteCount(expected) >= 32;
    }

    public static bool IsAuthorized(HttpContext httpContext, IConfiguration configuration)
    {
        var expected = configuration["ZyroMapping:InternalKey"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        var supplied = httpContext.Request.Headers[HeaderName].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        if (expectedBytes.Length < 32 || suppliedBytes.Length != expectedBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
