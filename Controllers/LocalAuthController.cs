using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/auth")]
public sealed class LocalAuthController(LocalAuthService auth, TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpPost("login"), AllowAnonymous]
    public async Task<IActionResult> Login(LocalLoginRequest request, CancellationToken ct)
    {
        if (!string.Equals(configuration["Auth:Mode"], "Local", StringComparison.OrdinalIgnoreCase))
            return NotFound();

        var result = await auth.LoginAsync(request.Username, request.Password, ct);
        return result is null ? Unauthorized(new { message = "Invalid username or password." }) : Ok(result);
    }

    [HttpGet("me"), Authorize]
    public IActionResult Me() => Ok(new
    {
        id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),
        username = User.FindFirstValue("preferred_username"),
        displayName = User.Identity?.Name,
        role = User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("roles"),
        authSource = User.FindFirstValue("auth_source") ?? "entra"
    });

    [HttpGet("users"), Authorize(Policy = "TmsAdmin")]
    public async Task<IActionResult> Users(CancellationToken ct) =>
        Ok(await db.TmsUsers.AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .Select(x => new { x.Id, x.Username, x.DisplayName, x.Role, x.Active, x.CreatedAtUtc, x.LastLoginAtUtc })
            .ToListAsync(ct));

    [HttpPost("users"), Authorize(Policy = "TmsAdmin")]
    public async Task<IActionResult> CreateUser(LocalCreateUserRequest request, CancellationToken ct)
    {
        try
        {
            var user = await auth.CreateAsync(request.Username, request.DisplayName, request.Password, request.Role, ct);
            return Ok(new { user.Id, user.Username, user.DisplayName, user.Role, user.Active });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("users/{id:guid}/password"), Authorize(Policy = "TmsAdmin")]
    public async Task<IActionResult> ResetPassword(Guid id, LocalPasswordRequest request, CancellationToken ct)
    {
        try
        {
            await auth.ResetPasswordAsync(id, request.Password, ct);
            return Ok(new { reset = true });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("users/{id:guid}/active"), Authorize(Policy = "TmsAdmin")]
    public async Task<IActionResult> SetActive(Guid id, LocalUserActiveRequest request, CancellationToken ct)
    {
        await auth.SetActiveAsync(id, request.Active, ct);
        return Ok(new { id, request.Active });
    }
}

public sealed record LocalLoginRequest(string Username, string Password);
public sealed record LocalCreateUserRequest(string Username, string DisplayName, string Password, string Role);
public sealed record LocalPasswordRequest(string Password);
public sealed record LocalUserActiveRequest(bool Active);
