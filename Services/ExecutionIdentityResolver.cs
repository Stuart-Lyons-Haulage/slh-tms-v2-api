using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
 
namespace Slh.Tms.Api.Services;
 
/// <summary>
/// Canonical identity correlation used by TachoMaster, DOT/Falcon, geofence and ETA execution.
/// Keeps the same TMS vehicle/driver identity through the operational evidence chain.
/// </summary>
public static class ExecutionIdentityResolver
{
    public static async Task<Dictionary<Guid, HashSet<string>>> VehicleAliasesAsync(
        TmsDbContext db,
        IReadOnlyCollection<Vehicle> vehicles,
        CancellationToken ct)
    {
        var result = vehicles.ToDictionary(
            vehicle => vehicle.Id,
            vehicle => ExpandAliases(new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }));
 
        if (vehicles.Count == 0) return result;
        var ids = vehicles.Select(vehicle => vehicle.Id).ToList();
        try
        {
            var mappings = await db.IntegrationMappings.AsNoTracking()
                .Where(mapping => mapping.Active &&
                                  mapping.TmsEntityType == "Vehicle" &&
                                  ids.Contains(mapping.TmsEntityId) &&
                                  (mapping.Provider == "DotTracking" || mapping.Provider == "TachoMaster"))
                .Select(mapping => new { mapping.TmsEntityId, mapping.ExternalKey })
                .ToListAsync(ct);
            foreach (var mapping in mappings)
            {
                if (!result.TryGetValue(mapping.TmsEntityId, out var aliases)) continue;
                var rawKey = mapping.ExternalKey?.Trim();
                if (!string.IsNullOrWhiteSpace(rawKey)) aliases.Add(rawKey);
                foreach (var alias in VehicleAliasVariants(mapping.ExternalKey)) aliases.Add(alias);
            }
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }

        // VehicleTrackingEvents retains the provider's exact Falcon vehicle key. The
        // canonical aliases above deliberately normalise registrations for correlation,
        // but an indexed SQL `VehicleIdentifier IN (...)` history query must also carry
        // the exact provider string (including spaces/punctuation). VehicleLiveStatus is
        // a tiny one-row-per-provider-vehicle table, so use it to bootstrap those exact
        // keys without ever scanning the full fleet tracking history.
        try
        {
            var liveIdentifiers = await db.VehicleLiveStatuses.AsNoTracking()
                .Select(status => status.VehicleIdentifier)
                .ToListAsync(ct);

            foreach (var identifier in liveIdentifiers.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var matches = result
                    .Where(pair => MatchesVehicleIdentifier(pair.Value, identifier))
                    .Select(pair => pair.Key)
                    .Distinct()
                    .Take(2)
                    .ToList();
                if (matches.Count != 1) continue;

                var aliases = result[matches[0]];
                aliases.Add(identifier.Trim());
                foreach (var alias in VehicleAliasVariants(identifier)) aliases.Add(alias);
            }
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }

        return result;
    }

    public static async Task<int> RepairDotVehicleMappingsAsync(
        TmsDbContext db,
        IReadOnlyCollection<Vehicle> vehicles,
        IEnumerable<string?> providerIdentifiers,
        CancellationToken ct)
    {
        if (vehicles.Count == 0) return 0;

        // Keep the exact provider key in IntegrationMappings. Matching continues to use
        // the canonical normalised variants, while the raw key gives the indexed tracking
        // history query the exact VehicleIdentifier stored by RoadTech/Falcon.
        var identifiers = providerIdentifiers
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (identifiers.Count == 0) return 0;

        var directAliases = vehicles.ToDictionary(
            vehicle => vehicle.Id,
            vehicle => ExpandAliases(new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }));

        List<string> existingKeys;
        try
        {
            existingKeys = await db.IntegrationMappings
                .Where(mapping => mapping.Active &&
                                  mapping.Provider == "DotTracking" &&
                                  mapping.TmsEntityType == "Vehicle" &&
                                  identifiers.Contains(mapping.ExternalKey))
                .Select(mapping => mapping.ExternalKey)
                .ToListAsync(ct);
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            return 0;
        }
        var existing = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var identifier in identifiers.Where(identifier => !existing.Contains(identifier)))
        {
            var matches = directAliases
                .Where(pair => MatchesVehicleIdentifier(pair.Value, identifier))
                .Select(pair => pair.Key)
                .Distinct()
                .Take(2)
                .ToList();

            if (matches.Count != 1) continue;

            db.IntegrationMappings.Add(new IntegrationMapping
            {
                Provider = "DotTracking",
                ExternalKey = identifier,
                ExternalLabel = identifier,
                TmsEntityType = "Vehicle",
                TmsEntityId = matches[0],
                Active = true,
                Notes = "Auto-linked from unique RoadTech/Falcon telemetry identifier matched to the planned vehicle registration/fleet aliases. Exact provider key retained for indexed tracking history.",
                UpdatedBy = "RoadTech geofence identity repair"
            });
            added++;
        }

        if (added > 0)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (Exception exception) when (SchemaUnavailable(exception) || exception is DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return 0;
            }
        }
        return added;
    }
 
    public static IReadOnlyCollection<string> VehicleAliasVariants(string? value)
    {
        var normalised = NormaliseVehicle(value);
        if (normalised.Length == 0) return [];
 
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalised };
        for (var length = 3; length <= Math.Min(6, normalised.Length); length++) aliases.Add(normalised[^length..]);
 
        if (normalised.Length == 7 && char.IsLetter(normalised[0]) && char.IsLetter(normalised[1]) && char.IsDigit(normalised[2]) && char.IsDigit(normalised[3]))
            aliases.Add(normalised[2..]);
 
        if (normalised.EndsWith("H", StringComparison.OrdinalIgnoreCase) && normalised.Length > 4) aliases.Add(normalised[..^1]);
        return aliases;
    }
 
    public static bool MatchesVehicleIdentifier(IReadOnlyCollection<string> aliases, string? providerIdentifier)
    {
        if (aliases.Count == 0 || string.IsNullOrWhiteSpace(providerIdentifier)) return false;
        var keys = ExpandAliases(aliases);
        return VehicleAliasVariants(providerIdentifier).Any(keys.Contains);
    }
 
    public static VehicleLiveStatus? MatchLive(IReadOnlyCollection<string> aliases, IEnumerable<VehicleLiveStatus> statuses)
        => statuses.Where(status => MatchesVehicleIdentifier(aliases, status.VehicleIdentifier)).OrderByDescending(status => status.LastEventTimeUtc).FirstOrDefault();
 
    public static TachoVehicleDriverStatus? MatchTacho(IReadOnlyCollection<string> aliases, IReadOnlyDictionary<string, TachoVehicleDriverStatus> statuses)
    {
        var keys = ExpandAliases(aliases);
        return statuses
            .Select(pair => new
            {
                Status = pair.Value,
                MatchLength = VehicleAliasVariants(pair.Key).Where(keys.Contains).Select(alias => alias.Length).DefaultIfEmpty(0).Max()
            })
            // A real TachoMaster duty remains valid identity evidence even when the separate
            // remaining-drive metrics endpoint is temporarily delayed. Hours availability is
            // assessed separately; do not turn an existing duty into "Tacho unavailable".
            .Where(item => item.MatchLength > 0 && item.Status.EvidenceSource == "TachoMasterDuty" && item.Status.MemberCode > 0)
            .OrderByDescending(item => item.MatchLength)
            .ThenByDescending(item => item.Status.DutyStartUtc)
            .Select(item => item.Status)
            .FirstOrDefault();
    }
 
    public static TachoVehicleDriverStatus? MatchTachoForDriver(
        IReadOnlyCollection<string> aliases,
        Driver? driver,
        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> statusesByVehicle)
    {
        var keys = ExpandAliases(aliases);
        var candidates = statusesByVehicle
            .Select(pair => new
            {
                MatchLength = VehicleAliasVariants(pair.Key).Where(keys.Contains).Select(alias => alias.Length).DefaultIfEmpty(0).Max(),
                pair.Value
            })
            .Where(item => item.MatchLength > 0)
            .SelectMany(item => item.Value.Select(status => (item.MatchLength, Status: status)))
            // Falcon-only identity rows use MemberCode 0. They can identify a current occupant,
            // but they are not a TachoMaster duty and must never carry legal-hours authority.
            .Where(item => item.Status.EvidenceSource == "TachoMasterDuty" && item.Status.MemberCode > 0)
            .ToList();
 
        if (candidates.Count == 0) return null;
 
        if (driver is not null)
        {
            return candidates
                .Where(item => DriverMatches(driver, item.Status))
                .OrderByDescending(item => item.MatchLength)
                .ThenByDescending(item => item.Status.DutyStartUtc)
                .Select(item => item.Status)
                .FirstOrDefault();
        }
 
        return candidates.OrderByDescending(item => item.MatchLength).ThenByDescending(item => item.Status.DutyStartUtc).Select(item => item.Status).First();
    }

    public static TachoVehicleDriverStatus? MatchLiveDriverIdentityForVehicle(
        IReadOnlyCollection<string> aliases,
        Driver? driver,
        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> statusesByVehicle)
    {
        var keys = ExpandAliases(aliases);
        var candidates = statusesByVehicle
            .Select(pair => new
            {
                MatchLength = VehicleAliasVariants(pair.Key).Where(keys.Contains).Select(alias => alias.Length).DefaultIfEmpty(0).Max(),
                pair.Value
            })
            .Where(item => item.MatchLength > 0)
            .SelectMany(item => item.Value.Select(status => (item.MatchLength, Status: status)))
            .ToList();

        if (candidates.Count == 0) return null;
        if (driver is not null)
        {
            var driverMatch = candidates
                .Where(item => DriverMatches(driver, item.Status))
                .OrderByDescending(item => item.MatchLength)
                .ThenByDescending(item => item.Status.EvidenceSource == "TachoMasterDuty")
                .ThenByDescending(item => item.Status.DutyStartUtc)
                .Select(item => item.Status)
                .FirstOrDefault();
            if (driverMatch is not null) return driverMatch;
        }

        return candidates
            .OrderByDescending(item => item.MatchLength)
            .ThenByDescending(item => item.Status.EvidenceSource == "TachoMasterDuty")
            .ThenByDescending(item => item.Status.DutyStartUtc)
            .Select(item => item.Status)
            .First();
    }
 
    public static DateTimeOffset? FirstMovement(IReadOnlyCollection<string> aliases, IEnumerable<VehicleTrackingEvent> events, DateTimeOffset? notBeforeUtc = null)
        => events.Where(item => MatchesVehicleIdentifier(aliases, item.VehicleIdentifier))
            .Where(item => notBeforeUtc is null || item.EventTimeUtc >= notBeforeUtc.Value)
            .Where(item => item.IsMoving == true || item.SpeedKph.GetValueOrDefault() > 2)
            .OrderBy(item => item.EventTimeUtc).Select(item => (DateTimeOffset?)item.EventTimeUtc).FirstOrDefault();
 
    public static bool DriverMatches(Driver? allocatedDriver, TachoVehicleDriverStatus? tacho)
    {
        if (allocatedDriver is null || tacho is null) return false;
        if (CardsMatch(allocatedDriver.TachoCardNumber, tacho.CardNumber)) return true;
        if (int.TryParse(allocatedDriver.TachoMasterDriverId, out var memberCode) && memberCode > 0 && memberCode == tacho.MemberCode) return true;
        if (!string.IsNullOrWhiteSpace(tacho.EmployeeNumber) &&
            string.Equals(NormaliseVehicle(tacho.EmployeeNumber), NormaliseVehicle(allocatedDriver.EmployeeNumber), StringComparison.OrdinalIgnoreCase)) return true;
 
        var tachoName = NormalisePerson(tacho.DriverName);
        if (tachoName.Length == 0) return false;
        var plannedNames = new[] { allocatedDriver.TachoName, allocatedDriver.DisplayName }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalisePerson).Where(value => value.Length > 0);
        return plannedNames.Any(value => string.Equals(value, tachoName, StringComparison.OrdinalIgnoreCase));
    }
 
    public static string DriverEvidenceStatus(Driver? allocatedDriver, TachoVehicleDriverStatus? tacho)
    {
        if (allocatedDriver is null) return "NoPlannedDriver";
        if (tacho is null) return "NoTachoDuty";
        return DriverMatches(allocatedDriver, tacho) ? "Matched" : "Mismatch";
    }
 
    public static string NormaliseVehicle(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
 
    public static string NormalisePerson(string? value) => string.Join(' ', (value ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
        .Where(word => word.Length > 0).OrderBy(word => word, StringComparer.Ordinal));
 
    private static bool CardsMatch(string? left, string? right)
    {
        var a = NormaliseVehicle(left);
        var b = NormaliseVehicle(right);
        if (a.Length < 8 || b.Length < 8) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }
 
    private static HashSet<string> ExpandAliases(IEnumerable<string?> values)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
            foreach (var alias in VehicleAliasVariants(value)) aliases.Add(alias);
        return aliases;
    }
 
    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
