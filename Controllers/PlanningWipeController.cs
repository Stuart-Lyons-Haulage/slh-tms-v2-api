using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Clear the slate: permanently delete every order, booking and run dated on or before a day.
/// GET shows what would go; POST does it, only for an admin and only when the confirmation
/// matches the date. Today or earlier only, never a future day.
/// </summary>
[ApiController]
[Route("api/v1/admin/planning-wipe")]
[Authorize(Policy = "TmsAdmin")]
public sealed class PlanningWipeController(TmsDbContext db, IServiceScopeFactory scopes, ILogger<PlanningWipeController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static object? lastResult;
    private static string state = "Idle";

    public sealed record WipeRequest(DateOnly Through, string? Confirm);

    [HttpGet, DisableRequestTimeout]
    public async Task<IActionResult> Preview([FromQuery] DateOnly through, CancellationToken ct)
    {
        if (Refuse(through) is { } refusal) return refusal;
        return Ok(PlanningWipe.Summary(await PlanningWipe.ResolveAsync(db, through, ct)));
    }

    /// <summary>Whether a clear-out is running, and what the last one deleted.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new { state, result = lastResult });

    /// <summary>Starts the clear-out in the background (it can take a few minutes); poll status.</summary>
    [HttpPost]
    public IActionResult Wipe(WipeRequest request)
    {
        if (Refuse(request.Through) is { } refusal) return refusal;
        if (!string.Equals(request.Confirm?.Trim(), request.Through.ToString("yyyy-MM-dd"), StringComparison.Ordinal))
            return BadRequest(new { message = $"Type {request.Through:yyyy-MM-dd} to confirm." });
        if (!OneAtATime.Wait(0)) return Conflict(new { message = "A clear-out is already running." });
        var user = User.Identity?.Name;
        state = "Running";
        lastResult = null;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
                var plan = await PlanningWipe.ResolveAsync(scopedDb, request.Through, CancellationToken.None);
                lastResult = await PlanningWipe.ExecuteAsync(scopedDb, plan, user, logger, CancellationToken.None);
                state = "Done";
                Slh.Tms.Api.Middleware.PlanningChangeNotificationMiddleware.ClearReadCache();
                scope.ServiceProvider.GetRequiredService<PlanningChangeNotifier>().Publish("POST /api/v1/admin/planning-wipe");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Planning wipe through {Through} failed and was rolled back.", request.Through);
                lastResult = new { message = $"Nothing was deleted: {ex.GetBaseException().Message}" };
                state = "Failed";
            }
            finally { OneAtATime.Release(); }
        });
        return Accepted(new { state });
    }

    private BadRequestObjectResult? Refuse(DateOnly through)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        return through > today ? BadRequest(new { message = "Only today or earlier can be cleared." }) : null;
    }
}
