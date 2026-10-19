using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>Where the API's time has gone recently: slowest endpoints and slow calls.</summary>
[ApiController, Route("api/v1/diagnostics"), Authorize]
public sealed class PerformanceDiagnosticsController(RequestLatencyMonitor monitor) : ControllerBase
{
    [HttpGet("slow-endpoints")]
    public IActionResult SlowEndpoints() => Ok(monitor.Read(DateTimeOffset.UtcNow));
}
