using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("health/dependencies")]
[Route("api/v1/health/dependencies")]
[AllowAnonymous]
public sealed class DependencyHealthController(DependencyHealthService health) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await health.GetSnapshotAsync(ct));
}
