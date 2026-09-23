using System.Globalization;
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
public sealed class PlannerSourceLineImportController(TmsDbContext db) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> SiteNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "NWF", "NATURES", "WAY", "FOODS", "BAR", "BARFOOTS", "LAN", "LANGMEADS", "SB", "GHS", "SLH",
        "WAITROSE", "MORRISONS", "ALDI", "COLLECT", "COLLECTION", "DELIVER", "DELIVERY", "SITE", "RDC",
        "CHILL", "FRV", "PLUS3C", "PLUS10C"
    };

    [HttpPost("import-source-plan"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ImportSourcePlan(PlannerPlanImportRequest request, CancellationToken ct)
    {
        if (request.Runs is null || request.Runs.Count == 0)
            return BadRequest(new ErrorResponse("planner_plan_empty", "At least one planner run is required.", HttpContext.TraceIdentifier));
        if (request.Runs.Any(run => run.PlanningDate != request.PlanningDate))
            return BadRequest(new ErrorResponse("planning_date_mismatch", "Every run must use the root planningDate.", HttpContext.TraceIdentifier));
        if (request.Runs.Any(run => string.IsNullOrWhiteSpace(run.RunRef)))
            return BadRequest(new ErrorResponse("run_ref_required", "Every run must have a RunRef.", HttpContext.TraceIdentifier));

        var drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var trailers = await db.Trailers.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var orders = await db.TransportOrders.AsNoTracking().ToListAsync(ct);
        var siteResolver = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);

        var warnings = new List<string>();
        var unresolvedDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedTrailers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PlannerPlanRunResult>();
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var held = 0;

        foreach (var run in request.Runs.OrderBy(run => NaturalRunNumber(run.RunRef)).ThenBy(run => run.RunRef, StringComparer.OrdinalIgnoreCase))
        {
            var tmsReference = PlannerPlanImportRules.TmsReference(request.PlanningDate, run.RunRef);

            if (IsStandbyRun(run))
            {
                var existingStandby = await db.Loads.SingleOrDefaultAsync(load => load.Reference == tmsReference, ct);
                if (existingStandby is not null && existingStandby.Status != LoadStatus.Completed)
                {
                    await db.LoadStops.Where(stop => stop.LoadId == existingStandby.Id).ExecuteDeleteAsync(ct);
                    existingStandby.Status = LoadStatus.Cancelled;
                    existingStandby.DriverId = null;
                    existingStandby.VehicleId = null;
                    existingStandby.TrailerId = null;
                    await db.SaveChangesAsync(ct);
                }

                held++;
                results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, "ExcludedStandby", "Green", 0m, "Standby rows are not operational runs and were excluded from Planner/Live Runs."));
                continue;
            }

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
                results.Add(new PlannerPlanRunResult(run.RunRef, tmsReference, "Held", capacity.Status, capacity.UtilisationPercent, "No source lines supplied"));
                continue;
            }

            var driver = ResolveDriver(drivers, run.Driver);
            var vehicle = ResolveVehicle(vehicles, run.Vehicle);
            var trailer = ResolveTrailer(trailers, run.Trailer);
            if (!string.IsNullOrWhiteSpace(run.Driver) && driver is null && !IsPlaceholder(run.Driver)) unresolvedDrivers.Add(run.Driver.Trim());
            if (!string.IsNullOrWhiteSpace(run.Vehicle) && vehicle is null) unresolvedVehicles.Add(run.Vehicle.Trim());
            if (!string.IsNullOrWhiteSpace(run.Trailer) && trailer is null) unresolvedTrailers.Add(run.Trailer.Trim());

            var load = await db.Loads.Include(x => x.Stops).SingleOrDefaultAsync(x => x.Reference == tmsReference, ct);
            var auditKey = $"planimport-source:{request.PlanningDate:yyyyMMdd}:{run.RunRef.ToUpperInvariant()}";
            var runJson = JsonSerializer.Serialize(run, JsonOptions);
            var audit = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == auditKey, ct);
            var samePayload = audit is not null && string.Equals(audit.PayloadJson, runJson, StringComparison.Ordinal);

            if (load is null)
            {
                load = new Load { Id = Guid.NewGuid(), Reference = tmsReference, PlanningDate = request.PlanningDate };
                db.Loads.Add(load);
                created++;
            }
            else if (samePayload)
            {
                unchanged++;
            }
            else
            {
                updated++;
            }

            load.DriverId = driver?.Id;
            load.VehicleId = vehicle?.Id;
            load.TrailerId = trailer?.Id;
            var trailerResolved = string.IsNullOrWhiteSpace(run.Trailer) || trailer is not null;
            load.Status = driver is not null && vehicle is not null && trailerResolved ? LoadStatus.Planned : LoadStatus.Draft;
            load.PalletSpacesUsed = capacity.StandardEquivalentUsed;
            load.TotalPalletSpaces = capacity.StandardEquivalentCapacity;
            load.CapacityType = "Mixed Standard/Euro";
            load.PlannerNotes = PlannerPlanImportRules.BuildPlannerNotes(run, capacity);

            if (load.Stops.Count > 0) db.LoadStops.RemoveRange(load.Stops);
            var replacementStops = BuildStops(load.Id, run, orders, siteResolver, warnings);
            db.LoadStops.AddRange(replacementStops);
            load.Stops = replacementStops;
            await db.SaveChangesAsync(ct);

            await LoadCommercialStore.SaveAsync(db, load,
                new LoadCommercialValues(null, null, null, null, null, null, null, null,
                    capacity.StandardEquivalentUsed, capacity.StandardEquivalentCapacity, "Mixed Standard/Euro", null, null, load.PlannerNotes),
                User.Identity?.Name, ct);

            var allocations = BuildAllocations(run, orders);
            var allocationChanges = await PlanningAllocationStore.SyncPlannerImportAllocationsAsync(db, load.Id, request.PlanningDate, allocations, User.Identity?.Name, ct);

            if (audit is null)
            {
                audit = new StagedImport
                {
                    EntityType = "plannerplansourcerun",
                    IdempotencyKey = auditKey,
                    PayloadJson = runJson,
                    Source = "Planner source-line import"
                };
                db.StagedImports.Add(audit);
            }
            audit.PayloadJson = runJson;
            audit.Status = StagingStatus.Promoted;
            audit.ReviewedAtUtc = DateTimeOffset.UtcNow;
            audit.ReviewedBy = User.Identity?.Name;
            audit.ReviewNote = $"Imported as {tmsReference} with {run.Stops.Count} preserved source lines.";
            await db.SaveChangesAsync(ct);

            if (capacity.Status is "Red" or "Amber") warnings.Add($"{run.RunRef}: capacity is {capacity.Status} at {capacity.UtilisationPercent:0.0}%.");
            if (allocationChanges < run.Stops.Count(stop => stop.Pallets > 0))
                warnings.Add($"{run.RunRef}: {allocationChanges} of {run.Stops.Count(stop => stop.Pallets > 0)} source pallet lines matched existing orders for Pallet Control.");

            results.Add(new PlannerPlanRunResult(
                run.RunRef,
                tmsReference,
                load.Status == LoadStatus.Planned ? "ImportedPlanned" : "ImportedDraft",
                capacity.Status,
                capacity.UtilisationPercent,
                $"{run.Stops.Count} source line(s) preserved; {allocationChanges} pallet allocation(s) written."));
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

    private static List<LoadStop> BuildStops(
        Guid loadId,
        PlannerPlanRunRequest run,
        IReadOnlyCollection<TransportOrder> orders,
        PlannerSourceMasterDataResolver siteResolver,
        ICollection<string> warnings)
    {
        var sourceRows = run.Stops.OrderBy(stop => stop.Sequence).ToList();
        var result = new List<LoadStop>();

        foreach (var row in sourceRows.Where(row => !string.IsNullOrWhiteSpace(row.CollectionSite)))
        {
            var site = siteResolver.Resolve(row.CollectionSite);
            AddSiteWarnings(warnings, run.RunRef, row.CollectionSite, site);
            result.Add(new LoadStop
            {
                Id = Guid.NewGuid(),
                LoadId = loadId,
                Sequence = result.Count + 1,
                Name = Clip($"Collect · {row.CollectionSite}", 200)!,
                Address = Clip(WithMasterAddress(CollectionDetail(row), site.Address), 500),
                PlannerNote = Clip($"{SourceLine(row)} · {site.EvidenceNote}", 1000),
                Latitude = site.Latitude,
                Longitude = site.Longitude,
                PlannedArrivalUtc = ParsePlannerTime(run.PlanningDate, row.CollectFrom ?? row.CollectTo)
            });
        }

        foreach (var group in GroupDeliveries(sourceRows))
        {
            var site = siteResolver.Resolve(group.Site);
            AddSiteWarnings(warnings, run.RunRef, group.Site, site);
            var firstOrder = group.Rows.Select(row => MatchOrder(row, orders, new HashSet<Guid>(), run.PlanningDate)).FirstOrDefault(order => order is not null);
            var detail = DeliveryDetail(group.Rows);
            result.Add(new LoadStop
            {
                Id = Guid.NewGuid(),
                LoadId = loadId,
                OrderId = firstOrder?.Id,
                Sequence = result.Count + 1,
                Name = Clip($"Deliver · {group.Site}", 200)!,
                Address = Clip(WithMasterAddress(detail, site.Address), 500),
                PlannerNote = Clip($"{detail} · {site.EvidenceNote}", 1000),
                Latitude = site.Latitude,
                Longitude = site.Longitude,
                PlannedArrivalUtc = ParsePlannerTime(run.PlanningDate, group.Rows.Select(row => row.Deadline).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)))
            });
        }

        return result;
    }

    private static void AddSiteWarnings(ICollection<string> warnings, string runRef, string? label, PlannerSourceSiteResolution site)
    {
        string? warning = !site.SiteMatched
            ? $"{runRef}: site '{label}' did not resolve uniquely to Site Master."
            : !site.GeofenceLinked
                ? $"{runRef}: Site {site.SiteNumber} ({site.SiteName}) has no active linked geofence."
                : null;
        if (warning is not null && !warnings.Contains(warning)) warnings.Add(warning);
    }

    private static string WithMasterAddress(string detail, string? masterAddress) => string.IsNullOrWhiteSpace(masterAddress)
        ? detail
        : $"{detail} · {masterAddress.Trim()}";

    private static List<(Guid OrderId, int Pallets)> BuildAllocations(PlannerPlanRunRequest run, IReadOnlyCollection<TransportOrder> orders)
    {
        var used = new HashSet<Guid>();
        var result = new List<(Guid OrderId, int Pallets)>();
        foreach (var row in run.Stops.OrderBy(stop => stop.Sequence))
        {
            if (row.Pallets is null || row.Pallets <= 0 || decimal.Truncate(row.Pallets.Value) != row.Pallets.Value) continue;
            var order = MatchOrder(row, orders, used, run.PlanningDate);
            if (order is null) continue;
            result.Add((order.Id, decimal.ToInt32(row.Pallets.Value)));
            used.Add(order.Id);
        }
        return result;
    }

    private static TransportOrder? MatchOrder(PlannerPlanStopRequest row, IReadOnlyCollection<TransportOrder> orders, ISet<Guid> used, DateOnly date)
    {
        if (!string.IsNullOrWhiteSpace(row.Reference))
        {
            var reference = Normalize(row.Reference);
            var exactReference = orders.Where(order => !used.Contains(order.Id) && Normalize(order.Reference) == reference).Take(2).ToList();
            if (exactReference.Count == 1) return exactReference[0];
        }

        if (row.Pallets is null || decimal.Truncate(row.Pallets.Value) != row.Pallets.Value) return null;
        var pallets = decimal.ToInt32(row.Pallets.Value);
        var candidates = orders.Where(order =>
            !used.Contains(order.Id) &&
            (order.CollectionDate == date || order.DeliveryDate == date) &&
            (order.Pallets ?? 0) == pallets &&
            SameOperationalSite(order.SellerName, row.CollectionSite) &&
            SameOperationalSite(order.StallNumber, row.DeliverySite)).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool SameOperationalSite(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (Normalize(left) == Normalize(right)) return true;
        var leftTokens = SiteTokens(left);
        var rightTokens = SiteTokens(right);
        return leftTokens.Count > 0 && rightTokens.Count > 0 && leftTokens.SetEquals(rightTokens);
    }

    private static HashSet<string> SiteTokens(string? value)
    {
        var spaced = new string((value ?? string.Empty)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ')
            .ToArray());
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1 && !SiteNoise.Contains(token) && !token.EndsWith("C", StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string Site, string Deadline, List<PlannerPlanStopRequest> Rows)> GroupDeliveries(IEnumerable<PlannerPlanStopRequest> rows)
    {
        var groups = new List<(string Site, string Deadline, List<PlannerPlanStopRequest> Rows)>();
        foreach (var row in rows.OrderBy(row => row.Sequence))
        {
            var site = row.DeliverySite?.Trim();
            if (string.IsNullOrWhiteSpace(site)) continue;
            var deadline = row.Deadline?.Trim() ?? string.Empty;
            var index = groups.FindIndex(group => Normalize(group.Site) == Normalize(site) && string.Equals(group.Deadline, deadline, StringComparison.Ordinal));
            if (index >= 0) groups[index].Rows.Add(row);
            else groups.Add((site, deadline, [row]));
        }
        return groups;
    }

    private static string CollectionDetail(PlannerPlanStopRequest row) => string.Join(" · ", new[]
    {
        row.Pallets is null ? null : $"{row.Pallets:0.##} pallets",
        string.IsNullOrWhiteSpace(row.DeliverySite) ? null : $"for {row.DeliverySite}",
        string.IsNullOrWhiteSpace(row.Reference) ? null : $"Ref {row.Reference}",
        Window(row.CollectFrom, row.CollectTo)
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string DeliveryDetail(IEnumerable<PlannerPlanStopRequest> rows)
    {
        var ordered = rows.OrderBy(row => row.Sequence).ToList();
        var total = ordered.Sum(row => row.Pallets ?? 0m);
        var split = ordered.Select(row => $"{row.Pallets:0.##} from {row.CollectionSite}{(string.IsNullOrWhiteSpace(row.Reference) ? string.Empty : $" (Ref {row.Reference})")}");
        return $"{total:0.##} pallets total · {string.Join(" · ", split)}";
    }

    private static string SourceLine(PlannerPlanStopRequest row) =>
        $"Source {(row.SourceRow is null ? $"sequence {row.Sequence}" : $"row {row.SourceRow}")} · {row.Pallets:0.##} pallets · {row.CollectionSite} → {row.DeliverySite}{(string.IsNullOrWhiteSpace(row.Reference) ? string.Empty : $" · Ref {row.Reference}")}";

    private static DateTimeOffset? ParsePlannerTime(DateOnly date, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !TimeOnly.TryParse(value, out var time)) return null;
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        }
        catch (TimeZoneNotFoundException)
        {
            return new DateTimeOffset(local, TimeSpan.Zero);
        }
    }

    private static string? Window(string? from, string? to) => string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to)
        ? null
        : $"collect {from}{(!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to) ? "-" : string.Empty)}{to}";

    private static Driver? ResolveDriver(IEnumerable<Driver> rows, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsPlaceholder(value)) return null;
        var needle = Normalize(value);
        var matches = rows.Where(row =>
            Normalize(row.DisplayName) == needle ||
            Normalize(row.TachoName) == needle ||
            Normalize(row.EmployeeNumber) == needle).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static Vehicle? ResolveVehicle(IEnumerable<Vehicle> rows, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var needle = Normalize(value);
        var exact = rows.Where(row => Normalize(row.Registration) == needle || Normalize(row.Abbreviation) == needle || Normalize(row.FleetNumber) == needle).ToList();
        if (exact.Count == 1) return exact[0];
        var suffix = rows.Where(row => Normalize(row.Registration).EndsWith(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        return suffix.Count == 1 ? suffix[0] : null;
    }

    private static Trailer? ResolveTrailer(IEnumerable<Trailer> rows, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var needle = TrailerKey(value);
        var matches = rows.Where(row => TrailerKey(row.TrailerNumber) == needle).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string TrailerKey(string? value)
    {
        var key = Normalize(value);
        if (key.StartsWith("SLH", StringComparison.Ordinal)) key = key[3..];
        return int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : key;
    }

    private static int NaturalRunNumber(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    private static bool IsStandbyRun(PlannerPlanRunRequest run)
    {
        if ($"{run.PlannerRun} {run.RunType}".Contains("standby", StringComparison.OrdinalIgnoreCase)) return true;
        var labels = (run.Stops ?? [])
            .SelectMany(stop => new[] { stop.CollectionSite, stop.DeliverySite })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();
        return labels.Count > 0 && labels.All(value => value.StartsWith("Standby", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlaceholder(string? value) => string.Equals(value?.Trim(), "c/o", StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), "tbc", StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
