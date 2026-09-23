using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record MasterDataDuplicateCandidate(
    string CandidateId,
    string EntityType,
    int Confidence,
    string Reason,
    bool CanAutoMerge,
    MasterDataDuplicateRecord Canonical,
    IReadOnlyList<MasterDataDuplicateRecord> Duplicates,
    IReadOnlyList<string> PreservedFields);

public sealed record MasterDataDuplicateRecord(
    Guid Id,
    string Code,
    string Name,
    string? Address,
    string? Postcode,
    bool Active,
    IReadOnlyDictionary<string, object?> Fields);

public sealed record MasterDataDuplicateMergeRequest(Guid CanonicalId, IReadOnlyList<Guid> DuplicateIds, string? Note);
public sealed record MasterDataDuplicateRejectRequest(string CandidateId, string EntityType, string? Note);
public sealed record MasterDataDuplicateMergeResult(int Merged, int Reviewed, IReadOnlyList<string> Messages);

public static class MasterDataDuplicateReviewService
{
    public static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindCandidatesAsync(TmsDbContext db, string? entityType, CancellationToken ct)
    {
        var type = (Clean(entityType ?? "sites") ?? "sites").ToLowerInvariant();
        var candidates = type switch
        {
            "site" or "sites" => await FindSiteCandidatesAsync(db, ct),
            "driver" or "drivers" => await FindDriverCandidatesAsync(db, ct),
            "vehicle" or "vehicles" => await FindVehicleCandidatesAsync(db, ct),
            "trailer" or "trailers" => await FindTrailerCandidatesAsync(db, ct),
            "market" or "markets" => await FindMarketCandidatesAsync(db, ct),
            _ => []
        };

        return await ExcludeRejectedCandidatesAsync(db, candidates, ct);
    }

    public static async Task<MasterDataDuplicateMergeResult> AutoMergeHighConfidenceAsync(TmsDbContext db, string? entityType, string actor, CancellationToken ct)
    {
        var messages = new List<string>();
        var candidates = await FindCandidatesAsync(db, entityType, ct);
        var merged = 0;

        foreach (var candidate in candidates.Where(candidate => candidate.CanAutoMerge).Take(50))
        {
            var result = await MergeAsync(
                db,
                candidate.EntityType,
                new MasterDataDuplicateMergeRequest(candidate.Canonical.Id, candidate.Duplicates.Select(row => row.Id).ToList(), "Automatic high-confidence master-data duplicate merge"),
                actor,
                ct);
            merged += result.Merged;
            messages.AddRange(result.Messages);
        }

        return new MasterDataDuplicateMergeResult(merged, candidates.Count, messages);
    }

    public static async Task<MasterDataDuplicateMergeResult> MergeAsync(TmsDbContext db, string entityType, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var type = (Clean(entityType) ?? string.Empty).ToLowerInvariant();
        return type switch
        {
            "site" or "sites" => await MergeSitesAsync(db, request, actor, ct),
            "driver" or "drivers" => await MergeDriversAsync(db, request, actor, ct),
            "vehicle" or "vehicles" => await MergeVehiclesAsync(db, request, actor, ct),
            "trailer" or "trailers" => await MergeTrailersAsync(db, request, actor, ct),
            "market" or "markets" => await MergeMarketsAsync(db, request, actor, ct),
            _ => new MasterDataDuplicateMergeResult(0, 0, [$"Unsupported duplicate entity type '{entityType}'."])
        };
    }

    public static async Task RejectAsync(TmsDbContext db, MasterDataDuplicateRejectRequest request, string actor, CancellationToken ct)
    {
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = $"Duplicate:{Clean(request.EntityType)}",
            EntityId = Guid.Empty,
            Action = "RejectedDuplicateCandidate",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new { request.CandidateId, request.Note, rejectedAtUtc = DateTimeOffset.UtcNow })
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> ExcludeRejectedCandidatesAsync(TmsDbContext db, IReadOnlyList<MasterDataDuplicateCandidate> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return candidates;

        var rejectedPayloads = await db.MasterDataAudits.AsNoTracking()
            .Where(audit => audit.Action == "RejectedDuplicateCandidate" && audit.EntityType.StartsWith("Duplicate:"))
            .OrderByDescending(audit => audit.ChangedAtUtc)
            .Select(audit => audit.ChangesJson)
            .Take(500)
            .ToListAsync(ct);

        if (rejectedPayloads.Count == 0) return candidates;

        var rejectedIds = rejectedPayloads
            .Select(RejectedCandidateId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(candidate =>
                !rejectedIds.Contains(candidate.CandidateId) &&
                !rejectedPayloads.Any(payload => payload?.Contains(candidate.CandidateId, StringComparison.OrdinalIgnoreCase) == true))
            .ToList();
    }

    private static string? RejectedCandidateId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (string.Equals(property.Name, "CandidateId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindSiteCandidatesAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.Sites.AsNoTracking().Where(row => row.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, rows, ct);
        var groups = new Dictionary<string, HashSet<Site>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var code = Normalise(row.ExternalCode);
            var name = Normalise(row.Name);
            var driverName = Normalise(row.DriverTextName);
            var address = NormaliseAddress(row.CollectionAddress);
            var postcode = ExtractPostcode(row.CollectionAddress);
            var namesAndAliases = SiteIdentityTokens(row).ToList();

            if (code.Length >= 2) Add(groups, $"site-code:{code}", row);
            if (name.Length >= 5 && !string.IsNullOrWhiteSpace(postcode)) Add(groups, $"site-name-postcode:{name}|{postcode}", row);
            if (driverName.Length >= 5 && !string.IsNullOrWhiteSpace(postcode)) Add(groups, $"site-driver-postcode:{driverName}|{postcode}", row);
            if (name.Length >= 5 && address.Length >= 8) Add(groups, $"site-name-address:{name}|{address}", row);
            if (address.Length >= 12 && !string.IsNullOrWhiteSpace(postcode)) Add(groups, $"site-address-postcode:{address}|{postcode}", row);
            if (name.Length >= 8 && string.IsNullOrWhiteSpace(postcode) && address.Length == 0) Add(groups, $"site-name-only:{name}", row);

            foreach (var token in namesAndAliases)
            {
                if (token.Length < 5) continue;
                if (!string.IsNullOrWhiteSpace(postcode)) Add(groups, $"site-token-postcode:{token}|{postcode}", row);
                if (address.Length >= 8) Add(groups, $"site-token-address:{token}|{address}", row);
                if (string.IsNullOrWhiteSpace(postcode) && address.Length == 0 && token.Length >= 8) Add(groups, $"site-token-only:{token}", row);
            }
        }

        return DistinctGroups(groups.Values, row => row.Id)
            .Select(BuildSiteCandidate)
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Canonical.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindDriverCandidatesAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.Drivers.AsNoTracking().Where(row => row.Active).ToListAsync(ct);
        // TachoMasterDriverId is [NotMapped] — it must be populated by EnrichDriversAsync
        // or sameTacho will always be false and drivers will incorrectly score at 96 instead of 99.
        await MasterDetailStore.EnrichDriversAsync(db, rows, ct);
        var groups = new Dictionary<string, HashSet<Driver>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var tacho    = Normalise(row.TachoMasterDriverId);
            var employee = Normalise(row.EmployeeNumber);
            var licence  = Normalise(row.DrivingLicenceNumber);
            var card     = Normalise(row.TachoCardNumber);

            // Hard identity keys only — name+mobile intentionally excluded.
            // Two drivers with the same name and mobile number are not necessarily the same person;
            // only a shared Tacho member code, employee number, licence, or card proves identity.
            if (tacho.Length > 0)    Add(groups, $"driver-tacho:{tacho}", row);
            if (employee.Length > 0) Add(groups, $"driver-employee:{employee}", row);
            if (licence.Length > 6)  Add(groups, $"driver-licence:{licence}", row);
            if (card.Length > 6)     Add(groups, $"driver-card:{card}", row);
        }

        return DistinctGroups(groups.Values, row => row.Id)
            .Select(BuildDriverCandidate)
            .OrderByDescending(candidate => candidate.Confidence)
            .ToList();
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindVehicleCandidatesAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.Vehicles.AsNoTracking().Where(row => row.Active).ToListAsync(ct);
        var groups = new Dictionary<string, HashSet<Vehicle>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var registration = NormaliseRegistration(row.Registration);
            var fleetio = Normalise(row.FleetioId);
            var vin = Normalise(row.VIN);

            if (registration.Length > 0) Add(groups, $"vehicle-registration:{registration}", row);
            if (fleetio.Length > 0) Add(groups, $"vehicle-fleetio:{fleetio}", row);
            if (vin.Length > 8) Add(groups, $"vehicle-vin:{vin}", row);
        }

        return DistinctGroups(groups.Values, row => row.Id)
            .Select(BuildVehicleCandidate)
            .OrderByDescending(candidate => candidate.Confidence)
            .ToList();
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindTrailerCandidatesAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.Trailers.AsNoTracking().Where(row => row.Active).ToListAsync(ct);
        var groups = new Dictionary<string, HashSet<Trailer>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var exact = NormaliseRegistration(row.TrailerNumber);
            var canonical = NormaliseTrailerNumber(row.TrailerNumber);

            if (exact.Length > 0) Add(groups, $"trailer-exact:{exact}", row);
            if (canonical.Length > 0) Add(groups, $"trailer-canonical:{canonical}", row);
        }

        return DistinctGroups(groups.Values, row => row.Id)
            .Select(BuildTrailerCandidate)
            .OrderByDescending(candidate => candidate.Confidence)
            .ToList();
    }

    private static async Task<IReadOnlyList<MasterDataDuplicateCandidate>> FindMarketCandidatesAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.MarketContacts.AsNoTracking().Where(row => row.Active).ToListAsync(ct);
        var groups = new Dictionary<string, HashSet<MarketContact>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var market = Normalise(CanonicalMarket(row.Market));
            var name = Normalise(Clean(row.Name));
            var stand = Normalise(Clean(row.StandOrLocation) ?? InferStand(row.Name));
            var sender = Normalise(Clean(row.Sender));

            if (market.Length > 0 && name.Length > 0) Add(groups, $"market-name-stand:{market}|{name}|{stand}", row);
            if (market.Length > 0 && sender.Length > 0) Add(groups, $"market-sender:{market}|{sender}", row);
            if (market.Length > 0 && name.Length > 0 && sender.Length > 0) Add(groups, $"market-name-sender:{market}|{name}|{sender}", row);
        }

        return DistinctGroups(groups.Values, row => row.Id)
            .Select(BuildMarketCandidate)
            .OrderByDescending(candidate => candidate.Confidence)
            .ToList();
    }

    private static async Task<MasterDataDuplicateMergeResult> MergeSitesAsync(TmsDbContext db, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var canonical = await db.Sites.FirstOrDefaultAsync(row => row.Id == request.CanonicalId && row.Active, ct);
        if (canonical is null) return new MasterDataDuplicateMergeResult(0, 0, ["Canonical site was not found."]);

        var duplicates = await db.Sites.Where(row => request.DuplicateIds.Contains(row.Id) && row.Id != canonical.Id && row.Active).ToListAsync(ct);
        if (duplicates.Count == 0) return new MasterDataDuplicateMergeResult(0, 1, ["No active site duplicates were found to merge."]);

        await MasterDetailStore.EnrichSitesAsync(db, new[] { canonical }.Concat(duplicates).ToList(), ct);
        var duplicateIds = duplicates.Select(row => row.Id).ToList();
        var messages = new List<string>();

        foreach (var duplicate in duplicates)
        {
            PreserveSiteFields(canonical, duplicate, messages);
            duplicate.Active = false;
            await MasterDetailStore.SaveAsync(db, "site", duplicate.ExternalCode, JsonSerializer.Serialize(duplicate), "Duplicate merge archived source site", actor, ct);
        }

        var geofences = await db.SiteGeofences.Where(row => row.SiteId.HasValue && duplicateIds.Contains(row.SiteId.Value)).ToListAsync(ct);
        foreach (var geofence in geofences) geofence.SiteId = canonical.Id;

        var runStops = await db.RunStops.Where(row => duplicateIds.Contains(row.SiteId)).ToListAsync(ct);
        foreach (var runStop in runStops) runStop.SiteId = canonical.Id;

        var mappingCount = await ReassignIntegrationMappingsAsync(db, "Site", duplicateIds, canonical.Id, actor, ct);
        canonical.Aliases = MergeAliases(canonical, duplicates);
        await MasterDetailStore.SaveAsync(db, "site", canonical.ExternalCode, JsonSerializer.Serialize(canonical), "Duplicate merge preserved canonical site address/routing detail", actor, ct);

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = canonical.Id,
            Action = "DuplicateMerge",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.ExternalCode, merged = duplicates.Select(row => row.ExternalCode), request.Note, geofencesReassigned = geofences.Count, runStopsReassigned = runStops.Count, mappingsReassigned = mappingCount, messages })
        });
        await db.SaveChangesAsync(ct);

        messages.Insert(0, $"Merged {duplicates.Count} site duplicate(s) into {canonical.Name}; reassigned {geofences.Count} geofence(s), {runStops.Count} run stop(s) and {mappingCount} integration mapping(s).");
        return new MasterDataDuplicateMergeResult(duplicates.Count, duplicates.Count + 1, messages);
    }

    private static async Task<MasterDataDuplicateMergeResult> MergeDriversAsync(TmsDbContext db, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var canonical = await db.Drivers.FirstOrDefaultAsync(row => row.Id == request.CanonicalId && row.Active, ct);
        if (canonical is null) return new MasterDataDuplicateMergeResult(0, 0, ["Canonical driver was not found."]);

        var duplicates = await db.Drivers.Where(row => request.DuplicateIds.Contains(row.Id) && row.Id != canonical.Id && row.Active).ToListAsync(ct);
        if (duplicates.Count == 0) return new MasterDataDuplicateMergeResult(0, 1, ["No active driver duplicates were found to merge."]);

        var duplicateIds = duplicates.Select(row => row.Id).ToList();
        foreach (var duplicate in duplicates)
        {
            canonical.TachoMasterDriverId = Preserve(canonical.TachoMasterDriverId, duplicate.TachoMasterDriverId);
            canonical.MobileNumber = Preserve(canonical.MobileNumber, duplicate.MobileNumber);
            canonical.DriverType = Preserve(canonical.DriverType, duplicate.DriverType);
            canonical.DriverGroup = Preserve(canonical.DriverGroup, duplicate.DriverGroup);
            canonical.Skills = Preserve(canonical.Skills, duplicate.Skills);
            duplicate.Active = false;
        }

        var loads = await db.Loads.Where(row => row.DriverId.HasValue && duplicateIds.Contains(row.DriverId.Value)).ToListAsync(ct);
        foreach (var load in loads) load.DriverId = canonical.Id;

        var planRuns = await db.PlanProposalRuns.Where(row => row.DriverId.HasValue && duplicateIds.Contains(row.DriverId.Value)).ToListAsync(ct);
        foreach (var run in planRuns) run.DriverId = canonical.Id;

        var candidates = await db.PlanProposalCandidates.Where(row => duplicateIds.Contains(row.DriverId)).ToListAsync(ct);
        foreach (var candidate in candidates) candidate.DriverId = canonical.Id;

        var runResources = await db.RunResourceAllocations.Where(row => duplicateIds.Contains(row.DriverId)).ToListAsync(ct);
        foreach (var allocation in runResources) allocation.DriverId = canonical.Id;

        var statusLogs = await db.DriverStatusLogs.Where(row => row.DriverId.HasValue && duplicateIds.Contains(row.DriverId.Value)).ToListAsync(ct);
        foreach (var log in statusLogs) log.DriverId = canonical.Id;

        var mappings = await ReassignIntegrationMappingsAsync(db, "Driver", duplicateIds, canonical.Id, actor, ct);
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Driver",
            EntityId = canonical.Id,
            Action = "DuplicateMerge",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.EmployeeNumber, merged = duplicates.Select(row => row.EmployeeNumber), request.Note, loadsReassigned = loads.Count, planRunsReassigned = planRuns.Count, candidatesReassigned = candidates.Count, runResourcesReassigned = runResources.Count, statusLogsReassigned = statusLogs.Count, mappingsReassigned = mappings })
        });
        await db.SaveChangesAsync(ct);

        return new MasterDataDuplicateMergeResult(duplicates.Count, duplicates.Count + 1, [$"Merged {duplicates.Count} driver duplicate(s) into {canonical.DisplayName}; reassigned {loads.Count + planRuns.Count + candidates.Count + runResources.Count + statusLogs.Count + mappings} linked record(s)."]);
    }

    private static async Task<MasterDataDuplicateMergeResult> MergeVehiclesAsync(TmsDbContext db, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var canonical = await db.Vehicles.FirstOrDefaultAsync(row => row.Id == request.CanonicalId && row.Active, ct);
        if (canonical is null) return new MasterDataDuplicateMergeResult(0, 0, ["Canonical vehicle was not found."]);

        var duplicates = await db.Vehicles.Where(row => request.DuplicateIds.Contains(row.Id) && row.Id != canonical.Id && row.Active).ToListAsync(ct);
        if (duplicates.Count == 0) return new MasterDataDuplicateMergeResult(0, 1, ["No active vehicle duplicates were found to merge."]);

        var duplicateIds = duplicates.Select(row => row.Id).ToList();
        foreach (var duplicate in duplicates)
        {
            canonical.FleetNumber = Preserve(canonical.FleetNumber, duplicate.FleetNumber);
            canonical.Abbreviation = Preserve(canonical.Abbreviation, duplicate.Abbreviation);
            canonical.FuelProvider = Preserve(canonical.FuelProvider, duplicate.FuelProvider);
            canonical.CabMobile = Preserve(canonical.CabMobile, duplicate.CabMobile);
            canonical.FuelPin = Preserve(canonical.FuelPin, duplicate.FuelPin);
            canonical.ShellCard = Preserve(canonical.ShellCard, duplicate.ShellCard);
            canonical.BpRedCard = Preserve(canonical.BpRedCard, duplicate.BpRedCard);
            canonical.BpPlainCard = Preserve(canonical.BpPlainCard, duplicate.BpPlainCard);
            canonical.FleetioId = Preserve(canonical.FleetioId, duplicate.FleetioId);
            canonical.FleetioName = Preserve(canonical.FleetioName, duplicate.FleetioName);
            canonical.FleetioStatus = Preserve(canonical.FleetioStatus, duplicate.FleetioStatus);
            duplicate.Active = false;
        }

        var loads = await db.Loads.Where(row => row.VehicleId.HasValue && duplicateIds.Contains(row.VehicleId.Value)).ToListAsync(ct);
        foreach (var load in loads) load.VehicleId = canonical.Id;

        var planRuns = await db.PlanProposalRuns.Where(row => row.VehicleId.HasValue && duplicateIds.Contains(row.VehicleId.Value)).ToListAsync(ct);
        foreach (var run in planRuns) run.VehicleId = canonical.Id;

        var candidates = await db.PlanProposalCandidates.Where(row => duplicateIds.Contains(row.VehicleId)).ToListAsync(ct);
        foreach (var candidate in candidates) candidate.VehicleId = canonical.Id;

        var runResources = await db.RunResourceAllocations.Where(row => duplicateIds.Contains(row.VehicleId)).ToListAsync(ct);
        foreach (var allocation in runResources) allocation.VehicleId = canonical.Id;

        var mappings = await ReassignIntegrationMappingsAsync(db, "Vehicle", duplicateIds, canonical.Id, actor, ct);
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Vehicle",
            EntityId = canonical.Id,
            Action = "DuplicateMerge",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.Registration, merged = duplicates.Select(row => row.Registration), request.Note, loadsReassigned = loads.Count, planRunsReassigned = planRuns.Count, candidatesReassigned = candidates.Count, runResourcesReassigned = runResources.Count, mappingsReassigned = mappings })
        });
        await db.SaveChangesAsync(ct);

        return new MasterDataDuplicateMergeResult(duplicates.Count, duplicates.Count + 1, [$"Merged {duplicates.Count} vehicle duplicate(s) into {canonical.Registration}; preserved fuel/card fields and reassigned {loads.Count + planRuns.Count + candidates.Count + runResources.Count + mappings} linked record(s)."]);
    }

    private static async Task<MasterDataDuplicateMergeResult> MergeTrailersAsync(TmsDbContext db, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var canonical = await db.Trailers.FirstOrDefaultAsync(row => row.Id == request.CanonicalId && row.Active, ct);
        if (canonical is null) return new MasterDataDuplicateMergeResult(0, 0, ["Canonical trailer was not found."]);

        var duplicates = await db.Trailers.Where(row => request.DuplicateIds.Contains(row.Id) && row.Id != canonical.Id && row.Active).ToListAsync(ct);
        if (duplicates.Count == 0) return new MasterDataDuplicateMergeResult(0, 1, ["No active trailer duplicates were found to merge."]);

        var duplicateIds = duplicates.Select(row => row.Id).ToList();
        foreach (var duplicate in duplicates)
        {
            canonical.Type = Preserve(canonical.Type, duplicate.Type);
            canonical.StandardCapacity ??= duplicate.StandardCapacity;
            canonical.EuroCapacity ??= duplicate.EuroCapacity;
            duplicate.Active = false;
        }

        var loads = await db.Loads.Where(row => row.TrailerId.HasValue && duplicateIds.Contains(row.TrailerId.Value)).ToListAsync(ct);
        foreach (var load in loads) load.TrailerId = canonical.Id;

        var planRuns = await db.PlanProposalRuns.Where(row => row.TrailerId.HasValue && duplicateIds.Contains(row.TrailerId.Value)).ToListAsync(ct);
        foreach (var run in planRuns) run.TrailerId = canonical.Id;

        var runResources = await db.RunResourceAllocations.Where(row => duplicateIds.Contains(row.TrailerId)).ToListAsync(ct);
        foreach (var allocation in runResources) allocation.TrailerId = canonical.Id;

        var mappings = await ReassignIntegrationMappingsAsync(db, "Trailer", duplicateIds, canonical.Id, actor, ct);
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Trailer",
            EntityId = canonical.Id,
            Action = "DuplicateMerge",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.TrailerNumber, merged = duplicates.Select(row => row.TrailerNumber), request.Note, loadsReassigned = loads.Count, planRunsReassigned = planRuns.Count, runResourcesReassigned = runResources.Count, mappingsReassigned = mappings })
        });
        await db.SaveChangesAsync(ct);

        return new MasterDataDuplicateMergeResult(duplicates.Count, duplicates.Count + 1, [$"Merged {duplicates.Count} trailer duplicate(s) into {canonical.TrailerNumber}; preserved type/capacity and reassigned {loads.Count + planRuns.Count + runResources.Count + mappings} linked record(s)."]);
    }

    private static async Task<MasterDataDuplicateMergeResult> MergeMarketsAsync(TmsDbContext db, MasterDataDuplicateMergeRequest request, string actor, CancellationToken ct)
    {
        var canonical = await db.MarketContacts.FirstOrDefaultAsync(row => row.Id == request.CanonicalId && row.Active, ct);
        if (canonical is null) return new MasterDataDuplicateMergeResult(0, 0, ["Canonical market record was not found."]);

        var duplicates = await db.MarketContacts.Where(row => request.DuplicateIds.Contains(row.Id) && row.Id != canonical.Id && row.Active).ToListAsync(ct);
        if (duplicates.Count == 0) return new MasterDataDuplicateMergeResult(0, 1, ["No active market duplicates were found to merge."]);

        canonical.Market = CanonicalMarket(canonical.Market);
        canonical.Name = Clean(canonical.Name) ?? canonical.Name;
        canonical.StandOrLocation = Clean(canonical.StandOrLocation) ?? InferStand(canonical.Name);
        canonical.Salesman = Clean(canonical.Salesman);
        canonical.Sender = Clean(canonical.Sender);

        foreach (var duplicate in duplicates)
        {
            duplicate.Market = CanonicalMarket(duplicate.Market);
            duplicate.Name = Clean(duplicate.Name) ?? duplicate.Name;
            duplicate.StandOrLocation = Clean(duplicate.StandOrLocation) ?? InferStand(duplicate.Name);
            duplicate.Salesman = Clean(duplicate.Salesman);
            duplicate.Sender = Clean(duplicate.Sender);

            canonical.StandOrLocation = Preserve(canonical.StandOrLocation, duplicate.StandOrLocation);
            canonical.Salesman = Preserve(canonical.Salesman, duplicate.Salesman);
            canonical.Sender = Preserve(canonical.Sender, duplicate.Sender);
            duplicate.Active = false;
        }

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Market",
            EntityId = canonical.Id,
            Action = "DuplicateMerge",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.Name, market = canonical.Market, stand = canonical.StandOrLocation, merged = duplicates.Select(row => row.Name), request.Note })
        });
        await db.SaveChangesAsync(ct);

        return new MasterDataDuplicateMergeResult(duplicates.Count, duplicates.Count + 1, [$"Merged {duplicates.Count} market duplicate(s) into {canonical.Market} / {canonical.Name}."]);
    }

    private static MasterDataDuplicateCandidate BuildSiteCandidate(IReadOnlyList<Site> group)
    {
        var canonical = group.OrderByDescending(SiteCompleteness).ThenBy(row => row.ExternalCode, StringComparer.OrdinalIgnoreCase).First();
        var duplicates = group.Where(row => row.Id != canonical.Id).ToList();
        var postcode = ExtractPostcode(canonical.CollectionAddress) ?? duplicates.Select(row => ExtractPostcode(row.CollectionAddress)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var sameExternalCode = duplicates.Any(row => Normalise(row.ExternalCode) == Normalise(canonical.ExternalCode));
        var canonicalTokens = SiteIdentityTokens(canonical).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sameIdentity = duplicates.All(row => SiteIdentityTokens(row).Any(canonicalTokens.Contains));
        var samePostcode = !string.IsNullOrWhiteSpace(postcode) && duplicates.All(row => ExtractPostcode(row.CollectionAddress) == postcode);
        var confidence = sameExternalCode && sameIdentity ? 99 : sameIdentity && samePostcode ? 98 : sameExternalCode ? 94 : sameIdentity ? 88 : samePostcode ? 82 : 70;

        return new MasterDataDuplicateCandidate(
            CandidateId($"site:{canonical.Id}:{string.Join(',', duplicates.Select(row => row.Id))}"),
            "sites",
            confidence,
            sameExternalCode ? "Same site external code; merge preserves address/routing data." : sameIdentity && samePostcode ? "Same site name/alias and postcode." : sameIdentity ? "Same site name/alias; review address before merging." : "Likely duplicate site name/address. Review before merging.",
            confidence >= 95,
            SiteRecord(canonical),
            duplicates.Select(SiteRecord).ToList(),
            ["collectionAddress", "mapLink", "latitude", "longitude", "collectionInstructions", "driverTextName", "aliases", "geofences", "integrationMappings", "runStops"]);
    }

    private static MasterDataDuplicateCandidate BuildDriverCandidate(IReadOnlyList<Driver> group)
    {
        var canonical = group
            .OrderByDescending(row => new[] { row.TachoMasterDriverId, row.TachoCardNumber, row.MobileNumber, row.DriverType, row.DriverGroup, row.Skills, row.DrivingLicenceNumber }
                .Count(value => !string.IsNullOrWhiteSpace(value)))
            .ThenBy(row => row.DisplayName)
            .First();
        var duplicates = group.Where(row => row.Id != canonical.Id).ToList();

        // TachoMasterDriverId is enriched by FindDriverCandidatesAsync (EnrichDriversAsync called first)
        var sameTacho    = !string.IsNullOrWhiteSpace(canonical.TachoMasterDriverId) &&
                           duplicates.Any(row => Normalise(row.TachoMasterDriverId) == Normalise(canonical.TachoMasterDriverId) &&
                                                 !string.IsNullOrWhiteSpace(row.TachoMasterDriverId));
        var sameCard     = !string.IsNullOrWhiteSpace(canonical.TachoCardNumber) &&
                           duplicates.Any(row => Normalise(row.TachoCardNumber) == Normalise(canonical.TachoCardNumber) &&
                                                 !string.IsNullOrWhiteSpace(row.TachoCardNumber));
        var sameLicence  = !string.IsNullOrWhiteSpace(canonical.DrivingLicenceNumber) &&
                           duplicates.Any(row => Normalise(row.DrivingLicenceNumber) == Normalise(canonical.DrivingLicenceNumber) &&
                                                 !string.IsNullOrWhiteSpace(row.DrivingLicenceNumber));
        // Employee number alone is a weaker signal — it can be a data entry repeat.
        // Only auto-merge on employee number if there is also a matching licence or card.
        var sameEmployee = !string.IsNullOrWhiteSpace(canonical.EmployeeNumber) &&
                           duplicates.Any(row => Normalise(row.EmployeeNumber) == Normalise(canonical.EmployeeNumber) &&
                                                 !string.IsNullOrWhiteSpace(row.EmployeeNumber));
        var employeeWithCorroboration = sameEmployee && (sameLicence || sameCard);

        var confidence = sameTacho ? 99
            : sameCard ? 98
            : sameLicence ? 97
            : employeeWithCorroboration ? 96
            : sameEmployee ? 88   // employee alone: review only, not auto-merge
            : 80;

        var reason = sameTacho   ? "Same TachoMaster member code — strong identity match." :
                     sameCard    ? "Same digital tachograph card number." :
                     sameLicence ? "Same driving licence number." :
                     employeeWithCorroboration ? "Same employee number corroborated by matching licence or card." :
                     sameEmployee ? "Same employee number only — review before merging; could be a data entry repeat." :
                     "Grouped by name or partial identity; review all fields before merging.";

        return new MasterDataDuplicateCandidate(
            CandidateId($"driver:{canonical.Id}:{string.Join(',', duplicates.Select(row => row.Id))}"),
            "drivers",
            confidence,
            reason,
            confidence >= 95,
            DriverRecord(canonical),
            duplicates.Select(DriverRecord).ToList(),
            ["tachomasterDriverId", "tachoCardNumber", "mobileNumber", "driverType", "driverGroup", "skills", "linked loads/runs"]);
    }

    private static MasterDataDuplicateCandidate BuildVehicleCandidate(IReadOnlyList<Vehicle> group)
    {
        var canonical = group.OrderByDescending(row => new[] { row.FleetNumber, row.Abbreviation, row.FuelProvider, row.CabMobile, row.FuelPin, row.ShellCard, row.BpRedCard, row.BpPlainCard, row.FleetioId }.Count(value => !string.IsNullOrWhiteSpace(value))).ThenBy(row => row.Registration).First();
        var duplicates = group.Where(row => row.Id != canonical.Id).ToList();

        return new MasterDataDuplicateCandidate(
            CandidateId($"vehicle:{canonical.Id}:{string.Join(',', duplicates.Select(row => row.Id))}"),
            "vehicles",
            99,
            "Same normalised vehicle registration, VIN or Fleetio identity.",
            true,
            VehicleRecord(canonical),
            duplicates.Select(VehicleRecord).ToList(),
            ["fleetNumber", "abbreviation", "fuelProvider", "cabMobile", "fuelPin", "fuel cards", "Fleetio fields", "linked loads/runs"]);
    }

    private static MasterDataDuplicateCandidate BuildTrailerCandidate(IReadOnlyList<Trailer> group)
    {
        var canonical = group.OrderByDescending(row => new object?[] { row.Type, row.StandardCapacity, row.EuroCapacity }.Count(value => value is not null && !string.IsNullOrWhiteSpace(value.ToString()))).ThenBy(row => row.TrailerNumber).First();
        var duplicates = group.Where(row => row.Id != canonical.Id).ToList();

        return new MasterDataDuplicateCandidate(
            CandidateId($"trailer:{canonical.Id}:{string.Join(',', duplicates.Select(row => row.Id))}"),
            "trailers",
            99,
            "Same trailer number after normalising SLH/numeric aliases.",
            true,
            TrailerRecord(canonical),
            duplicates.Select(TrailerRecord).ToList(),
            ["type", "standardCapacity", "euroCapacity", "linked loads/runs"]);
    }

    private static MasterDataDuplicateCandidate BuildMarketCandidate(IReadOnlyList<MarketContact> group)
    {
        var canonical = group
            .OrderByDescending(row => string.Equals(row.Market, CanonicalMarket(row.Market), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(row => new[] { row.StandOrLocation, row.Salesman, row.Sender }.Count(value => !string.IsNullOrWhiteSpace(value)))
            .ThenBy(row => row.Name)
            .First();
        var duplicates = group.Where(row => row.Id != canonical.Id).ToList();
        var sameSender = !string.IsNullOrWhiteSpace(canonical.Sender) && duplicates.Any(row => Normalise(row.Sender) == Normalise(canonical.Sender));
        var sameStand = duplicates.All(row => Normalise(Clean(row.StandOrLocation) ?? InferStand(row.Name)) == Normalise(Clean(canonical.StandOrLocation) ?? InferStand(canonical.Name)));
        var confidence = sameSender ? 96 : sameStand ? 95 : 92;

        return new MasterDataDuplicateCandidate(
            CandidateId($"market:{canonical.Id}:{string.Join(',', duplicates.Select(row => row.Id))}"),
            "markets",
            confidence,
            sameSender ? "Same canonical market and sender/contact identity." : "Same canonical market, customer/sender and stand/location.",
            confidence >= 95,
            MarketRecord(canonical),
            duplicates.Select(MarketRecord).ToList(),
            ["standOrLocation", "salesman", "sender"]);
    }

    private static void PreserveSiteFields(Site canonical, Site duplicate, List<string> messages)
    {
        var beforeAddress = canonical.CollectionAddress;
        canonical.CustomerCode = Preserve(canonical.CustomerCode, duplicate.CustomerCode);
        canonical.CollectionAddress = Preserve(canonical.CollectionAddress, duplicate.CollectionAddress);
        canonical.CollectionInstructions = Preserve(canonical.CollectionInstructions, duplicate.CollectionInstructions);
        canonical.DriverTextName = Preserve(canonical.DriverTextName, duplicate.DriverTextName);
        canonical.MapLink = Preserve(canonical.MapLink, duplicate.MapLink);
        canonical.OperationalRegion = Preserve(canonical.OperationalRegion, duplicate.OperationalRegion);
        canonical.Latitude ??= duplicate.Latitude;
        canonical.Longitude ??= duplicate.Longitude;
        if (string.IsNullOrWhiteSpace(beforeAddress) && !string.IsNullOrWhiteSpace(canonical.CollectionAddress)) messages.Add($"Recovered address for {canonical.Name} from duplicate {duplicate.ExternalCode}.");
    }

    public static string? Preserve(string? existing, string? incoming) =>
        !string.IsNullOrWhiteSpace(existing) ? existing.Trim() : Clean(incoming);

    private static void Add<T>(Dictionary<string, HashSet<T>> groups, string key, T row) where T : notnull
    {
        if (!groups.TryGetValue(key, out var set)) groups[key] = set = [];
        set.Add(row);
    }

    private static IReadOnlyList<IReadOnlyList<T>> DistinctGroups<T>(IEnumerable<HashSet<T>> source, Func<T, Guid> id) where T : notnull
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return source
            .Select(group => group.OrderBy(id).ToList())
            .Where(group => group.Count > 1 && seen.Add(string.Join('|', group.Select(id).OrderBy(value => value))))
            .Cast<IReadOnlyList<T>>()
            .ToList();
    }

    private static async Task<int> ReassignIntegrationMappingsAsync(TmsDbContext db, string entityType, IReadOnlyCollection<Guid> duplicateIds, Guid canonicalId, string actor, CancellationToken ct)
    {
        var mappings = await db.IntegrationMappings
            .Where(row => row.TmsEntityType == entityType && duplicateIds.Contains(row.TmsEntityId))
            .ToListAsync(ct);

        foreach (var mapping in mappings)
        {
            mapping.TmsEntityId = canonicalId;
            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mapping.UpdatedBy = actor;
        }

        return mappings.Count;
    }

    private static MasterDataDuplicateRecord SiteRecord(Site row) => new(row.Id, row.ExternalCode, row.Name, row.CollectionAddress, ExtractPostcode(row.CollectionAddress), row.Active, new Dictionary<string, object?> { ["customerCode"] = row.CustomerCode, ["driverTextName"] = row.DriverTextName, ["collectionInstructions"] = row.CollectionInstructions, ["mapLink"] = row.MapLink, ["latitude"] = row.Latitude, ["longitude"] = row.Longitude, ["aliases"] = row.Aliases, ["region"] = row.OperationalRegion });
    private static MasterDataDuplicateRecord DriverRecord(Driver row) => new(row.Id, row.EmployeeNumber, row.DisplayName, null, null, row.Active, new Dictionary<string, object?> { ["tachoMasterDriverId"] = row.TachoMasterDriverId, ["tachoCardNumber"] = row.TachoCardNumber, ["mobileNumber"] = row.MobileNumber, ["driverType"] = row.DriverType, ["driverGroup"] = row.DriverGroup, ["skills"] = row.Skills, ["licenceNumber"] = row.DrivingLicenceNumber });
    private static MasterDataDuplicateRecord VehicleRecord(Vehicle row) => new(row.Id, row.Registration, row.Registration, null, null, row.Active, new Dictionary<string, object?> { ["fleetNumber"] = row.FleetNumber, ["abbreviation"] = row.Abbreviation, ["fuelProvider"] = row.FuelProvider, ["cabMobile"] = row.CabMobile, ["fuelPin"] = row.FuelPin, ["shellCard"] = row.ShellCard, ["bpRedCard"] = row.BpRedCard, ["bpPlainCard"] = row.BpPlainCard, ["fleetioId"] = row.FleetioId });
    private static MasterDataDuplicateRecord TrailerRecord(Trailer row) => new(row.Id, row.TrailerNumber, row.TrailerNumber, null, null, row.Active, new Dictionary<string, object?> { ["type"] = row.Type, ["standardCapacity"] = row.StandardCapacity, ["euroCapacity"] = row.EuroCapacity });
    private static MasterDataDuplicateRecord MarketRecord(MarketContact row) => new(row.Id, row.MarketKey ?? row.Id.ToString("N"), $"{CanonicalMarket(row.Market)} / {Clean(row.Name) ?? row.Name}", null, null, row.Active, new Dictionary<string, object?> { ["market"] = CanonicalMarket(row.Market), ["standOrLocation"] = Clean(row.StandOrLocation) ?? InferStand(row.Name), ["salesman"] = Clean(row.Salesman), ["sender"] = Clean(row.Sender) });
    private static int SiteCompleteness(Site row) => new object?[] { row.CollectionAddress, row.MapLink, row.Latitude, row.Longitude, row.CollectionInstructions, row.DriverTextName, row.Aliases, row.OperationalRegion, row.CustomerCode }.Count(value => value is not null && !string.IsNullOrWhiteSpace(value.ToString()));
    private static string MergeAliases(Site canonical, IReadOnlyCollection<Site> duplicates) => string.Join(", ", new[] { canonical.Name, canonical.DriverTextName, canonical.Aliases }.Concat(duplicates.SelectMany(row => new[] { row.Name, row.DriverTextName, row.Aliases, row.ExternalCode })).Where(value => !string.IsNullOrWhiteSpace(value)).SelectMany(SplitAliases).Distinct(StringComparer.OrdinalIgnoreCase));
    private static string CandidateId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value.Trim(), @"\s+", " ");
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string NormaliseRegistration(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static IEnumerable<string> SiteIdentityTokens(Site site) =>
        new[] { site.Name, site.DriverTextName, site.ExternalCode }
            .Concat(SplitAliases(site.Aliases))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalise)
            .Where(value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitAliases(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CanonicalMarket(string? value)
    {
        var clean = Clean(value) ?? "General";
        var normal = Normalise(clean);
        if (normal.Contains("covent")) return "Covent";
        if (normal.Contains("spital") || normal.Contains("spit")) return "Spitalfields";
        if (normal.Contains("western")) return "Western";
        if (normal.Contains("sales")) return "Sales";
        if (normal.Contains("sender")) return "Sender";
        return clean;
    }

    private static string? InferStand(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var bracket = Regex.Match(name, @"\(([^)]+)\)\s*$", RegexOptions.IgnoreCase);
        if (bracket.Success) return bracket.Groups[1].Value.Trim();
        var labelled = Regex.Match(name, @"\b(?:stall|stand|unit|units)\s*#?\s*([a-z]?\d{1,4}[a-z]?(?:\s*(?:-|–|—|&|and)\s*[a-z]?\d{1,4}[a-z]?)?)\s*$", RegexOptions.IgnoreCase);
        return labelled.Success ? labelled.Groups[1].Value.Trim() : null;
    }

    private static string NormaliseTrailerNumber(string? value)
    {
        var normalised = NormaliseRegistration(value);
        if (normalised.StartsWith("SLH", StringComparison.OrdinalIgnoreCase)) normalised = normalised[3..];
        normalised = normalised.TrimStart('0');
        return string.IsNullOrWhiteSpace(normalised) ? NormaliseRegistration(value) : normalised;
    }

    private static string NormaliseAddress(string? value) => Normalise(Regex.Replace(value ?? string.Empty, @"\b(road|rd|street|st|avenue|ave|lane|ln|drive|dr|unit|industrial|estate)\b", string.Empty, RegexOptions.IgnoreCase));

    private static string? ExtractPostcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value.ToUpperInvariant(), @"\b([A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\b");
        return match.Success ? Regex.Replace(match.Groups[1].Value, @"\s+", string.Empty) : null;
    }
}
