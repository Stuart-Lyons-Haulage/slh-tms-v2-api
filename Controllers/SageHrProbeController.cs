using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Temporary diagnostic: which Sage HR endpoints could supply drivers' working patterns.
/// Field names and status codes only (no employee data). The probe runs in the background;
/// call again to read the result. Re-runs at most every 10 minutes.
/// </summary>
[ApiController]
[Route("api/v1/health/sage-hr-probe")]
public sealed class SageHrProbeController(IServiceScopeFactory scopes) : ControllerBase
{
    private static readonly object Sync = new();
    private static Task? running;
    private static DateTimeOffset? startedAtUtc;
    private static object? last;

    [HttpGet, AllowAnonymous]
    public IActionResult Get()
    {
        lock (Sync)
        {
            var stale = startedAtUtc is null || DateTimeOffset.UtcNow - startedAtUtc > TimeSpan.FromMinutes(10);
            if ((running is null || running.IsCompleted) && stale)
            {
                startedAtUtc = DateTimeOffset.UtcNow;
                running = Task.Run(RunAsync);
            }
            return Ok(new { running = running is { IsCompleted: false }, startedAtUtc, last });
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sage = scope.ServiceProvider.GetRequiredService<SageHrClient>();
            if (!sage.IsConfigured) { last = new { configured = false, missing = sage.MissingSettings }; return; }
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var results = await sage.ProbeWorkPatternSourcesAsync(budget.Token);
            last = new { configured = true, checkedAtUtc = DateTimeOffset.UtcNow, results };
        }
        catch (Exception ex)
        {
            last = new { failed = true, error = ex.GetType().Name, message = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message };
        }
    }
}
