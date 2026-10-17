using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record MasterDataDuplicateConsolidationResult(
    SiteMasterConsolidationResult Sites,
    int MarketDuplicatesArchived,
    int VehicleDuplicatesArchived,
    int VehicleFuelDetailsRecovered,
    int DriverDuplicatesArchived,
    int TrailerDuplicatesArchived);

public static class MasterDataDuplicateConsolidation
{
    public static async Task<MasterDataDuplicateConsolidationResult> RunAsync(
        TmsDbContext db,
        string actor,
        ILogger logger,
        CancellationToken ct)
    {
        var sites = await SiteMasterConsolidation.ReconcileAsync(db, actor, ct);
        var drivers = await ConsolidateDriversAsync(db, actor, logger, ct);
        var markets = await ConsolidateMarketsAsync(db, actor, ct);
        var (vehicles, fuelRecovered) = await ConsolidateVehiclesAsync(db, actor, logger, ct);
        var trailers = await ConsolidateTrailersAsync(db, actor, logger, ct);
        return new MasterDataDuplicateConsolidationResult(sites, markets, vehicles, fuelRecovered, drivers, trailers);
    }

    private static async Task<int> ConsolidateDriversAsync(
        TmsDbContext db,
        string actor,
        ILogger logger,
        CancellationToken ct)
    {
        var drivers = await db.Drivers.ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        var loadUse = await db.Loads.AsNoTracking()
            .Where(load => load.DriverId != null)
            .GroupBy(load => load.DriverId!.Value)
            .Select(group => new { DriverId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.DriverId, item => item.Count, ct);

        var archived = 0;

        // Driver Master/Sage HR is authoritative for the operational driver population.
        // TachoMaster enriches identity/card/duty evidence but must never deactivate a valid
        // Driver Master record simply because it has not linked yet. Keep unmatched active
        // drivers live and surface them for review instead.
        var reviewQueued = 0;
        var pendingTachoReview = drivers.Where(driver =>
            driver.Active &&
            string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)).ToList();

        foreach (var driver in pendingTachoReview)
        {
            var reviewIdentity = !string.IsNullOrWhiteSpace(driver.EmployeeNumber)
                ? driver.EmployeeNumber
                : driver.Id.ToString("N");
            var reviewKey = $"driverreview:tacho-unmatched:{reviewIdentity}";
            var exists = await db.StagedImports.AnyAsync(row =>
                row.IdempotencyKey == reviewKey &&
                row.Status == StagingStatus.PendingReview, ct);

            if (exists) continue;

            db.StagedImports.Add(new StagedImport
            {
                EntityType = "driverreview",
                IdempotencyKey = reviewKey,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    driverId = driver.Id,
                    employeeNumber = driver.EmployeeNumber,
                    displayName = driver.DisplayName,
                    tachoCardNumber = driver.TachoCardNumber,
                    lastTachoSyncUtc = driver.LastTachoSyncUtc,
                    source = "Active Driver Master record awaiting TachoMaster identity match"
                }),
                Source = "MasterDataDuplicateConsolidation Tacho identity review",
                Status = StagingStatus.PendingReview,
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                ReviewNote = $"Driver {driver.DisplayName} ({driver.EmployeeNumber}) is active in Driver Master but has no TachoMaster Member/DB number. Keep active; review or enter the Tacho identity manually if automatic matching does not resolve it."
            });
            reviewQueued++;
        }

        // Consolidate only within the same Member Code. Two drivers with different
        // non-blank Member Codes are different people and must never be merged here —
        // TachoMemberCodeDriverMasterSync owns cross-member merging under its stricter rules.
        foreach (var group in DuplicateGroupsByKeys(
            drivers.Where(driver => driver.Active && !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)).ToList(),
            driver => driver.Id,
            driver => new[]
            {
                IdentityKey("member", driver.TachoMasterDriverId),
                IdentityKey("employee", driver.EmployeeNumber),
                IdentityKey("licence", driver.DrivingLicenceNumber)
                // NameMobileKey intentionally removed: name + mobile alone cannot prove
                // two records are the same person without a shared Tacho identity.
            })
            // Hard gate: only merge rows that share the same normalised Member Code.
            .Where(group => group
                .Select(driver => TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 1))
        {
            var canonical = group
                .OrderByDescending(driver => !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId))
                .ThenByDescending(driver => loadUse.GetValueOrDefault(driver.Id))
                .ThenByDescending(DriverCompleteness)
                .ThenBy(driver => driver.DisplayName)
                .First();

            foreach (var duplicate in group.Where(driver => driver.Id != canonical.Id))
            {
                PreserveDriverDetail(canonical, duplicate);

                foreach (var load in await db.Loads.Where(load => load.DriverId == duplicate.Id).ToListAsync(ct))
                    load.DriverId = canonical.Id;

                try
                {
                    foreach (var run in await db.PlanProposalRuns.Where(run => run.DriverId == duplicate.Id).ToListAsync(ct))
                        run.DriverId = canonical.Id;
                    await ReassignDriverProposalCandidatesAsync(db, duplicate.Id, canonical.Id, ct);
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Optional planning driver references could not be reassigned while consolidating {DriverId}.", duplicate.Id);
                }

                try
                {
                    foreach (var allocation in await db.RunResourceAllocations.Where(allocation => allocation.DriverId == duplicate.Id).ToListAsync(ct))
                        allocation.DriverId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Run allocation driver references could not be reassigned while consolidating {DriverId}.", duplicate.Id);
                }

                try
                {
                    foreach (var statusLog in await db.DriverStatusLogs.Where(log => log.DriverId == duplicate.Id).ToListAsync(ct))
                        statusLog.DriverId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Driver status references could not be reassigned while consolidating {DriverId}.", duplicate.Id);
                }

                try
                {
                    foreach (var mapping in await db.IntegrationMappings.Where(mapping => mapping.TmsEntityType == "Driver" && mapping.TmsEntityId == duplicate.Id).ToListAsync(ct))
                        mapping.TmsEntityId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Driver integration mappings could not be reassigned while consolidating {DriverId}.", duplicate.Id);
                }

                duplicate.Active = false;
                archived++;
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Driver",
                    EntityId = canonical.Id,
                    Action = "MergedDuplicateDriverIdentity",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        canonicalDriverId = canonical.Id,
                        canonicalEmployeeNumber = canonical.EmployeeNumber,
                        canonicalTachoMasterDriverId = canonical.TachoMasterDriverId,
                        duplicateDriverId = duplicate.Id,
                        duplicateEmployeeNumber = duplicate.EmployeeNumber,
                        duplicateTachoMasterDriverId = duplicate.TachoMasterDriverId
                    })
                });
            }
        }

        if (archived > 0 || reviewQueued > 0) await db.SaveChangesAsync(ct);
        return archived;
    }

    private static async Task<int> ConsolidateMarketsAsync(TmsDbContext db, string actor, CancellationToken ct)
    {
        var rows = await db.MarketContacts.Where(row => row.Active).ToListAsync(ct);
        var archived = 0;

        foreach (var group in rows
            .GroupBy(row => MarketIdentity(row.Market, row.Name, row.StandOrLocation), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() > 1))
        {
            var canonical = group
                .OrderByDescending(row => !string.IsNullOrWhiteSpace(row.MarketKey))
                .ThenByDescending(MarketCompleteness)
                .ThenBy(row => row.Id)
                .First();

            foreach (var duplicate in group.Where(row => row.Id != canonical.Id))
            {
                canonical.Salesman ??= duplicate.Salesman;
                canonical.Sender ??= duplicate.Sender;
                canonical.ReadOnlyMapPdfUrl ??= duplicate.ReadOnlyMapPdfUrl;
                canonical.MarketKey ??= duplicate.MarketKey;
                duplicate.Active = false;
                archived++;

                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "MarketContact",
                    EntityId = canonical.Id,
                    Action = "MergedDuplicateMarketIdentity",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        canonicalMarketContactId = canonical.Id,
                        canonicalMarketKey = canonical.MarketKey,
                        market = canonical.Market,
                        name = canonical.Name,
                        standOrLocation = canonical.StandOrLocation,
                        duplicateMarketContactId = duplicate.Id,
                        duplicateMarketKey = duplicate.MarketKey
                    })
                });
            }
        }

        if (archived > 0) await db.SaveChangesAsync(ct);
        return archived;
    }

    private static async Task<(int Archived, int FuelRecovered)> ConsolidateVehiclesAsync(
        TmsDbContext db,
        string actor,
        ILogger logger,
        CancellationToken ct)
    {
        var vehicles = await db.Vehicles.ToListAsync(ct);
        var loadUse = await db.Loads.AsNoTracking()
            .Where(load => load.VehicleId != null)
            .GroupBy(load => load.VehicleId!.Value)
            .Select(group => new { VehicleId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.VehicleId, item => item.Count, ct);

        var archived = 0;
        var fuelRecovered = 0;
        foreach (var group in DuplicateGroupsByKeys(
            vehicles.Where(vehicle => vehicle.Active).ToList(),
            vehicle => vehicle.Id,
            vehicle => new[]
            {
                IdentityKey("registration", vehicle.Registration),
                IdentityKey("fleetio", vehicle.FleetioId),
                IdentityKey("vin", vehicle.VIN)
            }))
        {
            var canonical = group
                .OrderByDescending(vehicle => vehicle.Active)
                .ThenByDescending(vehicle => !string.IsNullOrWhiteSpace(vehicle.FleetioId))
                .ThenByDescending(vehicle => loadUse.GetValueOrDefault(vehicle.Id))
                .ThenByDescending(VehicleCompleteness)
                .ThenBy(vehicle => vehicle.Id)
                .First();

            foreach (var duplicate in group.Where(vehicle => vehicle.Id != canonical.Id))
            {
                var hadFuelGap = HasFuelGap(canonical) && HasFuelData(duplicate);
                PreserveVehicleDetail(canonical, duplicate);
                if (hadFuelGap && HasFuelData(canonical)) fuelRecovered++;

                foreach (var load in await db.Loads.Where(load => load.VehicleId == duplicate.Id).ToListAsync(ct))
                    load.VehicleId = canonical.Id;

                try
                {
                    foreach (var run in await db.PlanProposalRuns.Where(run => run.VehicleId == duplicate.Id).ToListAsync(ct))
                        run.VehicleId = canonical.Id;
                    await ReassignVehicleProposalCandidatesAsync(db, duplicate.Id, canonical.Id, ct);
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Optional planning vehicle references could not be reassigned while consolidating {VehicleId}.", duplicate.Id);
                }

                try
                {
                    foreach (var allocation in await db.RunResourceAllocations.Where(allocation => allocation.VehicleId == duplicate.Id).ToListAsync(ct))
                        allocation.VehicleId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Run allocation vehicle references could not be reassigned while consolidating {VehicleId}.", duplicate.Id);
                }

                try
                {
                    foreach (var mapping in await db.IntegrationMappings.Where(mapping => mapping.TmsEntityType == "Vehicle" && mapping.TmsEntityId == duplicate.Id).ToListAsync(ct))
                        mapping.TmsEntityId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Vehicle integration mappings could not be reassigned while consolidating {VehicleId}.", duplicate.Id);
                }

                duplicate.Active = false;
                archived++;
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Vehicle",
                    EntityId = canonical.Id,
                    Action = "MergedDuplicateRegistration",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        canonicalVehicleId = canonical.Id,
                        canonicalRegistration = canonical.Registration,
                        duplicateVehicleId = duplicate.Id,
                        duplicateRegistration = duplicate.Registration,
                        fuelDetailsRecovered = hadFuelGap
                    })
                });
            }
        }

        if (archived > 0 || fuelRecovered > 0) await db.SaveChangesAsync(ct);
        return (archived, fuelRecovered);
    }

    private static async Task<int> ConsolidateTrailersAsync(
        TmsDbContext db,
        string actor,
        ILogger logger,
        CancellationToken ct)
    {
        var trailers = await db.Trailers.ToListAsync(ct);
        var loadUse = await db.Loads.AsNoTracking()
            .Where(load => load.TrailerId != null)
            .GroupBy(load => load.TrailerId!.Value)
            .Select(group => new { TrailerId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.TrailerId, item => item.Count, ct);

        var archived = 0;
        foreach (var group in DuplicateGroupsByKeys(
            trailers.Where(trailer => trailer.Active).ToList(),
            trailer => trailer.Id,
            trailer => new[]
            {
                IdentityKey("trailer", trailer.TrailerNumber),
                TrailerAliasKey(trailer.TrailerNumber)
            }))
        {
            var canonical = group
                .OrderByDescending(trailer => loadUse.GetValueOrDefault(trailer.Id))
                .ThenByDescending(TrailerCompleteness)
                .ThenBy(trailer => trailer.TrailerNumber)
                .First();

            foreach (var duplicate in group.Where(trailer => trailer.Id != canonical.Id))
            {
                canonical.Type ??= duplicate.Type;
                canonical.StandardCapacity ??= duplicate.StandardCapacity;
                canonical.EuroCapacity ??= duplicate.EuroCapacity;

                foreach (var load in await db.Loads.Where(load => load.TrailerId == duplicate.Id).ToListAsync(ct))
                    load.TrailerId = canonical.Id;

                try
                {
                    foreach (var run in await db.PlanProposalRuns.Where(run => run.TrailerId == duplicate.Id).ToListAsync(ct))
                        run.TrailerId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Optional planning trailer references could not be reassigned while consolidating {TrailerId}.", duplicate.Id);
                }

                try
                {
                    foreach (var allocation in await db.RunResourceAllocations.Where(allocation => allocation.TrailerId == duplicate.Id).ToListAsync(ct))
                        allocation.TrailerId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Run allocation trailer references could not be reassigned while consolidating {TrailerId}.", duplicate.Id);
                }

                try
                {
                    foreach (var mapping in await db.IntegrationMappings.Where(mapping => mapping.TmsEntityType == "Trailer" && mapping.TmsEntityId == duplicate.Id).ToListAsync(ct))
                        mapping.TmsEntityId = canonical.Id;
                }
                catch (Exception ex) when (SchemaUnavailable(ex))
                {
                    logger.LogWarning(ex, "Trailer integration mappings could not be reassigned while consolidating {TrailerId}.", duplicate.Id);
                }

                duplicate.Active = false;
                archived++;
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Trailer",
                    EntityId = canonical.Id,
                    Action = "MergedDuplicateTrailerIdentity",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        canonicalTrailerId = canonical.Id,
                        canonicalTrailerNumber = canonical.TrailerNumber,
                        duplicateTrailerId = duplicate.Id,
                        duplicateTrailerNumber = duplicate.TrailerNumber
                    })
                });
            }
        }

        if (archived > 0) await db.SaveChangesAsync(ct);
        return archived;
    }

    private static async Task ReassignDriverProposalCandidatesAsync(TmsDbContext db, Guid duplicateId, Guid canonicalId, CancellationToken ct)
    {
        var rows = await db.PlanProposalCandidates.Where(candidate => candidate.DriverId == duplicateId).ToListAsync(ct);
        foreach (var row in rows)
        {
            var conflict = await db.PlanProposalCandidates.AnyAsync(existing =>
                existing.Id != row.Id &&
                existing.ProposalRunId == row.ProposalRunId &&
                existing.DriverId == canonicalId &&
                existing.VehicleId == row.VehicleId, ct);
            if (conflict) db.PlanProposalCandidates.Remove(row);
            else row.DriverId = canonicalId;
        }
    }

    private static async Task ReassignVehicleProposalCandidatesAsync(TmsDbContext db, Guid duplicateId, Guid canonicalId, CancellationToken ct)
    {
        var rows = await db.PlanProposalCandidates.Where(candidate => candidate.VehicleId == duplicateId).ToListAsync(ct);
        foreach (var row in rows)
        {
            var conflict = await db.PlanProposalCandidates.AnyAsync(existing =>
                existing.Id != row.Id &&
                existing.ProposalRunId == row.ProposalRunId &&
                existing.DriverId == row.DriverId &&
                existing.VehicleId == canonicalId, ct);
            if (conflict) db.PlanProposalCandidates.Remove(row);
            else row.VehicleId = canonicalId;
        }
    }

    private static void PreserveDriverDetail(Driver target, Driver source)
    {
        target.TachoMasterDriverId ??= source.TachoMasterDriverId;
        target.TachoName ??= source.TachoName;
        target.MobileNumber ??= source.MobileNumber;
        target.DriverType ??= source.DriverType;
        target.DriverGroup ??= source.DriverGroup;
        target.Skills ??= source.Skills;
        target.DrivingLicenceNumber ??= source.DrivingLicenceNumber;
        target.LicenceExpiry ??= source.LicenceExpiry;
        target.CPCExpiry ??= source.CPCExpiry;
        target.DigitalTachoCardExpiry ??= source.DigitalTachoCardExpiry;
        target.MedicalExpiry ??= source.MedicalExpiry;
        target.LastTachoSyncUtc ??= source.LastTachoSyncUtc;
    }

    private static void PreserveVehicleDetail(Vehicle target, Vehicle source)
    {
        target.FleetNumber ??= source.FleetNumber;
        target.VIN ??= source.VIN;
        target.VehicleSite ??= source.VehicleSite;
        target.OwnerType ??= source.OwnerType;
        target.Abbreviation ??= source.Abbreviation;
        target.Transmission ??= source.Transmission;
        target.DvsCompliant ??= source.DvsCompliant;
        target.CabMobile ??= source.CabMobile;
        target.FuelProvider ??= source.FuelProvider;
        target.FuelPin ??= source.FuelPin;
        target.ShellCard ??= source.ShellCard;
        target.BpRedCard ??= source.BpRedCard;
        target.BpPlainCard ??= source.BpPlainCard;
        target.FuelPinSecretName ??= source.FuelPinSecretName;
        target.FuelCardLastFour ??= source.FuelCardLastFour;
        target.Notes ??= source.Notes;
        target.FleetioId ??= source.FleetioId;
        target.FleetioName ??= source.FleetioName;
        target.FleetioStatus ??= source.FleetioStatus;
    }

    private static IReadOnlyList<IReadOnlyList<T>> DuplicateGroupsByKeys<T>(IReadOnlyList<T> rows, Func<T, Guid> id, Func<T, IEnumerable<string?>> keys) where T : notnull
    {
        var groupsByKey = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var key in keys(row).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!groupsByKey.TryGetValue(key!, out var group)) groupsByKey[key!] = group = [];
                group.Add(row);
            }
        }

        var remaining = rows.ToDictionary(id);
        var result = new List<IReadOnlyList<T>>();
        foreach (var seed in rows)
        {
            if (!remaining.Remove(id(seed))) continue;
            var group = new List<T> { seed };
            var queue = new Queue<T>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var key in keys(current).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!groupsByKey.TryGetValue(key!, out var matches)) continue;
                    foreach (var match in matches.ToList())
                    {
                        if (!remaining.Remove(id(match))) continue;
                        group.Add(match);
                        queue.Enqueue(match);
                    }
                }
            }
            if (group.Count > 1) result.Add(group);
        }

        return result;
    }

    private static string? IdentityKey(string prefix, string? value)
    {
        var normalised = Normalise(value);
        return normalised.Length == 0 ? null : $"{prefix}:{normalised}";
    }

    private static string? NameMobileKey(string? name, string? mobile)
    {
        var normalisedName = Normalise(name);
        var normalisedMobile = Normalise(mobile);
        return normalisedName.Length < 8 || normalisedMobile.Length < 6 ? null : $"name-mobile:{normalisedName}|{normalisedMobile}";
    }

    private static string? TrailerAliasKey(string? trailerNumber)
    {
        var normalised = Normalise(trailerNumber);
        if (normalised.StartsWith("SLH", StringComparison.OrdinalIgnoreCase)) normalised = normalised[3..];
        normalised = normalised.TrimStart('0');
        return normalised.Length == 0 ? null : $"trailer-alias:{normalised}";
    }

    private static bool HasFuelData(Vehicle vehicle) =>
        !string.IsNullOrWhiteSpace(vehicle.FuelPin) ||
        !string.IsNullOrWhiteSpace(vehicle.ShellCard) ||
        !string.IsNullOrWhiteSpace(vehicle.BpRedCard) ||
        !string.IsNullOrWhiteSpace(vehicle.BpPlainCard) ||
        !string.IsNullOrWhiteSpace(vehicle.FuelProvider) ||
        !string.IsNullOrWhiteSpace(vehicle.FuelCardLastFour);

    private static bool HasFuelGap(Vehicle vehicle) =>
        string.IsNullOrWhiteSpace(vehicle.FuelPin) ||
        (string.IsNullOrWhiteSpace(vehicle.ShellCard) && string.IsNullOrWhiteSpace(vehicle.BpRedCard) && string.IsNullOrWhiteSpace(vehicle.BpPlainCard));

    private static int DriverCompleteness(Driver driver) => new string?[]
    {
        driver.TachoMasterDriverId, driver.TachoName, driver.MobileNumber, driver.DriverType,
        driver.DriverGroup, driver.Skills, driver.DrivingLicenceNumber
    }.Count(value => !string.IsNullOrWhiteSpace(value)) +
    new object?[] { driver.LicenceExpiry, driver.CPCExpiry, driver.DigitalTachoCardExpiry, driver.MedicalExpiry, driver.LastTachoSyncUtc }
        .Count(value => value is not null);

    private static int VehicleCompleteness(Vehicle vehicle) => new string?[]
    {
        vehicle.FleetNumber, vehicle.VIN, vehicle.VehicleSite, vehicle.CabMobile,
        vehicle.FuelProvider, vehicle.FuelPin, vehicle.ShellCard, vehicle.BpRedCard,
        vehicle.BpPlainCard, vehicle.FleetioId, vehicle.Notes
    }.Count(value => !string.IsNullOrWhiteSpace(value));

    private static int TrailerCompleteness(Trailer trailer) => new object?[]
    {
        trailer.Type, trailer.StandardCapacity, trailer.EuroCapacity
    }.Count(value => value is not null && !string.IsNullOrWhiteSpace(value.ToString()));

    private static int MarketCompleteness(MarketContact row) => new string?[]
    {
        row.MarketKey, row.Salesman, row.Sender, row.ReadOnlyMapPdfUrl
    }.Count(value => !string.IsNullOrWhiteSpace(value));

    private static string MarketIdentity(string? market, string? name, string? stand) =>
        $"{Normalise(market)}|{Normalise(name)}|{Normalise(stand)}";

    private static string Normalise(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
