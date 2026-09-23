using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/assistant/order-duplicates")]
[Authorize]
public sealed class AssistantOrderDuplicatesController(TmsDbContext db, ILogger<AssistantOrderDuplicatesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Status([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var rows = await ReadOpenOrders(date, ct);
        var groups = ExactGroups(rows);
        var safe = 0;
        var review = 0;
        var examples = new List<object>();

        foreach (var group in groups.Take(25))
        {
            var ids = group.Select(x => x.Id).ToList();
            var linked = await SafeLinkedCounts(ids, ct);
            var canonical = ChooseCanonical(group, linked);
            var safeDuplicates = group.Where(x => x.Id != canonical.Id && linked.GetValueOrDefault(x.Id) == 0).ToList();
            safe += safeDuplicates.Count;
            review += group.Count - 1 - safeDuplicates.Count;
            examples.Add(new
            {
                reference = canonical.Reference,
                customer = canonical.CustomerCode,
                collectionDate = canonical.CollectionDate,
                records = group.Count,
                safeToRemove = safeDuplicates.Count,
                requiresReview = group.Count - 1 - safeDuplicates.Count
            });
        }

        return Ok(new
        {
            date,
            duplicateGroups = groups.Count,
            duplicateRecords = groups.Sum(group => group.Count - 1),
            safeToRemove = safe,
            requiresReview = review,
            examples,
            message = groups.Count == 0
                ? "No exact duplicate active orders were found."
                : $"Found {groups.Count} exact duplicate order group(s). {safe} duplicate record(s) can be cancelled safely; {review} linked/planned record(s) require planner review."
        });
    }

    [HttpPost("fix"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Fix([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var rows = await ReadOpenOrders(date, ct);
        var groups = ExactGroups(rows);
        var attempted = new List<(Guid Id, string Description)>();
        var skipped = new List<string>();

        foreach (var group in groups)
        {
            var ids = group.Select(x => x.Id).ToList();
            var linked = await SafeLinkedCounts(ids, ct);
            var canonical = ChooseCanonical(group, linked);

            foreach (var duplicate in group.Where(x => x.Id != canonical.Id))
            {
                if (linked.GetValueOrDefault(duplicate.Id) > 0)
                {
                    skipped.Add($"{duplicate.Reference} / {duplicate.CustomerCode}: duplicate record {duplicate.Id} is linked to a run and was left for planner review.");
                    continue;
                }

                duplicate.Status = OrderStatus.Cancelled;
                attempted.Add((duplicate.Id, $"Cancelled exact duplicate order {duplicate.Reference} / {duplicate.CustomerCode} ({duplicate.CollectionDate:dd/MM/yyyy}); retained {canonical.Id} as the canonical record."));
            }
        }

        if (attempted.Count > 0 || skipped.Count > 0)
        {
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "assistantfix",
                IdempotencyKey = $"assistant-order-duplicates:{Guid.NewGuid():N}",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { attempted = attempted.Select(x => x.Description), skipped, appliedAtUtc = DateTimeOffset.UtcNow }),
                Source = "SLH Assistant exact duplicate order repair",
                Status = StagingStatus.Promoted,
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewedBy = User.Identity?.Name,
                ReviewNote = $"Attempted to cancel {attempted.Count} unallocated exact duplicate order record(s); {skipped.Count} linked duplicate(s) retained for review."
            });
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var attemptedIds = attempted.Select(x => x.Id).ToList();
        var statuses = attemptedIds.Count == 0
            ? new Dictionary<Guid, OrderStatus>()
            : await db.TransportOrders.AsNoTracking().Where(x => attemptedIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Status, ct);
        var changes = attempted.Where(x => statuses.GetValueOrDefault(x.Id) == OrderStatus.Cancelled).Select(x => x.Description).ToList();
        var verificationFailures = attempted.Where(x => statuses.GetValueOrDefault(x.Id) != OrderStatus.Cancelled)
            .Select(x => $"Could not verify that duplicate order {x.Id} was cancelled in the live TMS.").ToList();
        var skippedReasons = skipped.Concat(verificationFailures).ToList();

        return Ok(new
        {
            attempted = attempted.Count,
            applied = changes.Count,
            verified = verificationFailures.Count == 0,
            skipped = skippedReasons.Count,
            changes,
            verificationFailures,
            skippedReasons
        });
    }

    private async Task<List<TransportOrder>> ReadOpenOrders(DateOnly? date, CancellationToken ct)
    {
        try
        {
            var query = db.TransportOrders.Where(x => x.Status != OrderStatus.Cancelled && x.Status != OrderStatus.Delivered);
            if (date is not null) query = query.Where(x => x.CollectionDate == date.Value);
            return await query.OrderBy(x => x.CreatedAtUtc).Take(5000).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant duplicate-order scan could not read primary orders.");
            return [];
        }
    }

    private static List<List<TransportOrder>> ExactGroups(IEnumerable<TransportOrder> orders) => orders
        .GroupBy(x => string.Join('|', new[]
        {
            Normalise(x.Reference), Normalise(x.CustomerCode), x.CollectionDate.ToString("yyyyMMdd"),
            x.DeliveryDate?.ToString("yyyyMMdd") ?? "", x.Pallets?.ToString() ?? "",
            Normalise(x.SellerName), Normalise(x.MarketName), Normalise(x.StallNumber)
        }), StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .Select(group => group.OrderBy(x => x.CreatedAtUtc).ToList())
        .ToList();

    private async Task<Dictionary<Guid, int>> SafeLinkedCounts(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        try
        {
            return await db.LoadStops.Where(stop => stop.OrderId != null && ids.Contains(stop.OrderId.Value))
                .GroupBy(stop => stop.OrderId!.Value)
                .Select(group => new { Id = group.Key, Count = group.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant duplicate-order scan could not verify linked planning stops; all duplicates will be review-only.");
            return ids.ToDictionary(id => id, _ => 1);
        }
    }

    private static TransportOrder ChooseCanonical(IReadOnlyCollection<TransportOrder> group, IReadOnlyDictionary<Guid, int> linked) => group
        .OrderByDescending(x => linked.GetValueOrDefault(x.Id))
        .ThenByDescending(x => StatusRank(x.Status))
        .ThenBy(x => x.CreatedAtUtc)
        .First();

    private static int StatusRank(OrderStatus status) => status switch
    {
        OrderStatus.InTransit => 5,
        OrderStatus.Planned => 4,
        OrderStatus.ReadyToPlan => 3,
        OrderStatus.Draft => 2,
        _ => 1
    };

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}