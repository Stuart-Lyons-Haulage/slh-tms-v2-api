using System.Security.Claims;

namespace Slh.Tms.Api.Authorization;

internal static class TmsLocalRolePolicy
{
    private static readonly HashSet<string> WriteRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "TMS.Admin", "TMS.Management", "TMS.Planner", "TMS.Transport", "TMS.Warehouse", "TMS.Accounts"
    };

    private static readonly HashSet<string> ApproveRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "TMS.Admin", "TMS.Management", "TMS.Planner", "TMS.Transport"
    };

    public static bool CanRead(ClaimsPrincipal user, IReadOnlyCollection<string> allowedDomains) =>
        TmsAccessPolicy.IsCompanyUser(user, allowedDomains);

    public static bool CanWrite(ClaimsPrincipal user, IReadOnlyCollection<string> allowedDomains)
    {
        if (!TmsAccessPolicy.IsCompanyUser(user, allowedDomains)) return false;
        return !IsLocal(user) || Roles(user).Any(WriteRoles.Contains);
    }

    public static bool CanApprove(ClaimsPrincipal user, IReadOnlyCollection<string> allowedDomains)
    {
        if (!TmsAccessPolicy.IsCompanyUser(user, allowedDomains)) return false;
        return !IsLocal(user) || Roles(user).Any(ApproveRoles.Contains);
    }

    private static bool IsLocal(ClaimsPrincipal user) =>
        user.HasClaim("auth_source", "local");

    private static IEnumerable<string> Roles(ClaimsPrincipal user) =>
        user.Claims
            .Where(claim => claim.Type is ClaimTypes.Role or "roles")
            .Select(claim => claim.Value);
}
