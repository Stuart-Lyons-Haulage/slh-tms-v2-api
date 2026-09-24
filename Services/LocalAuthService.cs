using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record LocalLoginResult(string AccessToken, DateTimeOffset ExpiresAtUtc, string Username, string DisplayName, string Role);

public sealed class LocalAuthService(TmsDbContext db, IConfiguration configuration)
{
    private readonly PasswordHasher<TmsUser> _hasher = new();

    public async Task<LocalLoginResult?> LoginAsync(string username, string password, CancellationToken ct)
    {
        var normalised = NormaliseUsername(username);
        if (normalised.Length == 0 || string.IsNullOrWhiteSpace(password)) return null;

        var user = await db.TmsUsers.SingleOrDefaultAsync(x => x.Username == normalised && x.Active, ct);
        if (user is null) return null;

        var verified = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verified == PasswordVerificationResult.Failed) return null;

        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = _hasher.HashPassword(user, password);

        user.LastLoginAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Issue(user);
    }

    public async Task<TmsUser> CreateAsync(string username, string displayName, string password, string role, CancellationToken ct)
    {
        var normalised = NormaliseUsername(username);
        if (normalised.Length < 3) throw new InvalidOperationException("Username must contain at least three characters.");
        if (string.IsNullOrWhiteSpace(displayName)) throw new InvalidOperationException("Display name is required.");
        ValidatePassword(password);

        if (await db.TmsUsers.AnyAsync(x => x.Username == normalised, ct))
            throw new InvalidOperationException("That username already exists.");

        var user = new TmsUser
        {
            Username = normalised,
            DisplayName = displayName.Trim(),
            PasswordHash = string.Empty,
            Role = NormaliseRole(role),
            Active = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PasswordChangedAtUtc = DateTimeOffset.UtcNow
        };
        user.PasswordHash = _hasher.HashPassword(user, password);
        db.TmsUsers.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task ResetPasswordAsync(Guid userId, string password, CancellationToken ct)
    {
        ValidatePassword(password);
        var user = await db.TmsUsers.SingleAsync(x => x.Id == userId, ct);
        user.PasswordHash = _hasher.HashPassword(user, password);
        user.PasswordChangedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(Guid userId, bool active, CancellationToken ct)
    {
        var user = await db.TmsUsers.SingleAsync(x => x.Id == userId, ct);
        user.Active = active;
        await db.SaveChangesAsync(ct);
    }

    public async Task<TmsUser?> FindAsync(Guid id, CancellationToken ct) =>
        await db.TmsUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);

    public static string NormaliseUsername(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static string NormaliseRole(string? role)
    {
        var value = (role ?? string.Empty).Trim();
        return value.ToLowerInvariant() switch
        {
            "admin" or "tms.admin" => "TMS.Admin",
            "management" or "manager" => "TMS.Management",
            "planner" => "TMS.Planner",
            "transport" => "TMS.Transport",
            "warehouse" => "TMS.Warehouse",
            "accounts" => "TMS.Accounts",
            "readonly" or "read only" or "viewer" => "TMS.ReadOnly",
            _ => throw new InvalidOperationException("Unknown TMS role.")
        };
    }

    private LocalLoginResult Issue(TmsUser user)
    {
        var key = configuration["Auth:Local:SigningKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("Auth:Local:SigningKey must be at least 32 characters.");

        var issuer = configuration["Auth:Local:Issuer"] ?? "slh-tms-v2";
        var audience = configuration["Auth:Local:Audience"] ?? "slh-tms-v2";
        var expires = DateTimeOffset.UtcNow.AddHours(12);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim("preferred_username", user.Username),
            new Claim("auth_source", "local"),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("roles", user.Role)
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return new LocalLoginResult(
            new JwtSecurityTokenHandler().WriteToken(token),
            expires,
            user.Username,
            user.DisplayName,
            user.Role);
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
            throw new InvalidOperationException("Password must be at least 10 characters.");
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new InvalidOperationException("Password must contain letters and numbers.");
    }
}
