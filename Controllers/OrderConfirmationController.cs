using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Confirmation workflow for transport orders. Orders start as AwaitingReview and
/// must be Confirmed before the planner can allocate them to a run. The planning
/// gate in PlannerAutosaveController and PlanningController rejects unconfirmed
/// orders.
/// </summary>
[ApiController]
[Route("api/v1/orders")]
[Authorize]
public sealed class OrderConfirmationController(TmsDbContext db) : ControllerBase
{
    /// <summary>
    /// Lists orders that are AwaitingReview for a given planning date.
    /// GET /api/v1/orders/awaiting-review?date=2026-10-22
    /// </summary>
    [HttpGet("awaiting-review")]
    public async Task<IActionResult> AwaitingReview([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var targetDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var orders = await db.TransportOrders
            .AsNoTracking()
            .Where(order => order.ConfirmationStatus == ConfirmationStatus.AwaitingReview
                         && order.CollectionDate == targetDate
                         && order.Status != OrderStatus.Cancelled)
            .OrderBy(order => order.Reference)
            .Select(order => new
            {
                order.Id,
                order.Reference,
                order.CustomerCode,
                order.CollectionDate,
                order.DeliveryDate,
                order.Pallets,
                order.SellerName,
                order.MarketName,
                order.Status,
                order.ConfirmationStatus,
                order.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(new { date = targetDate, count = orders.Count, orders });
    }

    /// <summary>
    /// Confirms one or more orders, making them eligible for planning.
    /// POST /api/v1/orders/confirm
    /// </summary>
    [HttpPost("confirm"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Confirm([FromBody] OrderConfirmationRequest request, CancellationToken ct)
    {
        if (request.Ids is null || request.Ids.Count == 0)
            return BadRequest(new { message = "Select at least one order to confirm." });
        if (request.Ids.Count > 500)
            return BadRequest(new { message = "A maximum of 500 orders can be confirmed at once." });

        var ids = request.Ids.Distinct().ToList();
        var orders = await db.TransportOrders
            .Where(order => ids.Contains(order.Id))
            .ToListAsync(ct);

        var confirmed = 0;
        var skipped = new List<object>();
        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown";

        foreach (var order in orders)
        {
            if (order.ConfirmationStatus != ConfirmationStatus.AwaitingReview)
            {
                skipped.Add(new { order.Id, order.Reference, reason = $"Already {order.ConfirmationStatus}" });
                continue;
            }

            order.ConfirmationStatus = ConfirmationStatus.Confirmed;
            order.ConfirmedAtUtc = DateTimeOffset.UtcNow;
            order.ConfirmedBy = actor;
            confirmed++;
        }

        if (confirmed > 0) await db.SaveChangesAsync(ct);

        return Ok(new
        {
            requested = ids.Count,
            confirmed,
            skipped = skipped.Count,
            skippedItems = skipped,
            message = confirmed == 0
                ? "No orders were confirmed — they may already be confirmed or rejected."
                : $"{confirmed} order{(confirmed == 1 ? "" : "s")} confirmed and now eligible for planning."
        });
    }

    /// <summary>
    /// Rejects one or more orders, removing them from the review queue.
    /// POST /api/v1/orders/reject
    /// </summary>
    [HttpPost("reject"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject([FromBody] OrderConfirmationRequest request, CancellationToken ct)
    {
        if (request.Ids is null || request.Ids.Count == 0)
            return BadRequest(new { message = "Select at least one order to reject." });
        if (request.Ids.Count > 500)
            return BadRequest(new { message = "A maximum of 500 orders can be rejected at once." });

        var ids = request.Ids.Distinct().ToList();
        var orders = await db.TransportOrders
            .Where(order => ids.Contains(order.Id))
            .ToListAsync(ct);

        var rejected = 0;
        var skipped = new List<object>();
        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown";

        foreach (var order in orders)
        {
            if (order.ConfirmationStatus == ConfirmationStatus.Rejected)
            {
                skipped.Add(new { order.Id, order.Reference, reason = "Already rejected" });
                continue;
            }

            order.ConfirmationStatus = ConfirmationStatus.Rejected;
            order.ConfirmedAtUtc = DateTimeOffset.UtcNow;
            order.ConfirmedBy = actor;
            rejected++;
        }

        if (rejected > 0) await db.SaveChangesAsync(ct);

        return Ok(new
        {
            requested = ids.Count,
            rejected,
            skipped = skipped.Count,
            skippedItems = skipped,
            message = rejected == 0
                ? "No orders were rejected — they may already be rejected."
                : $"{rejected} order{(rejected == 1 ? "" : "s")} rejected and removed from the review queue."
        });
    }
}

public sealed record OrderConfirmationRequest(List<Guid> Ids);
