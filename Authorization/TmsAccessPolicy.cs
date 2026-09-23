using System.Security.Claims;

namespace Slh.Tms.Api.Authorization;

internal static class TmsAccessPolicy
{
    private static readonly string[] DefaultAllowedDomains = ["lyonshaulage.com", "stuartlyonshaulage.co.uk"];
    private static readonly string[] TrustedSignInClaimTypes =
    [
        "preferred_username",
        "upn",
        "email",
        ClaimTypes.Email,
        ClaimTypes.Upn
    ];

    public static bool IsCompanyUser(ClaimsPrincipal user, IReadOnlyCollection<string>? allowedDomains = null)
    {
        if (user.Identity?.IsAuthenticated != true) return false;

        var domains = NormaliseAllowedDomains(allowedDomains);
        if (domains.Count == 0) return false;

        return user.Claims
            .Where(claim => TrustedSignInClaimTypes.Contains(claim.Type))
            .Select(claim => claim.Value)
            .Any(value => HasAllowedDomain(value, domains));
    }

    private static bool HasAllowedDomain(string? signInAddress, IReadOnlySet<string> allowedDomains)
    {
        if (string.IsNullOrWhiteSpace(signInAddress)) return false;

        var value = signInAddress.Trim();
        if (value.Any(char.IsWhiteSpace)) return false;

        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1) return false;
        if (value.IndexOf('@') != at) return false;

        var domain = value[(at + 1)..].TrimEnd('.');
        return allowedDomains.Contains(domain);
    }

    private static HashSet<string> NormaliseAllowedDomains(IReadOnlyCollection<string>? allowedDomains)
    {
        var source = allowedDomains is { Count: > 0 } ? allowedDomains : DefaultAllowedDomains;
        return source
            .Select(domain => domain.Trim().TrimStart('@').TrimEnd('.'))
            .Where(domain => !string.IsNullOrWhiteSpace(domain) && !domain.Any(char.IsWhiteSpace))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
