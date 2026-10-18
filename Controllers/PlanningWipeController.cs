using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Clear the slate: permanently delete every order, booking and run dated on or before a day.
/// Both the check and the delete run in the background (reading every order and run record
/// takes longer than a browser will wait); the screen polls status. Only the system owner
/// (or a TMS.Admin), only today or earlier, and only after the check for that same date.
/// </summary>
[ApiController]
[Route("api/v1/admin/planning-wipe")]
[Authorize(Policy = "TmsOwner")]
public sealed class PlanningWipeController(IServiceScopeFactory scopes, ILogger<PlanningWipeController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static readonly object Sync = new();
    private static Job job = new("Idle", null, null, null, null);

    private sealed record Job(string State, DateOnly? Through, object? Preview, object? Result, string? Message);

    public sealed record CheckRequest(DateOnly Through);
    public sealed record WipeRequest(DateOnly Through, string? Confirm);

    /// <summary>What's happening now: Checking, Checked (with counts), Deleting, Done, Failed or Idle.</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        lock (Sync) return Ok(new { state = job.State, through = job.Through, preview = job.Preview, result = job.Result, message = job.Message });
    }

    [HttpPost("check")]
    public IActionResult Check(CheckRequest request)
    {
        if (Refuse(request.Through) is { } refusal) return refusal;
        if (!OneAtATime.Wait(0)) return Conflict(new { message = "A check or clear-out is already running." });
        Set(new Job("Checking", request.Through, null, null, null));
        Run(async db =>
        {
            var plan = await PlanningWipe.ResolveAsync(db, request.Through, CancellationToken.None);
            Set(new Job("Checked", request.Through, PlanningWipe.Summary(plan), null, null));
        }, request.Through, "check");
        return Accepted(new { state = "Checking" });
    }

    [HttpPost]
    public IActionResult Wipe(WipeRequest request)
    {
        if (Refuse(request.Through) is { } refusal) return refusal;
        if (!string.Equals(request.Confirm?.Trim(), request.Through.ToString("yyyy-MM-dd"), StringComparison.Ordinal))
            return BadRequest(new { message = $"Type {request.Through:yyyy-MM-dd} to confirm." });
        lock (Sync)
            if (job.State != "Checked" || job.Through != request.Through)
                return BadRequest(new { message = "Check what would go for this date first." });
        if (!OneAtATime.Wait(0)) return Conflict(new { message = "A check or clear-out is already running." });
        var user = User.Identity?.Name;
        Set(new Job("Deleting", request.Through, null, null, null));
        Run(async db =>
        {
            // Re-read at delete time so anything added since the check is included too.
            var plan = await PlanningWipe.ResolveAsync(db, request.Through, CancellationToken.None);
            var result = await PlanningWipe.ExecuteAsync(db, plan, user, logger, CancellationToken.None);
            Set(new Job("Done", request.Through, null, result, null));
            Slh.Tms.Api.Middleware.PlanningChangeNotificationMiddleware.ClearReadCache();
        }, request.Through, "clear-out", publish: true);
        return Accepted(new { state = "Deleting" });
    }

    private void Run(Func<TmsDbContext, Task> work, DateOnly through, string what, bool publish = false)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                await work(scope.ServiceProvider.GetRequiredService<TmsDbContext>());
                if (publish) scope.ServiceProvider.GetRequiredService<PlanningChangeNotifier>().Publish("POST /api/v1/admin/planning-wipe");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Planning {What} through {Through} failed; nothing was deleted.", what, through);
                Set(new Job("Failed", through, null, null, $"The {what} failed and nothing was deleted: {ex.GetBaseException().Message}"));
            }
            finally { OneAtATime.Release(); }
        });
    }

    private static void Set(Job next) { lock (Sync) job = next; }

    private BadRequestObjectResult? Refuse(DateOnly through)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        return through > today ? BadRequest(new { message = "Only today or earlier can be cleared." }) : null;
    }
}
