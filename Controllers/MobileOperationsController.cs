using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Small, phone-friendly operational surface for authenticated SLH office users.
/// It deliberately returns only the data needed for quick lookups and allocation changes.
/// Sensitive fuel data is never included in the general snapshot.
/// </summary>
[ApiController]
[Route("api/v1/mobile")]
[Authorize(Policy = "TmsRead")]
public sealed class MobileOperationsController(TmsDbContext db) : ControllerBase
{
    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot([FromQuery] DateOnly date, CancellationToken ct)
    {
        var loads = await PlanningResilience.ReadLoadsAsync(db, date, ct);
        var drivers = await db.Drivers.AsNoTracking()
            .Where(x => x.Active)
            .OrderBy(x => x.DisplayName)
            .Select(x => new
            {
                x.Id,
                x.EmployeeNumber,
                x.DisplayName,
                x.DriverType,
                x.DriverGroup,
                x.MobileNumber
            })
            .ToListAsync(ct);

        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(x => x.Active)
            .OrderBy(x => x.Registration)
            .Select(x => new
            {
                x.Id,
                x.Registration,
                x.FleetNumber,
                x.Abbreviation,
                x.VehicleSite,
                x.FuelProvider,
                x.FuelCardLastFour,
                x.CabMobile
            })
            .ToListAsync(ct);

        var trailers = await db.Trailers.AsNoTracking()
            .Where(x => x.Active)
            .OrderBy(x => x.TrailerNumber)
            .Select(x => new { x.Id, x.TrailerNumber, x.Type })
            .ToListAsync(ct);

        var liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);

        static string Key(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        var liveByVehicle = liveStatuses
            .GroupBy(x => Key(x.VehicleIdentifier))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LastEventTimeUtc).First());

        var driverById = drivers.ToDictionary(x => x.Id);
        var vehicleById = vehicles.ToDictionary(x => x.Id);
        var trailerById = trailers.ToDictionary(x => x.Id);

        var runRows = loads
            .OrderBy(x => x.Stops.Where(s => s.PlannedArrivalUtc.HasValue).Select(s => s.PlannedArrivalUtc).Min() ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Reference)
            .Select(load =>
            {
                driverById.TryGetValue(load.DriverId ?? Guid.Empty, out var driver);
                vehicleById.TryGetValue(load.VehicleId ?? Guid.Empty, out var vehicle);
                trailerById.TryGetValue(load.TrailerId ?? Guid.Empty, out var trailer);

                VehicleLiveStatus? live = null;
                if (vehicle is not null)
                {
                    var candidates = new[] { Key(vehicle.Registration), Key(vehicle.FleetNumber), Key(vehicle.Abbreviation) }
                        .Where(x => x.Length > 0);
                    live = candidates.Select(k => liveByVehicle.GetValueOrDefault(k)).FirstOrDefault(x => x is not null);
                }

                var orderedStops = load.Stops.OrderBy(s => s.Sequence).ToList();
                var first = orderedStops.FirstOrDefault();
                var last = orderedStops.LastOrDefault();

                return new
                {
                    load.Id,
                    load.Reference,
                    load.PlanningDate,
                    status = load.Status.ToString(),
                    load.DriverId,
                    driverName = driver?.DisplayName,
                    driverEmployeeNumber = driver?.EmployeeNumber,
                    load.VehicleId,
                    vehicleRegistration = vehicle?.Registration,
                    vehicleFleetNumber = vehicle?.FleetNumber,
                    load.TrailerId,
                    trailerNumber = trailer?.TrailerNumber,
                    pallets = load.PalletSpacesUsed,
                    capacity = load.TotalPalletSpaces,
                    firstStop = first?.Name,
                    finalStop = last?.Name,
                    nextPlannedUtc = orderedStops.FirstOrDefault(s => s.PlannedArrivalUtc >= DateTimeOffset.UtcNow)?.PlannedArrivalUtc
                                     ?? first?.PlannedArrivalUtc,
                    stops = orderedStops.Select(s => new { s.Sequence, s.Name, s.Address, s.PlannedArrivalUtc }),
                    tracking = live is null ? null : new
                    {
                        live.Latitude,
                        live.Longitude,
                        live.SpeedKph,
                        live.IsMoving,
                        live.IgnitionOn,
                        live.LastKnownStatus,
                        live.LastEventTimeUtc
                    }
                };
            })
            .ToList();

        var assignedDrivers = runRows.Where(x => x.DriverId.HasValue).Select(x => x.DriverId!.Value).ToHashSet();
        var assignedVehicles = runRows.Where(x => x.VehicleId.HasValue).Select(x => x.VehicleId!.Value).ToHashSet();

        return Ok(new
        {
            planningDate = date,
            generatedAtUtc = DateTimeOffset.UtcNow,
            summary = new
            {
                runs = runRows.Count,
                active = runRows.Count(x => x.status is "Planned" or "Dispatched" or "InProgress"),
                unallocated = runRows.Count(x => !x.DriverId.HasValue || !x.VehicleId.HasValue),
                exceptions = runRows.Count(x => !x.DriverId.HasValue || !x.VehicleId.HasValue || x.tracking is null)
            },
            drivers = drivers.Select(d => new
            {
                d.Id,
                d.EmployeeNumber,
                d.DisplayName,
                d.DriverType,
                d.DriverGroup,
                d.MobileNumber,
                assigned = assignedDrivers.Contains(d.Id),
                currentRuns = runRows.Where(r => r.DriverId == d.Id).Select(r => new { r.Id, r.Reference, r.status, r.vehicleRegistration, r.firstStop, r.finalStop })
            }),
            vehicles = vehicles.Select(v =>
            {
                var keys = new[] { Key(v.Registration), Key(v.FleetNumber), Key(v.Abbreviation) }.Where(x => x.Length > 0);
                var live = keys.Select(k => liveByVehicle.GetValueOrDefault(k)).FirstOrDefault(x => x is not null);
                return new
                {
                    v.Id,
                    v.Registration,
                    v.FleetNumber,
                    v.Abbreviation,
                    v.VehicleSite,
                    v.FuelProvider,
                    v.FuelCardLastFour,
                    v.CabMobile,
                    assigned = assignedVehicles.Contains(v.Id),
                    currentRuns = runRows.Where(r => r.VehicleId == v.Id).Select(r => new { r.Id, r.Reference, r.status, r.driverName, r.firstStop, r.finalStop }),
                    tracking = live is null ? null : new { live.Latitude, live.Longitude, live.SpeedKph, live.IsMoving, live.LastKnownStatus, live.LastEventTimeUtc }
                };
            }),
            trailers,
            runs = runRows
        });
    }

    [HttpGet("fuel-pin/{vehicleId:guid}")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> FuelPin(Guid vehicleId, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(x => x.Id == vehicleId && x.Active, ct);
        if (vehicle is null) return NotFound();

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "vehicle-fuel-access",
            EntityId = vehicle.Id,
            Action = "FuelPinRevealed",
            ChangedBy = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "Microsoft user",
            ChangesJson = "{\"source\":\"SLH Mobile\"}"
        });
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            vehicle.Id,
            vehicle.Registration,
            vehicle.FuelProvider,
            fuelPin = vehicle.FuelPin,
            vehicle.FuelCardLastFour,
            vehicle.ShellCard,
            vehicle.BpRedCard,
            vehicle.BpPlainCard
        });
    }
}
