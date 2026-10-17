using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning")]
[Authorize]
public sealed class PlannerPlanImportController(TmsDbContext db) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [HttpPost("import-plan"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ImportPlan(PlannerPlanImportRequest request, CancellationToken ct)
    {
        if (request.Runs is null || request.Runs.Count == 0)
            return BadRequest(new ErrorResponse("planner_plan_empty", "At least one planner run is required.", HttpContext.TraceIdentifier));
        if (request.Runs.Any(run => run.PlanningDate != request.PlanningDate))
            return BadRequest(new ErrorResponse("planning_date_mismatch", "Every run must use the root planningDate.", HttpContext.TraceIdentifier));
        if (request.Runs.Any(run => string.IsNullOrWhiteSpace(run.RunRef)))
            return BadRequest(new ErrorResponse("run_ref_required", "Every run must have a RunRef.", HttpContext.TraceIdentifier));
        if (request.Runs.GroupBy(run => run.RunRef, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return BadRequest(new ErrorResponse("duplicate_run_ref", "RunRef values must be unique within the import.", HttpContext.TraceIdentifier));

        var activeDrivers = await db.Drivers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var activeVehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var activeTrailers = await db.Trailers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        List<TransportOrder> orders;
        try { orders = await db.TransportOrders.AsNoTracking().ToListAsync(ct); }
        catch (Exception ex) when (IsSchemaUnavailable(ex)) { db.ChangeTracker.Clear(); orders = await PlanningRegisterStore.ReadOrdersAsync(db, request.PlanningDate, request.PlanningDate.AddDays(2), ct); }
        var orderDetails = await ReadOrderDetails(request.PlanningDate, ct);

        var warnings = new List<string>();
        var unresolvedDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedTrailers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PlannerPlanRunResult>();
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var held = 0;

        foreach (var run in request.Runs)
        {
            var tmsReference = PlannerPlanImportRules.TmsReference(request.PlanningDate, run.RunRef);
            var capacity = PlannerPlanImportRules.Capacity(run);

            if (!run.IncludeInImport)
            {
                held++;
                results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, "Held", capacity.Status, capacity.UtilisationPercent, run.ReconciliationStatus));
                continue;
            }

            if (run.Stops is null || run.Stops.Count == 0)
            {
                held++;
                warnings.Add($"{run.RunRef}: no stops supplied; run held.");
                results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, "Held", capacity.Status, capacity.UtilisationPercent, "No stops supplied"));
                continue;
            }

            var driver = ResolveDriver(activeDrivers, run.Driver);
            var vehicle = ResolveVehicle(activeVehicles, run.Vehicle);
            var trailer = ResolveTrailer(activeTrailers, run.Trailer);
            if (!string.IsNullOrWhiteSpace(run.Driver) && driver is null && !IsPlaceholder(run.Driver)) unresolvedDrivers.Add(run.Driver.Trim());
            if (!string.IsNullOrWhiteSpace(run.Vehicle) && vehicle is null) unresolvedVehicles.Add(run.Vehicle.Trim());
            if (!string.IsNullOrWhiteSpace(run.Trailer) && trailer is null) unresolvedTrailers.Add(run.Trailer.Trim());
            if (capacity.Status == "Red") warnings.Add($"{run.RunRef}: trailer footprint is {capacity.UtilisationPercent:0.0}% and requires planner review.");
            if (capacity.Status == "Amber") warnings.Add($"{run.RunRef}: pallet type is incomplete so trailer capacity cannot be fully confirmed.");

            Load? load;
            var registerFallback = false;
            try { load = await db.Loads.Include(x => x.Stops).SingleOrDefaultAsync(x => x.Reference == tmsReference, ct); }
            catch (Exception ex) when (IsSchemaUnavailable(ex))
            {
                db.ChangeTracker.Clear();
                load = (await PlanningRegisterStore.ReadLoadsAsync(db, request.PlanningDate, ct)).SingleOrDefault(x => string.Equals(x.Reference, tmsReference, StringComparison.OrdinalIgnoreCase));
                registerFallback = true;
            }

            var auditKey = $"planimport:{request.PlanningDate:yyyyMMdd}:{run.RunRef.ToUpperInvariant()}";
            var runJson = JsonSerializer.Serialize(run, JsonOptions);
            var audit = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == auditKey, ct);
            var samePayload = audit is not null && string.Equals(audit.PayloadJson, runJson, StringComparison.Ordinal);
            var importAllocations = BuildImportAllocations(run, orders, orderDetails);

            if (load is null)
            {
                load = new Load { Id = Guid.NewGuid(), Reference = tmsReference, PlanningDate = request.PlanningDate };
                created++;
            }
            else if (samePayload)
            {
                var allocationChanges = await PlanningAllocationStore.SyncPlannerImportAllocationsAsync(db, load.Id, request.PlanningDate, importAllocations, User.Identity?.Name, ct);
                if (allocationChanges > 0)
                {
                    updated++;
                    results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, "Updated", capacity.Status, capacity.UtilisationPercent, $"{allocationChanges} pallet allocation{(allocationChanges == 1 ? string.Empty : "s")} backfilled for Pallet Control."));
                    continue;
                }
                unchanged++;
                // Reapply derived fields on an identical import. Earlier imports may
                // have created the run before pallet capacity persistence was added;
                // treating the payload as a no-op leaves those run totals blank.
            }
            else updated++;

            load.DriverId = driver?.Id;
            load.VehicleId = vehicle?.Id;
            load.TrailerId = trailer?.Id;
            load.Status = driver is not null && vehicle is not null ? LoadStatus.Planned : LoadStatus.Draft;
            load.PalletSpacesUsed = capacity.StandardEquivalentUsed;
            load.TotalPalletSpaces = capacity.StandardEquivalentCapacity;
            load.CapacityType = "Mixed Standard/Euro";
            load.PlannerNotes = AddFirstCollectionWalkroundNote(PlannerPlanImportRules.BuildPlannerNotes(run, capacity), run);

            if (registerFallback)
            {
                load.Stops = BuildStops(load.Id, run, orders, orderDetails);
                await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
            }
            else
            {
                if (db.Entry(load).State == EntityState.Detached)
                {
                    load.Stops = BuildStops(load.Id, run, orders, orderDetails);
                    db.Loads.Add(load);
                }
                else if (!samePayload)
                {
                    var replacementStops = BuildStops(load.Id, run, orders, orderDetails);
                    db.LoadStops.RemoveRange(load.Stops.ToList());
                    db.LoadStops.AddRange(replacementStops);
                    load.Stops = replacementStops;
                }

                await db.SaveChangesAsync(ct);
                await LoadCommercialStore.SaveAsync(db, load, new LoadCommercialValues(null, null, null, null, null, null, null, null,
                    capacity.StandardEquivalentUsed, capacity.StandardEquivalentCapacity, "Mixed Standard/Euro", null, null, load.PlannerNotes), User.Identity?.Name, ct);
            }
            var allocationChangesForRun = await PlanningAllocationStore.SyncPlannerImportAllocationsAsync(db, load.Id, request.PlanningDate, importAllocations, User.Identity?.Name, ct);

            await SaveImportAuditAsync(db, auditKey, runJson, tmsReference, User.Identity?.Name, ct);

            var detail = allocationChangesForRun > 0
                ? $"{capacity.Message} {allocationChangesForRun} pallet allocation{(allocationChangesForRun == 1 ? string.Empty : "s")} written for Pallet Control.".Trim()
                : capacity.Message;
            results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, load.Status == LoadStatus.Planned ? "ImportedPlanned" : "ImportedDraft", capacity.Status, capacity.UtilisationPercent, detail));
        }

        return Ok(new PlannerPlanImportSummary(
            request.PlanningDate,
            request.Runs.Count,
            created,
            updated,
            unchanged,
            held,
            warnings,
            unresolvedDrivers.OrderBy(x => x).ToList(),
            unresolvedVehicles.OrderBy(x => x).ToList(),
            unresolvedTrailers.OrderBy(x => x).ToList(),
            results));
    }

    private static async Task SaveImportAuditAsync(TmsDbContext db, string auditKey, string runJson, string tmsReference, string? reviewedBy, CancellationToken ct)
    {
        var audit = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == auditKey, ct);
        if (audit is null)
        {
            audit = new StagedImport { EntityType = "plannerplanrun", IdempotencyKey = auditKey, PayloadJson = runJson, Source = "Planner plan import" };
            db.StagedImports.Add(audit);
        }

        audit.PayloadJson = runJson;
        audit.Status = StagingStatus.Promoted;
        audit.ReviewedAtUtc = DateTimeOffset.UtcNow;
        audit.ReviewedBy = reviewedBy;
        audit.ReviewNote = $"Imported as {tmsReference}.";
        await db.SaveChangesAsync(ct);
    }

    private static List<LoadStop> BuildStops(Guid loadId, PlannerPlanRunRequest run, IReadOnlyCollection<TransportOrder> orders, IReadOnlyDictionary<string, OrderDetail> orderDetails)
    {
        var sourceRows = run.Stops.OrderBy(stop => stop.Sequence).ToList();
        var stops = new List<LoadStop>();

        foreach (var group in GroupByCollectionWindow(sourceRows))
        {
            var detail = BuildGroupedStopDetail(group.Rows, includeDelivery: true);
            if (stops.Count == 0)
            {
                var window = FirstCollectionWindow(group.Rows);
                var instruction = FirstCollectionWalkroundInstruction(window);
                if (!string.IsNullOrWhiteSpace(instruction)) detail = string.IsNullOrWhiteSpace(detail) ? instruction : $"{instruction} | {detail}";
            }

            stops.Add(new LoadStop
            {
                Id = Guid.NewGuid(),
                LoadId = loadId,
                Sequence = stops.Count + 1,
                Name = Clip($"Collect · {group.Site}", 200)!,
                Address = Clip(detail, 500),
                PlannedArrivalUtc = EarliestPlannerTime(run.PlanningDate, group.Rows.Select(stop => stop.CollectFrom ?? stop.CollectTo))
            });
        }

        foreach (var group in GroupByDeliveryDeadline(sourceRows))
        {
            var order = FirstMatchingOrder(group.Rows, orders, orderDetails, new HashSet<Guid>(), run.PlanningDate);
            stops.Add(new LoadStop
            {
                Id = Guid.NewGuid(),
                LoadId = loadId,
                OrderId = order?.Id,
                Sequence = stops.Count + 1,
                Name = Clip($"Deliver · {group.Site}", 200)!,
                Address = Clip(BuildGroupedStopDetail(group.Rows, includeDelivery: false), 500),
                PlannedArrivalUtc = EarliestPlannerTime(run.PlanningDate, group.Rows.Select(stop => stop.Deadline))
            });
        }

        return stops.Count > 0
            ? stops
            : sourceRows.Select((stop, index) => new LoadStop
            {
                Id = Guid.NewGuid(),
                LoadId = loadId,
                Sequence = index + 1,
                Name = Clip(PlannerPlanImportRules.StopName(stop), 200)!,
                Address = Clip(BuildStopDetail(stop), 500),
                PlannedArrivalUtc = ParsePlannerTime(run.PlanningDate, stop.CollectFrom ?? stop.Deadline)
            }).ToList();
    }

    private static IEnumerable<(string Site, string WindowKey, List<PlannerPlanStopRequest> Rows)> GroupByCollectionWindow(IReadOnlyCollection<PlannerPlanStopRequest> rows) =>
        GroupBySiteAndWindow(rows, stop => stop.CollectionSite, stop => $"{Clean(stop.CollectFrom)}-{Clean(stop.CollectTo)}");

    private static IEnumerable<(string Site, string WindowKey, List<PlannerPlanStopRequest> Rows)> GroupByDeliveryDeadline(IReadOnlyCollection<PlannerPlanStopRequest> rows) =>
        GroupBySiteAndWindow(rows, stop => stop.DeliverySite, stop => Clean(stop.Deadline));

    private static IEnumerable<(string Site, string WindowKey, List<PlannerPlanStopRequest> Rows)> GroupBySiteAndWindow(
        IReadOnlyCollection<PlannerPlanStopRequest> rows,
        Func<PlannerPlanStopRequest, string?> siteSelector,
        Func<PlannerPlanStopRequest, string> windowSelector)
    {
        var groups = new List<(string Site, string WindowKey, List<PlannerPlanStopRequest> Rows)>();
        foreach (var row in rows)
        {
            var site = siteSelector(row)?.Trim();
            if (string.IsNullOrWhiteSpace(site)) continue;
            var window = windowSelector(row);
            var existingIndex = groups.FindIndex(group => string.Equals(Normalize(group.Site), Normalize(site), StringComparison.Ordinal) && string.Equals(group.WindowKey, window, StringComparison.Ordinal));
            if (existingIndex >= 0) groups[existingIndex].Rows.Add(row);
            else groups.Add((site, window, [row]));
        }
        return groups;
    }

    private static List<(Guid OrderId, int Pallets)> BuildImportAllocations(
        PlannerPlanRunRequest run,
        IReadOnlyCollection<TransportOrder> orders,
        IReadOnlyDictionary<string, OrderDetail> orderDetails)
    {
        var allocations = new List<(Guid OrderId, int Pallets)>();
        var used = new HashSet<Guid>();
        foreach (var row in (run.Stops ?? []).OrderBy(stop => stop.Sequence))
        {
            if (row.Pallets is null || row.Pallets <= 0 || decimal.Truncate(row.Pallets.Value) != row.Pallets.Value) continue;
            var order = FirstMatchingOrder([row], orders, orderDetails, used, run.PlanningDate);
            if (order is null) continue;
            allocations.Add((order.Id, decimal.ToInt32(row.Pallets.Value)));
            used.Add(order.Id);
        }
        return allocations;
    }

    private static TransportOrder? FirstMatchingOrder(
        IEnumerable<PlannerPlanStopRequest> rows,
        IReadOnlyCollection<TransportOrder> orders,
        IReadOnlyDictionary<string, OrderDetail> orderDetails,
        ISet<Guid> usedOrderIds,
        DateOnly planningDate)
    {
        foreach (var row in rows)
        {
            var order = MatchOrder(row, orders, orderDetails, usedOrderIds, planningDate);
            if (order is not null) return order;
        }
        return null;
    }

    private static TransportOrder? MatchOrder(
        PlannerPlanStopRequest row,
        IReadOnlyCollection<TransportOrder> orders,
        IReadOnlyDictionary<string, OrderDetail> orderDetails,
        ISet<Guid> usedOrderIds,
        DateOnly planningDate)
    {
        if (!string.IsNullOrWhiteSpace(row.Reference))
        {
            var exact = orders.FirstOrDefault(item => !usedOrderIds.Contains(item.Id) && string.Equals(item.Reference, row.Reference, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        if (row.Pallets is null || decimal.Truncate(row.Pallets.Value) != row.Pallets.Value) return null;
        var pallets = decimal.ToInt32(row.Pallets.Value);
        var collection = Normalize(row.CollectionSite);
        var destination = Normalize(row.DeliverySite);
        if (pallets <= 0 || string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(destination)) return null;

        var candidates = orders.Where(order =>
        {
            if (usedOrderIds.Contains(order.Id)) return false;
            if (order.CollectionDate != planningDate) return false;
            orderDetails.TryGetValue(Normalize(order.Reference), out var detail);
            return EffectiveOrderedPallets(order, detail) == pallets
                && Normalize(Collection(detail, order)) == collection
                && Normalize(Destination(detail, order)) == destination;
        }).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string BuildGroupedStopDetail(IEnumerable<PlannerPlanStopRequest> rows, bool includeDelivery)
    {
        var details = rows.OrderBy(row => row.Sequence).Select(row =>
        {
            var routePart = includeDelivery && !string.IsNullOrWhiteSpace(row.DeliverySite) ? $"to {row.DeliverySite}" :
                !includeDelivery && !string.IsNullOrWhiteSpace(row.CollectionSite) ? $"from {row.CollectionSite}" : null;
            var parts = new[]
            {
                routePart,
                includeDelivery ? Window(row.CollectFrom, row.CollectTo) : Deadline(row.Deadline),
                row.Pallets is null ? null : $"{row.Pallets:0.##} pallets",
                string.IsNullOrWhiteSpace(row.Reference) ? null : $"Ref {row.Reference}",
                Actual("collection arrived", row.CollectionSiteArrDate, row.CollectionSiteArrTime),
                Actual("despatched", row.DespatchedDate, row.DespatchedTime),
                Actual("delivery arrived", row.DeliveredDate, row.DeliveryArrivalTime),
                Actual("delivery departed", row.DeliveredDate, row.DeliveryDepartTime),
                ManualEta(row.ReasonForLate)
            }.Where(part => !string.IsNullOrWhiteSpace(part));
            return string.Join(" · ", parts);
        }).Where(detail => !string.IsNullOrWhiteSpace(detail));

        return string.Join(" | ", details);
    }

    private static string AddFirstCollectionWalkroundNote(string notes, PlannerPlanRunRequest run)
    {
        var instruction = FirstCollectionWalkroundInstruction(FirstCollectionWindow(run.Stops ?? []));
        if (string.IsNullOrWhiteSpace(instruction)) return notes;
        return string.IsNullOrWhiteSpace(notes) ? instruction : $"{instruction} | {notes}";
    }

    private static string? FirstCollectionWindow(IEnumerable<PlannerPlanStopRequest> rows)
    {
        var first = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.CollectFrom))
            .OrderBy(row => row.Sequence)
            .ThenBy(row => Clean(row.CollectFrom), StringComparer.Ordinal)
            .FirstOrDefault();
        if (first is null) return null;
        var from = Clean(first.CollectFrom);
        var to = Clean(first.CollectTo);
        return string.IsNullOrWhiteSpace(to) ? from : $"{from}-{to}";
    }

    private static string? FirstCollectionWalkroundInstruction(string? window) => string.IsNullOrWhiteSpace(window)
        ? null
        : $"First collection window is {window}. Plan the driver start from the actual Tacho other-work required before first drive.";

    private static Driver? ResolveDriver(IEnumerable<Driver> drivers, string? value) => string.IsNullOrWhiteSpace(value) || IsPlaceholder(value)
        ? null
        : drivers.FirstOrDefault(x => string.Equals(x.DisplayName.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(x.TachoName?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static Vehicle? ResolveVehicle(IEnumerable<Vehicle> vehicles, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var needle = Normalize(value);
        var exact = vehicles.Where(x => Normalize(x.Registration) == needle || Normalize(x.Abbreviation) == needle || Normalize(x.FleetNumber) == needle).ToList();
        if (exact.Count == 1) return exact[0];
        var suffix = vehicles.Where(x => Normalize(x.Registration).EndsWith(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        return suffix.Count == 1 ? suffix[0] : null;
    }

    private static Trailer? ResolveTrailer(IEnumerable<Trailer> trailers, string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : trailers.FirstOrDefault(x => string.Equals(x.TrailerNumber.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsPlaceholder(string? value) => string.Equals(value?.Trim(), "c/o", StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), "tbc", StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
    private static string? BuildStopDetail(PlannerPlanStopRequest stop) => string.Join(" | ", new[]
    {
        string.IsNullOrWhiteSpace(stop.Reference) ? null : $"Ref {stop.Reference}",
        stop.Pallets is null ? null : $"{stop.Pallets:0.##} pallets",
        string.IsNullOrWhiteSpace(stop.PalletType) ? null : stop.PalletType,
        Window(stop.CollectFrom, stop.CollectTo),
        Deadline(stop.Deadline),
        Actual("collection arrived", stop.CollectionSiteArrDate, stop.CollectionSiteArrTime),
        Actual("despatched", stop.DespatchedDate, stop.DespatchedTime),
        Actual("delivery arrived", stop.DeliveredDate, stop.DeliveryArrivalTime),
        Actual("delivery departed", stop.DeliveredDate, stop.DeliveryDepartTime),
        ManualEta(stop.ReasonForLate)
    }.Where(x => x is not null));

    private static string? Window(string? from, string? to) => string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to)
        ? null
        : $"collect window {Clean(from)}{(string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) ? string.Empty : "-")}{Clean(to)}";

    private static string? Deadline(string? value) => string.IsNullOrWhiteSpace(value) ? null : $"deadline {value.Trim()}";

    private static string? Actual(string label, string? date, string? time) => string.IsNullOrWhiteSpace(date) && string.IsNullOrWhiteSpace(time)
        ? null
        : $"{label} {Clean(date)} {Clean(time)}".Trim();

    private static string? ManualEta(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.StartsWith("eta", StringComparison.OrdinalIgnoreCase) ? $"manual ETA {text[3..].Trim()}" : $"note {text}";
    }

    private async Task<Dictionary<string, OrderDetail>> ReadOrderDetails(DateOnly date, CancellationToken ct)
    {
        var result = new Dictionary<string, OrderDetail>(StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => (x.EntityType == "order" || x.EntityType == "register:order") && x.Status != StagingStatus.Rejected)
            .OrderByDescending(x => x.ReviewedAtUtc ?? x.ReceivedAtUtc).ThenByDescending(x => x.ReceivedAtUtc).Take(8000).ToListAsync(ct);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                var reference = Text(root, "poNumber", "reference", "orderReference", "orderRef");
                if (string.IsNullOrWhiteSpace(reference) || result.ContainsKey(Normalize(reference))) continue;
                if (DateOnly.TryParse(Text(root, "collectionDate"), out var collectionDate) && collectionDate != date) continue;
                var collection = Text(root, "collectionLocation", "collectionSite", "collection", "sellerName", "pickupLocation", "pickupSite");
                var destination = Text(root, "deliveryLocation", "deliverySite", "delivery", "destination", "depot", "stallNumber");
                var group = Text(root, "planningGroup", "palletOrderGroup", "collectionGroup");
                var temperature = Text(root, "temperature", "temperatureC", "temp", "temperatureRequirement") ?? Tagged(Text(root, "driverInstructions", "notes"), "Temperature");
                var pallets = Int(root, "pallets", "palletQty", "palletQuantity", "quantity");
                var amended = row.ReviewNote?.Contains("Amended from Manage Jobs", StringComparison.OrdinalIgnoreCase) == true;
                result[Normalize(reference)] = new OrderDetail(reference, collection, destination, group, temperature, pallets, row.Source, row.ReviewedAtUtc ?? row.ReceivedAtUtc, amended);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static int EffectiveOrderedPallets(TransportOrder order, OrderDetail? detail)
    {
        var value = detail?.Amended == true ? detail.Pallets ?? order.Pallets : order.Pallets ?? detail?.Pallets;
        return Math.Max(value ?? 0, 0);
    }

    private static string Collection(OrderDetail? detail, TransportOrder order) =>
        !string.IsNullOrWhiteSpace(detail?.Collection) ? detail.Collection! : !string.IsNullOrWhiteSpace(order.SellerName) ? order.SellerName! : "Collection not mapped";

    private static string Destination(OrderDetail? detail, TransportOrder order)
    {
        if (!string.IsNullOrWhiteSpace(detail?.Destination)) return detail.Destination!;
        if (!string.IsNullOrWhiteSpace(order.StallNumber)) return order.StallNumber!;
        var tagged = Tagged(order.DriverInstructions, "Depot") ?? Tagged(order.DriverInstructions, "Delivery site") ?? Tagged(order.DriverInstructions, "Destination");
        return string.IsNullOrWhiteSpace(tagged) ? "Destination not mapped" : tagged;
    }

    private static string? Tagged(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var prefix = $"{label}:";
        return notes.Split('·', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();
    }

    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (Normalize(property.Name) != Normalize(name)) continue;
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? property.Value.ToString() : null;
            }
        }
        return null;
    }

    private static int? Int(JsonElement root, params string[] names) => int.TryParse(Text(root, names), out var value) ? value : null;

    private static DateTimeOffset? EarliestPlannerTime(DateOnly date, IEnumerable<string?> values)
    {
        return values
            .Select(value => ParsePlannerTime(date, value))
            .Where(value => value is not null)
            .OrderBy(value => value)
            .FirstOrDefault();
    }

    private static DateTimeOffset? ParsePlannerTime(DateOnly date, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !TimeOnly.TryParse(value, out var time)) return null;
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record OrderDetail(string Reference, string? Collection, string? Destination, string? Group, string? Temperature, int? Pallets, string? Source, DateTimeOffset UpdatedAtUtc, bool Amended);
}
