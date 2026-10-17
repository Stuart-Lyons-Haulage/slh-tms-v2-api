using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Slh.Tms.Api.Services;

public static class TvWallboardAccess
{
    public const string HeaderName = "X-TMS-TV-Key";

    public static bool IsAllowed(HttpContext context, IConfiguration configuration)
    {
        // Signed-in portal requests already carry a validated bearer token. Azure AD
        // tokens can legitimately omit optional email/name claims, so authenticated
        // users must not depend on a particular claim shape to use the wallboard APIs.
        if (context.User.Identity?.IsAuthenticated == true) return true;

        var configuredKey = ConfiguredKey(configuration);
        if (string.IsNullOrWhiteSpace(configuredKey)) return false;

        var suppliedKey = ReadSuppliedKey(context.Request);
        return !string.IsNullOrWhiteSpace(suppliedKey) && FixedEquals(configuredKey, suppliedKey);
    }

    /// <summary>
    /// Returns the existing server-side read-only wallboard key when it is strong enough
    /// for public-TV transport. The six-digit pairing code remains the gate used to reveal
    /// this key; using the same key afterwards lets older TV browsers authenticate every
    /// wallboard feed via ?key= even when they strip custom request headers.
    /// </summary>
    public static string? ConfiguredKey(IConfiguration configuration)
    {
        var value = ReadConfiguredKey(configuration);
        return string.IsNullOrWhiteSpace(value) || value.Length < 24 ? null : value;
    }

    private static string? ReadConfiguredKey(IConfiguration configuration) =>
        new[]
        {
            "TvWallboard:AccessKey",
            "TvWallboard__AccessKey",
            "TV_WALLBOARD_ACCESS_KEY",
            "tv-wallboard-access-key"
        }
        .Select(key => configuration[key])
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?.Trim();

    private static string? ReadSuppliedKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out var headerValue))
            return headerValue.FirstOrDefault()?.Trim();
        if (request.Headers.TryGetValue("X-TV-Display-Key", out var displayHeaderValue))
            return displayHeaderValue.FirstOrDefault()?.Trim();
        if (request.Query.TryGetValue("key", out var queryValue))
            return queryValue.FirstOrDefault()?.Trim();
        return null;
    }

    private static bool FixedEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
