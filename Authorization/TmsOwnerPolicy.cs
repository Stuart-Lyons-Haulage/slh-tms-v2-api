using System.Security.Claims;

namespace Slh.Tms.Api.Authorization;

/// <summary>
/// For the few irreversible actions (the planning clear-out). Microsoft sign-ins don't carry
/// the TMS.Admin app role unless it's assigned in Entra, so the system owner is also named
/// here (setting Tms:Owners, comma separated; the TMS owner by default).
/// </summary>
internal static class TmsOwnerPolicy
{
    private static readonly string[] DefaultOwners = ["daniel@lyonshaulage.com"];
    private static readonly string[] SignInClaimTypes = ["preferred_username", "upn", "email", ClaimTypes.Email, ClaimTypes.Upn];

    public static bool IsOwner(ClaimsPrincipal user, IReadOnlyCollection<string> allowedDomains, IReadOnlyCollection<string>? owners)
    {
        if (TmsMasterRolePolicy.IsAdmin(user, allowedDomains)) return true;
        if (!TmsAccessPolicy.IsCompanyUser(user, allowedDomains) || user.HasClaim("auth_source", "local")) return false;
        var names = (owners is { Count: > 0 } ? owners : DefaultOwners)
            .Select(item => item.Trim()).Where(item => item.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return user.Claims.Any(claim => SignInClaimTypes.Contains(claim.Type) && names.Contains(claim.Value.Trim()));
    }
}
