using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Permanently removes every order, booking and run dated on or before a day, so planning can
/// start clean from the next day. Orders and bookings go by collection date (an overnight job
/// collected that day goes with that day's board); runs go by planning date.
/// Master data, drivers, vehicles, sites, emails and invoices are kept; invoices just lose
/// their link to the deleted order or run.
/// </summary>
public static class PlanningWipe
{
    // Order records held in the intake/register store.
    private static readonly string[] OrderTypes = ["order", "register:order", "archived:order"];

    // Planning records keyed to a run, an order or a date.
    private static readonly string[] PlanningTypes =
    [
        "planningload", "planningpalletallocation", "plannerplanrun", "plannerplansourcerun",
        "runoperational", "rundispatchstate", "loadcommercial", "orderchange", "planbaseline",
        "planchangeevent", "outbound-load-plan", "outbound-load-plan-draft", "warehousecheck"
    ];

    private static readonly string[] DateNames = ["planningDate", "date", "collectionDate", "deliveryDate", "runDate", "planDate", "loadDate"];

    public sealed record Plan(
        DateOnly Through,
        HashSet<Guid> Loads,
        HashSet<Guid> Runs,
        HashSet<Guid> Orders,
        HashSet<Guid> Bookings,
        HashSet<Guid> Staged,
        Dictionary<string, int> StagedByType);

    public static async Task<Plan> ResolveAsync(TmsDbContext db, DateOnly through, CancellationToken ct)
    {
        var loads = (await db.Loads.AsNoTracking().Where(x => x.PlanningDate <= through).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var runs = (await db.Runs.AsNoTracking().Where(x => x.PlanningDate <= through).Select(x => x.RunId).ToListAsync(ct)).ToHashSet();
        var orders = (await db.TransportOrders.AsNoTracking()
            .Where(x => x.CollectionDate <= through)
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var bookings = (await db.BookingReservations.AsNoTracking()
            .Where(x => x.CollectionDate <= through)
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();

        var staged = new HashSet<Guid>();
        var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Take(Guid id, string type) { if (staged.Add(id)) byType[type] = byType.GetValueOrDefault(type) + 1; }

        // Orders in the intake/register store (waiting, approved, promoted, rejected, archived).
        // Read a page at a time: these payloads are large and there are many of them.
        await foreach (var row in PagesAsync(db, OrderTypes, ct))
        {
            var dates = StagingQueueProjection.PlanningDates(row.PayloadJson);
            if (dates.Count == 0 || dates.Max() > through) continue;
            Take(row.Id, row.EntityType);
            foreach (var id in PayloadIds(row.PayloadJson, "id", "orderId", "transportOrderId")) orders.Add(id);
        }

        // Run records in the planning register carry their own id and planning date.
        await foreach (var row in PagesAsync(db, ["planningload"], ct))
        {
            var dates = Dates(row.PayloadJson);
            if (dates.Count == 0 || dates.Max() > through) continue;
            Take(row.Id, row.EntityType);
            foreach (var id in PayloadIds(row.PayloadJson, "id")) loads.Add(id);
        }

        // Everything else: a dated record goes when all its dates have gone (so a record for
        // a later day is kept even if it mentions an earlier order); an undated one goes when
        // it points at a deleted run or order.
        var gone = loads.Concat(runs).Concat(orders).Concat(bookings).ToHashSet();
        await foreach (var row in PagesAsync(db, PlanningTypes.Where(type => type != "planningload").ToArray(), ct))
        {
            var dates = Dates(row.PayloadJson);
            if (dates.Count > 0) { if (dates.Max() <= through) Take(row.Id, row.EntityType); }
            else if (ReferencesAny(row.IdempotencyKey, row.PayloadJson, gone)) Take(row.Id, row.EntityType);
        }

        // Intake rows behind a deleted order go too (when no kept order shares them).
        var sourceLinks = await db.TransportOrders.AsNoTracking()
            .Where(x => x.SourceStagedImportId != null)
            .Select(x => new { x.Id, Source = x.SourceStagedImportId!.Value })
            .ToListAsync(ct);
        var keptSources = sourceLinks.Where(x => !orders.Contains(x.Id)).Select(x => x.Source).ToHashSet();
        foreach (var link in sourceLinks.Where(x => orders.Contains(x.Id) && !keptSources.Contains(x.Source)))
            Take(link.Source, "order");

        return new Plan(through, loads, runs, orders, bookings, staged, byType);
    }

    private sealed record Row(Guid Id, string EntityType, string IdempotencyKey, string PayloadJson);

    private static async IAsyncEnumerable<Row> PagesAsync(TmsDbContext db, string[] types, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var skip = 0;
        while (true)
        {
            var page = await db.StagedImports.AsNoTracking()
                .Where(x => types.Contains(x.EntityType))
                .OrderBy(x => x.Id)
                .Skip(skip).Take(500)
                .Select(x => new Row(x.Id, x.EntityType, x.IdempotencyKey, x.PayloadJson))
                .ToListAsync(ct);
            foreach (var row in page) yield return row;
            if (page.Count < 500) yield break;
            skip += page.Count;
        }
    }

    public static object Summary(Plan plan) => new
    {
        through = plan.Through,
        runs = plan.Loads.Count,
        canonicalRuns = plan.Runs.Count,
        orders = plan.Orders.Count,
        bookings = plan.Bookings.Count,
        intakeAndPlanningRecords = plan.Staged.Count,
        recordsByType = plan.StagedByType.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value)
    };

    public static async Task<object> ExecuteAsync(TmsDbContext db, Plan plan, string? user, ILogger logger, CancellationToken ct)
    {
        var relational = db.Database.IsRelational();
        if (relational) db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        await using var transaction = relational ? await db.Database.BeginTransactionAsync(ct) : null;

        var loads = plan.Loads.ToList();
        var runs = plan.Runs.ToList();
        var orders = plan.Orders.ToList();
        var bookings = plan.Bookings.ToList();
        var staged = plan.Staged.ToList();
        var counts = new Dictionary<string, int>();
        void Count(string name, int value) { if (value > 0) counts[name] = counts.GetValueOrDefault(name) + value; }

        // 1. Intake history hanging off deleted intake rows.
        foreach (var batch in staged.Chunk(1000))
        {
            var revisions = await db.OrderRevisions.Where(x => batch.Contains(x.StagedImportId)).ToListAsync(ct);
            var revisionIds = revisions.Select(x => x.Id).ToList();
            List<OrderSourceLine> lines = revisionIds.Count == 0 ? [] : await db.OrderSourceLines.Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
            var lineIds = lines.Select(x => x.Id).ToList();
            List<PlanProposalAllocation> proposalAllocations = lineIds.Count == 0 ? [] : await db.PlanProposalAllocations.Where(x => lineIds.Contains(x.SourceLineId)).ToListAsync(ct);
            db.PlanProposalAllocations.RemoveRange(proposalAllocations); Count("planProposalAllocations", proposalAllocations.Count);
            await db.SaveChangesAsync(ct);
            db.OrderSourceLines.RemoveRange(lines); Count("orderSourceLines", lines.Count);
            await db.SaveChangesAsync(ct);
            db.OrderRevisions.RemoveRange(revisions); Count("orderRevisions", revisions.Count);
            var events = await db.StagedImportEvents.Where(x => batch.Contains(x.StagedImportId)).ToListAsync(ct);
            db.StagedImportEvents.RemoveRange(events); Count("intakeEvents", events.Count);
            foreach (var booking in await db.BookingReservations.Where(x => x.SourceStagedImportId != null && batch.Contains(x.SourceStagedImportId.Value)).ToListAsync(ct))
                booking.SourceStagedImportId = null;
            foreach (var order in await db.TransportOrders.Where(x => x.SourceStagedImportId != null && batch.Contains(x.SourceStagedImportId.Value)).ToListAsync(ct))
                order.SourceStagedImportId = null;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // 2. Invoices are kept; they just stop pointing at deleted orders, bookings and runs.
        foreach (var batch in loads.Concat(orders).Concat(bookings).Chunk(1000))
        {
            foreach (var invoice in await db.InvoiceRecords.Where(x => (x.LoadId != null && batch.Contains(x.LoadId.Value)) || (x.TransportOrderId != null && batch.Contains(x.TransportOrderId.Value)) || (x.BookingReservationId != null && batch.Contains(x.BookingReservationId.Value))).ToListAsync(ct))
            {
                if (invoice.LoadId is Guid l && plan.Loads.Contains(l)) invoice.LoadId = null;
                if (invoice.TransportOrderId is Guid o && plan.Orders.Contains(o)) invoice.TransportOrderId = null;
                if (invoice.BookingReservationId is Guid b && plan.Bookings.Contains(b)) invoice.BookingReservationId = null;
                Count("invoicesUnlinked", 1);
            }
            foreach (var line in await db.InvoiceRecordLines.Where(x => (x.LoadId != null && batch.Contains(x.LoadId.Value)) || (x.TransportOrderId != null && batch.Contains(x.TransportOrderId.Value)) || (x.BookingReservationId != null && batch.Contains(x.BookingReservationId.Value))).ToListAsync(ct))
            {
                if (line.LoadId is Guid l && plan.Loads.Contains(l)) line.LoadId = null;
                if (line.TransportOrderId is Guid o && plan.Orders.Contains(o)) line.TransportOrderId = null;
                if (line.BookingReservationId is Guid b && plan.Bookings.Contains(b)) line.BookingReservationId = null;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // 3. Things that point at orders.
        foreach (var batch in orders.Chunk(1000))
        {
            var bookingAllocations = await db.BookingReservationAllocations.Where(x => x.TransportOrderId != null && batch.Contains(x.TransportOrderId.Value)).ToListAsync(ct);
            db.BookingReservationAllocations.RemoveRange(bookingAllocations); Count("bookingAllocations", bookingAllocations.Count);
            var issues = await db.OrderReferenceIssues.Where(x => x.TransportOrderId != null && batch.Contains(x.TransportOrderId.Value)).ToListAsync(ct);
            var issueIds = issues.Select(x => x.Id).ToList();
            List<ReferenceChaseEvent> chases = issueIds.Count == 0 ? [] : await db.ReferenceChaseEvents.Where(x => issueIds.Contains(x.ReferenceIssueId)).ToListAsync(ct);
            db.ReferenceChaseEvents.RemoveRange(chases);
            await db.SaveChangesAsync(ct);
            db.OrderReferenceIssues.RemoveRange(issues); Count("referenceIssues", issues.Count);
            var runAllocations = await db.RunOrderAllocations.Where(x => batch.Contains(x.OrderId)).ToListAsync(ct);
            db.RunOrderAllocations.RemoveRange(runAllocations); Count("runOrderAllocations", runAllocations.Count);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // 4. Canonical runs (stops, allocations, status and tracking cascade with them).
        foreach (var batch in runs.Chunk(1000))
        {
            var allocations = await db.RunOrderAllocations.Where(x => batch.Contains(x.RunId)).ToListAsync(ct);
            db.RunOrderAllocations.RemoveRange(allocations); Count("runOrderAllocations", allocations.Count);
            db.RunStops.RemoveRange(await db.RunStops.Where(x => batch.Contains(x.RunId)).ToListAsync(ct));
            db.RunResourceAllocations.RemoveRange(await db.RunResourceAllocations.Where(x => batch.Contains(x.RunId)).ToListAsync(ct));
            db.RunStatusHistory.RemoveRange(await db.RunStatusHistory.Where(x => batch.Contains(x.RunId)).ToListAsync(ct));
            db.RunTrackingStates.RemoveRange(await db.RunTrackingStates.Where(x => batch.Contains(x.RunId)).ToListAsync(ct));
            foreach (var visit in await db.GeofenceVisits.Where(x => x.RunId != null && batch.Contains(x.RunId.Value)).ToListAsync(ct)) { visit.RunId = null; visit.RunStopId = null; }
            await db.SaveChangesAsync(ct);
            var rows = await db.Runs.Where(x => batch.Contains(x.RunId)).ToListAsync(ct);
            db.Runs.RemoveRange(rows); Count("canonicalRuns", rows.Count);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // 5. Runs (loads) and what records their day: stops, driver messages, ETAs.
        foreach (var batch in loads.Chunk(1000))
        {
            db.DriverStatusLogs.RemoveRange(await db.DriverStatusLogs.Where(x => batch.Contains(x.LoadId)).ToListAsync(ct));
            db.EtaSnapshots.RemoveRange(await db.EtaSnapshots.Where(x => batch.Contains(x.LoadId)).ToListAsync(ct));
            foreach (var visit in await db.GeofenceVisits.Where(x => x.LoadId != null && batch.Contains(x.LoadId.Value)).ToListAsync(ct)) { visit.LoadId = null; visit.LoadStopId = null; }
            foreach (var proposal in await db.PlanProposalRuns.Where(x => x.LiveLoadId != null && batch.Contains(x.LiveLoadId.Value)).ToListAsync(ct)) proposal.LiveLoadId = null;
            await db.SaveChangesAsync(ct);
            db.LoadStops.RemoveRange(await db.LoadStops.Where(x => batch.Contains(x.LoadId)).ToListAsync(ct));
            var rows = await db.Loads.Where(x => batch.Contains(x.Id)).ToListAsync(ct);
            db.Loads.RemoveRange(rows); Count("runs", rows.Count);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // 6. Orders and bookings.
        foreach (var batch in orders.Chunk(1000))
        {
            var rows = await db.TransportOrders.Where(x => batch.Contains(x.Id)).ToListAsync(ct);
            db.TransportOrders.RemoveRange(rows); Count("orders", rows.Count);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        foreach (var batch in bookings.Chunk(1000))
        {
            db.BookingReservationAllocations.RemoveRange(await db.BookingReservationAllocations.Where(x => batch.Contains(x.BookingReservationId)).ToListAsync(ct));
            db.BookingReservationRevisions.RemoveRange(await db.BookingReservationRevisions.Where(x => batch.Contains(x.BookingReservationId)).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            var rows = await db.BookingReservations.Where(x => batch.Contains(x.Id)).ToListAsync(ct);
            db.BookingReservations.RemoveRange(rows); Count("bookings", rows.Count);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // 7. The intake and planning register records themselves.
        foreach (var batch in staged.Chunk(1000))
        {
            var rows = await db.StagedImports.Where(x => batch.Contains(x.Id)).ToListAsync(ct);
            db.StagedImports.RemoveRange(rows); Count("intakeAndPlanningRecords", rows.Count);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // A short record of what was done (the deleted rows themselves are gone).
        db.OperationalHistoryEvents.Add(new OperationalHistoryEvent
        {
            EntityType = "planning-wipe",
            EntityId = Guid.NewGuid(),
            EventType = "PlanningWiped",
            PayloadJson = JsonSerializer.Serialize(new { through = plan.Through, summary = $"Deleted all orders, bookings and runs dated {plan.Through:yyyy-MM-dd} or earlier", deleted = counts }),
            Actor = user
        });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        logger.LogWarning("Planning wipe through {Through} by {User}: {Counts}", plan.Through, user, JsonSerializer.Serialize(counts));
        return new { through = plan.Through, deleted = counts };
    }

    private static List<Guid> PayloadIds(string json, params string[] names)
    {
        var result = new List<Guid>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String && Guid.TryParse(property.Value.GetString(), out var id))
                    result.Add(id);
        }
        catch (JsonException) { }
        return result;
    }

    internal static List<DateOnly> Dates(string json)
    {
        var result = new List<DateOnly>();
        try
        {
            using var document = JsonDocument.Parse(json);
            Walk(document.RootElement, 0);
        }
        catch (JsonException) { }
        return result;

        void Walk(JsonElement element, int depth)
        {
            if (depth > 3) return;
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && DateNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var text = property.Value.GetString();
                        if (text is { Length: >= 10 } && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", out var date)) result.Add(date);
                    }
                    else if (property.Value.ValueKind is JsonValueKind.Object) Walk(property.Value, depth + 1);
                }
            }
        }
    }

    private static bool ReferencesAny(string key, string json, HashSet<Guid> ids)
    {
        if (ids.Count == 0) return false;
        foreach (var part in key.Split(':', '|', '/'))
            if (Guid.TryParse(part, out var id) && ids.Contains(id)) return true;
        try
        {
            using var document = JsonDocument.Parse(json);
            return Scan(document.RootElement, 0);
        }
        catch (JsonException) { return false; }

        bool Scan(JsonElement element, int depth)
        {
            if (depth > 4) return false;
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString() is { Length: 32 or 36 } text && Guid.TryParse(text, out var id) && ids.Contains(id);
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject()) if (Scan(property.Value, depth + 1)) return true;
                    return false;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) if (Scan(item, depth + 1)) return true;
                    return false;
                default:
                    return false;
            }
        }
    }
}
