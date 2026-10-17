using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/diagnostics/order-review-schema")]
public sealed class OrderReviewSchemaHealthController(TmsDbContext db) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var checks = new Dictionary<string, object>();
        var healthy = true;

        healthy &= await Check("stagedImportEvents", db.StagedImportEvents.AsNoTracking(), checks, ct);
        healthy &= await Check("orderMovements", db.OrderMovements.AsNoTracking(), checks, ct);
        healthy &= await Check("orderRevisions", db.OrderRevisions.AsNoTracking(), checks, ct);
        healthy &= await Check("orderSourceLines", db.OrderSourceLines.AsNoTracking(), checks, ct);

        var response = new
        {
            status = healthy ? "Healthy" : "Unhealthy",
            message = healthy
                ? "Order Control review persistence schema is available."
                : "Order Control review persistence schema is incomplete. Approval/rejection should not be relied on until the repair script succeeds.",
            checks
        };

        return healthy ? Ok(response) : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    private static async Task<bool> Check<TEntity>(
        string name,
        IQueryable<TEntity> query,
        IDictionary<string, object> checks,
        CancellationToken ct)
    {
        try
        {
            var count = await query.CountAsync(ct);
            await query.Take(1).ToListAsync(ct);
            checks[name] = new { ok = true, count };
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex.GetBaseException().Message;
            checks[name] = new
            {
                ok = false,
                error = message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase)
                    ? message
                    : "The mapped table could not be read using the current application schema."
            };
            return false;
        }
    }
}
