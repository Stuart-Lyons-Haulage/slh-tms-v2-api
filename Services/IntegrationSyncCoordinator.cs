using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

public sealed record IntegrationSyncResult(string Provider, bool Success, DateTimeOffset CompletedAtUtc, string Message, int Changed = 0);

public sealed class IntegrationSyncCoordinator(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    SageHrClient sageHr,
    SageHrOptions sageOptions,
    FleetioClient fleetioClient,
    DistributedLeaseManager leases,
    ILogger<IntegrationSyncCoordinator> logger)
{
    public async Task<IntegrationSyncResult> SyncTachoMasterAsync(string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.TachoMaster, TimeSpan.FromMinutes(2), ct);
        if (lease is null)
            return new("TachoMaster", false, DateTimeOffset.UtcNow, "TachoMaster sync skipped because another distributed writer currently holds the integration lease.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LostToken);
        return await SyncTachoMasterCoreAsync(actor, linked.Token);
    }

    /// <summary>Only for callers that already hold <see cref="IntegrationLeaseNames.TachoMaster"/>.</summary>
    public async Task<IntegrationSyncResult> SyncTachoMasterCoreAsync(string actor, CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured)
            return new("TachoMaster", false, DateTimeOffset.UtcNow, $"TachoMaster is not configured: {string.Join(", ", tachoMaster.MissingSettings)}.");

        try
        {
            var profiles = await tachoMaster.GetDriverProfilesAsync(ct);
            var drivers = await db.Drivers.Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
            await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

            var byMemberCode = profiles.GroupBy(profile => profile.MemberCode).ToDictionary(group => group.Key, group => group.First());
            var byCard = UniqueLookup(profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.CardNumber)), profile => Normalise(profile.CardNumber));
            var byEmployee = UniqueLookup(profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.EmployeeNumber)), profile => Normalise(profile.EmployeeNumber));
            var byName = UniqueLookup(profiles, profile => NormalisePersonName(profile.DriverName));
            var matched = 0;
            var matchedMemberCodes = new HashSet<int>();

            foreach (var driver in drivers)
            {
            TachoDriverProfile? profile = null;

            // Stable identities first. TachoMaster member code and tachograph card must win over
            // mutable employee/name fields so daily compliance keeps the same driver identity.
            if (int.TryParse(driver.TachoMasterDriverId, out var memberCode) && memberCode > 0)
                byMemberCode.TryGetValue(memberCode, out profile);

            if (profile is null && !string.IsNullOrWhiteSpace(driver.TachoCardNumber))
                byCard.TryGetValue(Normalise(driver.TachoCardNumber), out profile);

            if (profile is null && !string.IsNullOrWhiteSpace(driver.EmployeeNumber))
                byEmployee.TryGetValue(Normalise(driver.EmployeeNumber), out profile);

            if (profile is null && !string.IsNullOrWhiteSpace(driver.TachoName))
                byName.TryGetValue(NormalisePersonName(driver.TachoName), out profile);

            if (profile is null)
                byName.TryGetValue(NormalisePersonName(driver.DisplayName), out profile);

            if (profile is null) continue;

            driver.TachoMasterDriverId = profile.MemberCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            driver.TachoCardNumber = profile.CardNumber ?? driver.TachoCardNumber;
            driver.TachoName = string.IsNullOrWhiteSpace(driver.TachoName) ? profile.DriverName : driver.TachoName;
            driver.TachoDriveAvailableTodayMinutes = profile.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes;
            driver.TachoDriveAvailableWeekMinutes = profile.DriveAvailableWeekMinutes ?? driver.TachoDriveAvailableWeekMinutes;
            driver.TachoWorkAvailableWeekMinutes = profile.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;
            driver.LastTachoSyncUtc = DateTimeOffset.UtcNow;
            await MasterDetailStore.SaveAsync(db, "driver", driver.EmployeeNumber, JsonSerializer.Serialize(driver), "TachoMaster driver directory", actor, ct);
            matched++;
            matchedMemberCodes.Add(profile.MemberCode);
            }

            await db.SaveChangesAsync(ct);
            var unmatchedProfiles = profiles.Select(profile => profile.MemberCode).Distinct().Count(code => !matchedMemberCodes.Contains(code));
            return new("TachoMaster", true, DateTimeOffset.UtcNow,
                $"TachoMaster matched {matched} of {drivers.Count} active TMS drivers; {unmatchedProfiles} TachoMaster member profile(s) remain unmatched. Identity order: member code, tacho card, employee number, name.", matched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TachoMaster driver directory sync failed.");
            var status = ex is HttpRequestException http && http.StatusCode is not null ? $" HTTP {(int)http.StatusCode}." : string.Empty;
            return new("TachoMaster", false, DateTimeOffset.UtcNow,
                $"TachoMaster sync failed.{status} {ex.GetBaseException().Message} No TMS driver records were intentionally updated by the sync result.");
        }
    }

    public async Task<IntegrationSyncResult> SyncSageHrAsync(string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.SageHr, TimeSpan.FromMinutes(30), ct);
        if (lease is null)
            return new("Sage HR", false, DateTimeOffset.UtcNow, "Sage HR sync skipped because another distributed writer currently holds the integration lease.");
        return await SyncSageHrCoreAsync(actor, ct);
    }

    internal async Task<IntegrationSyncResult> SyncSageHrCoreAsync(string actor, CancellationToken ct)
    {
        if (!sageHr.IsConfigured)
            return new("Sage HR", false, DateTimeOffset.UtcNow, $"Sage HR is not configured: {string.Join(", ", sageHr.MissingSettings)}.");

        var employees = await sageHr.GetActiveEmployeesAsync(ct);
        var rawCandidates = employees.Where(IsDriver).ToList();
        var candidates = rawCandidates
            .GroupBy(employee => string.IsNullOrWhiteSpace(employee.EmployeeNumber) ? $"SAGE-{employee.Id}" : employee.EmployeeNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        var created = 0;
        var updated = 0;
        var skipped = rawCandidates.Count - candidates.Count;
        var existingNumbers = (await db.Drivers.AsNoTracking().Select(driver => driver.EmployeeNumber).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var employee in candidates)
        {
            var employeeNumber = ClipRequired(string.IsNullOrWhiteSpace(employee.EmployeeNumber) ? $"SAGE-{employee.Id}" : employee.EmployeeNumber.Trim(), 40);
            var displayName = ClipRequired($"{employee.FirstName} {employee.LastName}".Trim(), 160);
            if (string.IsNullOrWhiteSpace(displayName)) { skipped++; continue; }
            var mobileNumber = Clip(employee.MobilePhone, 40);
            var driverType = Clip(employee.Position, 80);
            var driverGroup = Clip(employee.Team, 80);
            if (!existingNumbers.Contains(employeeNumber))
            {
                var id = Guid.NewGuid();
                string? tachoName = null;
                string? skills = null;
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO dbo.Drivers (Id, EmployeeNumber, DisplayName, TachoName, MobileNumber, DriverType, DriverGroup, Skills, Active)
                    VALUES ({id}, {employeeNumber}, {displayName}, {tachoName}, {mobileNumber}, {driverType}, {driverGroup}, {skills}, {true})", ct);
                existingNumbers.Add(employeeNumber);
                created++;
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE dbo.Drivers SET DisplayName = {displayName}, MobileNumber = {mobileNumber}, DriverType = {driverType}, DriverGroup = {driverGroup}, Active = {true}
                    WHERE EmployeeNumber = {employeeNumber}", ct);
                updated++;
            }
        }
        var now = DateTimeOffset.UtcNow;
        var activeDriverEmployeeNumbers = candidates
            .Select(employee => ClipRequired(string.IsNullOrWhiteSpace(employee.EmployeeNumber) ? $"SAGE-{employee.Id}" : employee.EmployeeNumber.Trim(), 40))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "sagehrsync",
            IdempotencyKey = $"sagehrsync:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(new { sourceEmployeeCount = employees.Count, driverCandidateCount = candidates.Count, activeDriverEmployeeNumbers, created, updated, skipped }),
            Source = actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase) ? "Sage HR scheduled synchronisation" : "Sage HR manual synchronisation",
            Status = StagingStatus.Promoted,
            ReceivedAtUtc = now,
            ReviewedAtUtc = now,
            ReviewedBy = actor,
            ReviewNote = "Sage HR synchronisation through the shared integration coordinator."
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new("Sage HR", true, now, $"Sage HR synchronised {created + updated} driver records ({created} created, {updated} updated).", created + updated);
    }

    public async Task<IntegrationSyncResult> SyncFleetioAsync(string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.Fleetio, TimeSpan.FromMinutes(45), ct);
        if (lease is null)
            return new("Fleetio", false, DateTimeOffset.UtcNow, "Fleetio sync skipped because another distributed writer currently holds the integration lease.");
        return await SyncFleetioCoreAsync(actor, ct);
    }

    internal async Task<IntegrationSyncResult> SyncFleetioCoreAsync(string actor, CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured)
            return new("Fleetio", false, DateTimeOffset.UtcNow, $"Fleetio is not configured: {string.Join(", ", fleetioClient.MissingSettings)}.");

        var assets = await fleetioClient.GetVehiclesAsync(100, ct);
            var vehicleAssets = assets.Where(asset => !IsTrailer(asset)).ToList();
            var trailerAssets = assets.Where(IsTrailer).ToList();
            var vehicles = await db.Vehicles.ToListAsync(ct);
            var trailers = await db.Trailers.ToListAsync(ct);
            var mappings = await SafeFleetioMappings(ct);
            var matchedVehicleIds = new HashSet<Guid>();
            var matchedTrailerIds = new HashSet<Guid>();
            var createdVehicles = 0;
            var updatedVehicles = 0;
            var createdTrailers = 0;
            var updatedTrailers = 0;
            var quarantinedVehicles = 0;
            var quarantinedTrailers = 0;
            var mergedTrailerAliases = 0;
            var correctedVehicleMappings = 0;
            var duplicateVehicleSourceRows = vehicleAssets
                .Select(BestVehicleRegistration)
                .Where(registration => !string.IsNullOrWhiteSpace(registration))
                .GroupBy(registration => CanonicalVehicleRegistration(registration!), StringComparer.OrdinalIgnoreCase)
                .Sum(group => Math.Max(0, group.Count() - 1));
            var now = DateTimeOffset.UtcNow;

            foreach (var asset in vehicleAssets)
            {
                var registration = BestVehicleRegistration(asset);
                if (string.IsNullOrWhiteSpace(registration)) continue;

                var mappedId = MappingTarget(mappings, asset.Id, "Vehicle");
                var mappedVehicle = mappedId is Guid mappedVehicleId ? vehicles.FirstOrDefault(item => item.Id == mappedVehicleId) : null;
                var registrationVehicle = FindVehicleByRegistration(vehicles, registration);
                var vehicle = ResolveVehicleForFleetioAsset(vehicles, asset, mappedId);

                if (registrationVehicle is not null && mappedVehicle is not null && registrationVehicle.Id != mappedVehicle.Id)
                {
                    correctedVehicleMappings++;
                    logger.LogWarning(
                        "Fleetio vehicle mapping {FleetioId} pointed to TMS vehicle {MappedVehicleId}, but registration {Registration} belongs to {RegistrationVehicleId}; repairing the mapping.",
                        asset.Id, mappedVehicle.Id, registration, registrationVehicle.Id);
                }

                if (vehicle is null)
                {
                    vehicle = new Vehicle { Registration = ClipRequired(CanonicalVehicleRegistration(registration), 20), Active = true };
                    db.Vehicles.Add(vehicle);
                    vehicles.Add(vehicle);
                    createdVehicles++;
                }
                else updatedVehicles++;

                // If this is already the same registration after normalisation, keep the stored
                // formatting. This avoids turning harmless historical spacing variants into a
                // unique-index collision when an equivalent registration row already exists.
                if (!string.Equals(CanonicalVehicleRegistration(vehicle.Registration), CanonicalVehicleRegistration(registration), StringComparison.OrdinalIgnoreCase))
                    vehicle.Registration = ClipRequired(CanonicalVehicleRegistration(registration), 20);

                vehicle.FleetNumber = Clip(asset.FleetNumber, 40) ?? vehicle.FleetNumber;
                vehicle.FleetioId = Clip(asset.Id, 80);
                vehicle.FleetioName = Clip(asset.Name, 160);
                vehicle.FleetioStatus = Clip(asset.Status, 80);
                vehicle.FleetioVor = asset.Vor;
                vehicle.FleetioPmiDueUtc = asset.PmiDueUtc;
                vehicle.FleetioMotDueUtc = asset.MotDueUtc;
                vehicle.FleetioServiceStatus = Clip(asset.ServiceStatus, 160);
                vehicle.FleetioLastSyncedUtc = now;
                vehicle.Active = true;
                matchedVehicleIds.Add(vehicle.Id);
                UpsertMapping(mappings, asset.Id, asset.Name ?? registration, "Vehicle", vehicle.Id, actor);
            }

            foreach (var asset in trailerAssets)
            {
                var fleetioName = asset.Name?.Trim();
                var cNumber = asset.Registration?.Trim();
                var preferred = !string.IsNullOrWhiteSpace(fleetioName) ? fleetioName : cNumber;
                if (string.IsNullOrWhiteSpace(preferred)) continue;

                var mappedId = MappingTarget(mappings, asset.Id, "Trailer");
                var aliases = TrailerKeys(fleetioName, cNumber, preferred).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var matchingTms = trailers.Where(item => TrailerKeys(item.TrailerNumber).Any(aliases.Contains)).ToList();
                var trailer = mappedId is Guid id ? trailers.FirstOrDefault(item => item.Id == id) : null;
                trailer ??= matchingTms.FirstOrDefault(item => Normalise(item.TrailerNumber) == Normalise(preferred));
                trailer ??= matchingTms.FirstOrDefault();

                if (trailer is null)
                {
                    trailer = new Trailer { TrailerNumber = ClipRequired(preferred, 40), Type = Clip(asset.Type, 80), Active = true };
                    db.Trailers.Add(trailer);
                    trailers.Add(trailer);
                    createdTrailers++;
                }
                else updatedTrailers++;

                // Fleetio owns identity/status, but TMS owns capacities. If an old alias contains the
                // capacity, carry it onto the canonical Fleetio trailer before retiring the alias.
                trailer.StandardCapacity ??= matchingTms.Select(item => item.StandardCapacity).FirstOrDefault(value => value is not null);
                trailer.EuroCapacity ??= matchingTms.Select(item => item.EuroCapacity).FirstOrDefault(value => value is not null);
                trailer.TrailerNumber = ClipRequired(preferred, 40);
                trailer.Type = Clip(asset.Type, 80) ?? trailer.Type;
                trailer.Active = true;
                matchedTrailerIds.Add(trailer.Id);
                UpsertMapping(mappings, asset.Id, preferred, "Trailer", trailer.Id, actor);

                foreach (var duplicate in matchingTms.Where(item => item.Id != trailer.Id))
                {
                    await ReassignTrailerLoadsAsync(duplicate.Id, trailer.Id, ct);
                    foreach (var mapping in mappings.Where(item => item.TmsEntityType == "Trailer" && item.TmsEntityId == duplicate.Id))
                        mapping.TmsEntityId = trailer.Id;
                    duplicate.Active = false;
                    mergedTrailerAliases++;
                }
            }

            // Fleetio is authoritative for fleet identity. An active TMS-only asset is quarantined so
            // it cannot be allocated as if it were current fleet. Records are retained for history.
            foreach (var vehicle in vehicles.Where(item => item.Active && !matchedVehicleIds.Contains(item.Id)))
            {
                vehicle.Active = false;
                quarantinedVehicles++;
            }
            foreach (var trailer in trailers.Where(item => item.Active && !matchedTrailerIds.Contains(item.Id)))
            {
                trailer.Active = false;
                quarantinedTrailers++;
            }

            await db.SaveChangesAsync(ct);
            var changed = createdVehicles + updatedVehicles + createdTrailers + updatedTrailers + quarantinedVehicles + quarantinedTrailers + mergedTrailerAliases;
        return new("Fleetio", true, now,
            $"Fleetio canonical sync: {createdVehicles} vehicle(s) created, {updatedVehicles} updated, {createdTrailers} trailer(s) created, {updatedTrailers} updated, {mergedTrailerAliases} trailer alias(es) consolidated, {quarantinedVehicles} TMS-only vehicle(s) and {quarantinedTrailers} TMS-only trailer(s) quarantined, {correctedVehicleMappings} stale vehicle mapping(s) repaired, {duplicateVehicleSourceRows} duplicate source registration row(s) resolved against canonical vehicles. Trailer capacities were retained from TMS.", changed);
    }

    public async Task<IReadOnlyList<IntegrationSyncResult>> ForceAllAsync(string actor, CancellationToken ct)
    {
        var results = new List<IntegrationSyncResult>();
        var syncs = new (string Provider, Func<string, CancellationToken, Task<IntegrationSyncResult>> Sync)[]
        {
            ("TachoMaster", SyncTachoMasterAsync),
            ("Sage HR", SyncSageHrAsync),
            ("Fleetio", SyncFleetioAsync)
        };

        foreach (var (provider, sync) in syncs)
        {
            try { results.Add(await sync(actor, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "{Provider} forced integration sync failed.", provider);
                results.Add(new(provider, false, DateTimeOffset.UtcNow, ex.GetBaseException().Message));
            }
        }
        return results;
    }

    private async Task<List<IntegrationMapping>> SafeFleetioMappings(CancellationToken ct)
    {
        try { return await db.IntegrationMappings.Where(item => item.Provider == "Fleetio" && item.Active).ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fleetio integration mappings are unavailable; canonical sync will use asset identity fallbacks.");
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static Guid? MappingTarget(IEnumerable<IntegrationMapping> mappings, string fleetioId, string entityType) => mappings
        .FirstOrDefault(item => string.Equals(item.ExternalKey, fleetioId, StringComparison.OrdinalIgnoreCase) && string.Equals(item.TmsEntityType, entityType, StringComparison.OrdinalIgnoreCase))?.TmsEntityId;

    private void UpsertMapping(List<IntegrationMapping> mappings, string fleetioId, string label, string entityType, Guid entityId, string actor)
    {
        var mapping = mappings.FirstOrDefault(item => string.Equals(item.ExternalKey, fleetioId, StringComparison.OrdinalIgnoreCase) && string.Equals(item.TmsEntityType, entityType, StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
        {
            mapping = new IntegrationMapping
            {
                Provider = "Fleetio",
                ExternalKey = ClipRequired(fleetioId, 200),
                ExternalLabel = Clip(label, 200),
                TmsEntityType = entityType,
                TmsEntityId = entityId,
                Active = true,
                UpdatedBy = actor
            };
            db.IntegrationMappings.Add(mapping);
            mappings.Add(mapping);
        }
        else
        {
            mapping.ExternalLabel = Clip(label, 200);
            mapping.TmsEntityId = entityId;
            mapping.Active = true;
            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mapping.UpdatedBy = actor;
        }
    }

    private async Task ReassignTrailerLoadsAsync(Guid fromTrailerId, Guid toTrailerId, CancellationToken ct)
    {
        try
        {
            var loads = await db.Loads.Where(load => load.TrailerId == fromTrailerId).ToListAsync(ct);
            foreach (var load in loads) load.TrailerId = toTrailerId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not reassign historical loads from trailer alias {FromTrailerId} to canonical trailer {ToTrailerId}.", fromTrailerId, toTrailerId);
        }
    }

    private bool IsDriver(SageHrEmployee employee) =>
        DriverPopulationRules.IsSageDriver(employee, sageOptions.DriverTeamName, sageOptions.DriverPositionKeyword);

    private static bool IsTrailer(FleetioVehicle asset) =>
        asset.Type?.Contains("Trailer", StringComparison.OrdinalIgnoreCase) == true ||
        (!string.IsNullOrWhiteSpace(asset.Registration) && Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase));

    private static string? BestVehicleRegistration(FleetioVehicle asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.Registration) && !Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase)) return asset.Registration.Trim();
        if (!string.IsNullOrWhiteSpace(asset.Name) && LooksLikeRegistration(asset.Name)) return asset.Name.Trim();
        return null;
    }

    private static bool LooksLikeRegistration(string value)
    {
        var key = Normalise(value);
        return key.Length is >= 5 and <= 8 && key.Any(char.IsLetter) && key.Any(char.IsDigit);
    }

    internal static string CanonicalVehicleRegistration(string? value) => Normalise(value);

    internal static Vehicle? FindVehicleByRegistration(IEnumerable<Vehicle> vehicles, string registration)
    {
        var registrationKey = CanonicalVehicleRegistration(registration);
        if (registrationKey.Length == 0) return null;
        return vehicles.FirstOrDefault(item =>
            string.Equals(CanonicalVehicleRegistration(item.Registration), registrationKey, StringComparison.OrdinalIgnoreCase));
    }

    internal static Vehicle? ResolveVehicleForFleetioAsset(IReadOnlyList<Vehicle> vehicles, FleetioVehicle asset, Guid? mappedId)
    {
        var registration = BestVehicleRegistration(asset);
        if (string.IsNullOrWhiteSpace(registration)) return null;

        // Registration is the database's unique vehicle identity and therefore wins over an old
        // Fleetio mapping. This is the key guard against assigning an existing registration to a
        // second Vehicle row and tripping IX_Vehicles_Registration.
        var registrationVehicle = FindVehicleByRegistration(vehicles, registration);
        if (registrationVehicle is not null) return registrationVehicle;

        if (!string.IsNullOrWhiteSpace(asset.Id))
        {
            var directFleetioVehicle = vehicles.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.FleetioId) &&
                string.Equals(item.FleetioId, asset.Id, StringComparison.OrdinalIgnoreCase));
            if (directFleetioVehicle is not null) return directFleetioVehicle;
        }

        if (mappedId is Guid id)
        {
            var mappedVehicle = vehicles.FirstOrDefault(item => item.Id == id);
            if (mappedVehicle is not null) return mappedVehicle;
        }

        var assetKeys = VehicleKeys(registration, asset.FleetNumber, asset.Name);
        return vehicles.FirstOrDefault(item =>
            VehicleKeys(item.Registration, item.FleetNumber, item.FleetioName)
                .Intersect(assetKeys, StringComparer.OrdinalIgnoreCase)
                .Any());
    }

    private static IReadOnlyList<string> VehicleKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var key = Normalise(value);
            if (key.Length == 0) continue;
            keys.Add(key);
            // Keep the historical trailing-H alias, but do not use the old last-three-character
            // shortcut. Two unrelated registrations can share three characters and must never be
            // merged during an authoritative fleet sync.
            if (key.EndsWith("H", StringComparison.OrdinalIgnoreCase) && key.Length > 4) keys.Add(key[..^1]);
        }
        return keys.ToList();
    }

    private static IReadOnlyList<string> TrailerKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var key = Normalise(value);
            if (key.Length == 0) continue;
            keys.Add(key);
            if (key.StartsWith("SLH", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[3..], out var number)) keys.Add(number.ToString());
            if (int.TryParse(key, out var numeric)) keys.Add($"SLH{numeric}");
        }
        return keys.ToList();
    }

    private static Dictionary<string, TachoDriverProfile> UniqueLookup(IEnumerable<TachoDriverProfile> profiles, Func<TachoDriverProfile, string> keySelector) => profiles
        .Select(profile => (Profile: profile, Key: keySelector(profile)))
        .Where(item => item.Key.Length > 0)
        .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Select(item => item.Profile.MemberCode).Distinct().Count() == 1)
        .ToDictionary(group => group.Key, group => group.First().Profile, StringComparer.OrdinalIgnoreCase);

    internal static bool CardsMatch(string? left, string? right)
    {
        var a = Normalise(left);
        var b = Normalise(right);
        if (a.Length < 8 || b.Length < 8) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormalisePersonName(string? value) => string.Join(' ', (value ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
        .Where(word => word.Length > 0).OrderBy(word => word, StringComparer.Ordinal));
    private static string? Clip(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
    private static string ClipRequired(string value, int maxLength) => value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
}
