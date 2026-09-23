using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planning-day")]
[Authorize]
public sealed class PlanningDayResetController(
    TmsDbContext db,
    ILogger<PlanningDayResetController> logger) : ControllerBase
{
    [HttpGet("{date}/reset-preview")]
    public async Task<IActionResult> Preview(DateOnly date, CancellationToken ct)
    {
        var loadCount = await SafeCountLoads(date, ct);
        var orderCount = await SafeCountOrders(date, ct);
        var stagedCount = await CountStagedForDate(date, ct);
        return Ok(new
        {
            date,
            loads = loadCount,
            orders = orderCount,
            staged = stagedCount,
            confirmation = $"RESET-{date:yyyy-MM-dd}"
        });
    }

    [HttpDelete("{date}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reset(
        DateOnly date,
        [FromQuery] string confirm,
        CancellationToken ct)
    {
        var required = $"RESET-{date:yyyy-MM-dd}";
        if (!string.Equals(confirm, required, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                code = "confirmation_required",
                message = $"Add confirm={required} to reset only {date:dd MMM yyyy}."
            });
        }

        var warnings = new List<string>();
        var removedStops = 0;
        var cancelledLoads = 0;
        var cancelledOrders = 0;
        var archivedStaged = 0;

        try
        {
            var loadIds = await db.Loads
                .AsNoTracking()
                .Where(load =>
                    load.PlanningDate == date &&
                    load.Status != LoadStatus.Completed &&
                    load.Status != LoadStatus.Cancelled)
                .Select(load => load.Id)
                .ToListAsync(ct);

            var orderIds = await db.TransportOrders
                .AsNoTracking()
                .Where(order =>
                    order.Status != OrderStatus.Delivered &&
                    order.Status != OrderStatus.Cancelled &&
                    (order.CollectionDate == date || order.DeliveryDate == date))
                .Select(order => order.Id)
                .ToListAsync(ct);

            removedStops = await db.LoadStops
                .Where(stop =>
                    loadIds.Contains(stop.LoadId) ||
                    (stop.OrderId != null && orderIds.Contains(stop.OrderId.Value)))
                .ExecuteDeleteAsync(ct);

            cancelledLoads = await db.Loads
                .Where(load =>
                    load.PlanningDate == date &&
                    load.Status != LoadStatus.Completed &&
                    load.Status != LoadStatus.Cancelled)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(load => load.Status, LoadStatus.Cancelled),
                    ct);

            cancelledOrders = await db.TransportOrders
                .Where(order =>
                    order.Status != OrderStatus.Delivered &&
                    order.Status != OrderStatus.Cancelled &&
                    (order.CollectionDate == date || order.DeliveryDate == date))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(order => order.Status, OrderStatus.Cancelled),
                    ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Primary planning tables could not be fully reset for {PlanningDate}.", date);
            warnings.Add("Primary planning tables could not be fully reset; audited staging rows were still processed.");
            db.ChangeTracker.Clear();
        }

        try
        {
            var staged = await db.StagedImports
                .Where(item =>
                    item.EntityType == "order" ||
                    item.EntityType == "register:order" ||
                    item.EntityType == "planningload" ||
                    item.EntityType == "plannerplanrun" ||
                    item.EntityType == "plannerplansourcerun")
                .ToListAsync(ct);

            var matching = staged.Where(item => PayloadMatchesDate(item, date)).ToList();
            var now = DateTimeOffset.UtcNow;
            foreach (var item in matching)
            {
                item.EntityType = item.EntityType == "planningload"
                    ? "archived:planningload"
                    : item.EntityType == "plannerplanrun"
                        ? "archived:plannerplanrun"
                        : item.EntityType == "plannerplansourcerun"
                            ? "archived:plannerplansourcerun"
                        : "archived:order";
                item.IdempotencyKey = $"reset:{date:yyyyMMdd}:{item.Id:N}:{Guid.NewGuid():N}";
                item.Status = StagingStatus.Rejected;
                item.ReviewedAtUtc = now;
                item.ReviewedBy = User.Identity?.Name;
                item.ReviewNote = $"Archived by planning-day reset for {date:yyyy-MM-dd}. Source payload retained and import key released for a clean re-import.";
            }

            if (matching.Count > 0)
                await db.SaveChangesAsync(ct);
            archivedStaged = matching.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Staging cleanup failed for planning date {PlanningDate}.", date);
            warnings.Add("Some staged/fallback records could not be archived; review the staging queue before re-import.");
            db.ChangeTracker.Clear();
        }

        return Ok(new
        {
            date,
            cancelledLoads,
            cancelledOrders,
            removedStops,
            archivedStaged,
            warnings,
            message = warnings.Count == 0
                ? $"{date:dd MMM yyyy} is clear for re-import. Cancelled {cancelledLoads} run(s), {cancelledOrders} order(s), removed {removedStops} stop(s), and archived {archivedStaged} staged record(s)."
                : $"{date:dd MMM yyyy} reset completed with warnings. Review the response before importing the locked plan."
        });
    }

    private async Task<int> SafeCountLoads(DateOnly date, CancellationToken ct)
    {
        try
        {
            return await db.Loads.AsNoTracking().CountAsync(load =>
                load.PlanningDate == date &&
                load.Status != LoadStatus.Completed &&
                load.Status != LoadStatus.Cancelled, ct);
        }
        catch { return 0; }
    }

    private async Task<int> SafeCountOrders(DateOnly date, CancellationToken ct)
    {
        try
        {
            return await db.TransportOrders.AsNoTracking().CountAsync(order =>
                order.Status != OrderStatus.Delivered &&
                order.Status != OrderStatus.Cancelled &&
                (order.CollectionDate == date || order.DeliveryDate == date), ct);
        }
        catch { return 0; }
    }

    private async Task<int> CountStagedForDate(DateOnly date, CancellationToken ct)
    {
        try
        {
            var staged = await db.StagedImports.AsNoTracking()
                .Where(item =>
                    item.EntityType == "order" ||
                    item.EntityType == "register:order" ||
                    item.EntityType == "planningload" ||
                    item.EntityType == "plannerplanrun" ||
                    item.EntityType == "plannerplansourcerun")
                .ToListAsync(ct);
            return staged.Count(item => PayloadMatchesDate(item, date));
        }
        catch { return 0; }
    }

    private static bool PayloadMatchesDate(StagedImport item, DateOnly date)
    {
        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var root = document.RootElement;
            return DateProperty(root, "collectionDate") == date ||
                   DateProperty(root, "deliveryDate") == date ||
                   DateProperty(root, "planningDate") == date;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateOnly? DateProperty(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(Normalise(property.Name), Normalise(name), StringComparison.Ordinal))
                continue;
            if (property.Value.ValueKind == JsonValueKind.String &&
                DateOnly.TryParse(property.Value.GetString(), out var parsed))
                return parsed;
        }
        return null;
    }

    private static string Normalise(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
