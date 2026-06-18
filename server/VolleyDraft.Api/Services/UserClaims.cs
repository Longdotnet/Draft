using System.Security.Claims;

namespace VolleyDraft.Api.Services;

public static class UserClaims
{
    public static string? GetUserId(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
