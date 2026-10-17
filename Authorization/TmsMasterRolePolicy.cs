using System.Security.Claims;

namespace Slh.Tms.Api.Authorization;

internal static class TmsMasterRolePolicy
{
    public static bool CanReadMaster(ClaimsPrincipal user, IReadOnlyCollection<string> allowedDomains) =>
        TmsAccessPolicy.IsCompanyUser(user, allowedDomains);

    public static bool IsAdmin(ClaimsPrincipal user, IReadOnlyCollection<string> allowedDomains) =>
        TmsAccessPolicy.IsCompanyUser(user, allowedDomains) &&
        HasRole(user, "TMS.Admin", "Tms.Admin");

    private static bool HasRole(ClaimsPrincipal user, params string[] expected) =>
        user.Claims
            .Where(x => x.Type is ClaimTypes.Role or "roles")
            .Select(x => x.Value)
            .Any(actual => expected.Contains(actual, StringComparer.OrdinalIgnoreCase));
}
