using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Slh.Tms.Api.Tests;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // If no test header present, do not authenticate - this allows testing 401 behavior
        if (!Request.Headers.TryGetValue("X-Test-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };
        if (!Request.Headers.TryGetValue("X-Test-Name-Only", out var nameOnly) ||
            !string.Equals(nameOnly.ToString(), "true", StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new("preferred_username", HeaderValue("X-Test-Preferred-Username", user.ToString())));
            claims.Add(new("upn", HeaderValue("X-Test-Upn", user.ToString())));
            claims.Add(new(ClaimTypes.Email, HeaderValue("X-Test-Email", user.ToString())));
        }
        if (Request.Headers.TryGetValue("X-Test-Scopes", out var scopes))
        {
            claims.Add(new Claim("scp", scopes.ToString()));
        }
        if (Request.Headers.TryGetValue("X-Test-Roles", out var roles))
        {
            foreach (var role in roles.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                claims.Add(new Claim("roles", role));
            }
        }
        if (Request.Headers.TryGetValue("X-Test-Oid", out var oid)) claims.Add(new Claim("oid", oid.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string HeaderValue(string headerName, string fallback) =>
        Request.Headers.TryGetValue(headerName, out var value) ? value.ToString() : fallback;
}
