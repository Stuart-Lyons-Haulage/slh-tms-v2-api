using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Temporary diagnostic: which Sage HR endpoints could supply drivers' working patterns.
/// Field names and status codes only (no employee data). Cached so it cannot hammer Sage.
/// </summary>
[ApiController]
[Route("api/v1/health/sage-hr-probe")]
public sealed class SageHrProbeController(SageHrClient sage) : ControllerBase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (DateTimeOffset At, object Result)? cached;

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!sage.IsConfigured) return Ok(new { configured = false, missing = sage.MissingSettings });
        await Gate.WaitAsync(ct);
        try
        {
            if (cached is { } hit && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return Ok(hit.Result);
            var results = await sage.ProbeWorkPatternSourcesAsync(ct);
            var payload = new { configured = true, checkedAtUtc = DateTimeOffset.UtcNow, results };
            cached = (DateTimeOffset.UtcNow, payload);
            return Ok(payload);
        }
        finally { Gate.Release(); }
    }
}
