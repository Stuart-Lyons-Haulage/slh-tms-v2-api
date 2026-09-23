using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record DriverMasterClassificationResult(
    int ActiveDrivers,
    int TypeNormalised,
    int GroupNormalised,
    int AgencyGroupsApplied,
    int EmailsRetained,
    int LegacyNorthPreloadFieldsRemoved,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Applies the controlled SLH Driver Master taxonomy after TachoMaster/Sage enrichment.
/// TachoMaster owns driver identity; this service owns the operational CRM classification:
/// Type is Employed/Casual/Agency, agency drivers use the agency name as Group, and legacy
/// North/Preload fields are removed from the audited master-detail payload.
/// </summary>
public sealed class DriverMasterClassificationService(TmsDbContext db, ILogger<DriverMasterClassificationService> logger)
{
    private const string DetailType = "masterdetail:driver";
    private const string ProfileType = "tachodriverprofile";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<DriverMasterClassificationResult> ApplyAsync(string actor, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Historical syncs can have left more than one StagedImport row with the same Tacho
        // profile idempotency key. The canonical sync materialises those rows as a dictionary,
        // so duplicate keys can abort the whole Driver Master cleanse before any driver is merged.
        // Retain the newest row at the canonical key and archive/re-key older copies for audit.
        await RepairDuplicateProfileKeysAsync(actor, now, ct);

        var drivers = await db.Drivers.Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        var profileRows = await db.StagedImports
            .Where(row => row.EntityType == ProfileType && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .ToListAsync(ct);
        var profiles = new Dictionary<string, TachoLiveWorker>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in profileRows)
        {
            try
            {
                var profile = JsonSerializer.Deserialize<TachoLiveWorker>(row.PayloadJson, JsonOptions);
                if (profile is null || profile.MemberCode <= 0) continue;
                profiles.TryAdd(profile.MemberCode.ToString(System.Globalization.CultureInfo.InvariantCulture), profile);
            }
            catch (JsonException) { }
        }

        var detailRows = await db.StagedImports
            .Where(row => row.EntityType == DetailType)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .ToListAsync(ct);
        var details = detailRows
            .GroupBy(row => NormaliseKey(row.IdempotencyKey.StartsWith($"{DetailType}:", StringComparison.OrdinalIgnoreCase)
                ? row.IdempotencyKey[(DetailType.Length + 1)..]
                : row.IdempotencyKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var typeNormalised = 0;
        var groupNormalised = 0;
        var agencyGroupsApplied = 0;
        var emailsRetained = 0;
        var legacyFieldsRemoved = 0;

        foreach (var driver in drivers)
        {
            profiles.TryGetValue((driver.TachoMasterDriverId ?? string.Empty).Trim(), out var profile);
            var agency = Meaningful(profile?.AgencyName) ?? Meaningful(driver.AgencyName);
            var beforeType = driver.DriverType;
            var beforeGroup = driver.DriverGroup;
            var canonicalType = CanonicalType(driver.DriverType, profile?.WorkerType, agency);
            var canonicalGroup = CanonicalGroup(driver.DriverGroup, profile?.WorkerType, canonicalType, agency);

            if (!string.Equals(beforeType, canonicalType, StringComparison.Ordinal))
            {
                driver.DriverType = canonicalType;
                typeNormalised++;
            }
            if (!string.Equals(beforeGroup, canonicalGroup, StringComparison.Ordinal))
            {
                driver.DriverGroup = canonicalGroup;
                groupNormalised++;
            }
            if (canonicalType == "Agency" && !string.IsNullOrWhiteSpace(agency) && string.Equals(canonicalGroup, agency, StringComparison.Ordinal))
                agencyGroupsApplied++;

            driver.AgencyName = agency;
            var detailKey = NormaliseKey(driver.EmployeeNumber);
            details.TryGetValue(detailKey, out var detail);
            var payload = ParseObject(detail?.PayloadJson);
            var existingEmail = Text(payload, "email");
            var email = Meaningful(profile?.Email) ?? existingEmail;
            if (!string.IsNullOrWhiteSpace(email)) emailsRetained++;

            var removedForDriver = 0;
            if (RemoveProperty(payload, "northEligible")) removedForDriver++;
            if (RemoveProperty(payload, "preloadEligible")) removedForDriver++;
            legacyFieldsRemoved += removedForDriver;

            payload["employeeNumber"] = driver.EmployeeNumber;
            payload["displayName"] = driver.DisplayName;
            payload["tachoName"] = driver.TachoName;
            payload["mobileNumber"] = driver.MobileNumber;
            payload["driverType"] = canonicalType;
            payload["driverGroup"] = canonicalGroup;
            payload["skills"] = driver.Skills;
            payload["coding"] = driver.Coding;
            payload["agencyName"] = agency;
            payload["email"] = email;
            payload["notes"] = driver.Notes;
            payload["tachoMasterDriverId"] = driver.TachoMasterDriverId;
            payload["tachoCardNumber"] = driver.TachoCardNumber;
            payload["drivingLicenceNumber"] = driver.DrivingLicenceNumber;
            payload["licenceExpiry"] = driver.LicenceExpiry?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            payload["licenceStatus"] = driver.LicenceStatus;
            payload["lastTachoSyncUtc"] = driver.LastTachoSyncUtc?.ToString("O");

            if (detail is null)
            {
                detail = new StagedImport
                {
                    EntityType = DetailType,
                    IdempotencyKey = $"{DetailType}:{detailKey}",
                    PayloadJson = "{}",
                    Source = "Canonical Driver Master classification",
                    Status = StagingStatus.Promoted,
                    ReceivedAtUtc = now
                };
                db.StagedImports.Add(detail);
                details[detailKey] = detail;
            }

            detail.PayloadJson = payload.ToJsonString(JsonOptions);
            detail.Status = StagingStatus.Promoted;
            detail.ReviewedAtUtc = now;
            detail.ReviewedBy = actor;
            detail.ReviewNote = "Canonical Driver Master: Type is Employed/Casual/Agency; agency Group is agency name; Day/Tramper groups normalised; email retained when supplied; North/Preload removed.";

            if (!string.Equals(beforeType, canonicalType, StringComparison.Ordinal) ||
                !string.Equals(beforeGroup, canonicalGroup, StringComparison.Ordinal) ||
                removedForDriver > 0)
            {
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Driver",
                    EntityId = driver.Id,
                    Action = "CanonicalClassification",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        beforeType,
                        afterType = canonicalType,
                        beforeGroup,
                        afterGroup = canonicalGroup,
                        agency,
                        emailPresent = !string.IsNullOrWhiteSpace(email),
                        legacyNorthPreloadFieldsRemoved = removedForDriver
                    }, JsonOptions)
                });
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Driver Master classification completed: {Drivers} active, {Types} types, {Groups} groups, {AgencyGroups} agency groups, {Emails} emails, {LegacyFields} legacy fields removed.",
            drivers.Count, typeNormalised, groupNormalised, agencyGroupsApplied, emailsRetained, legacyFieldsRemoved);

        return new DriverMasterClassificationResult(drivers.Count, typeNormalised, groupNormalised, agencyGroupsApplied, emailsRetained, legacyFieldsRemoved, now);
    }

    private async Task RepairDuplicateProfileKeysAsync(string actor, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.StagedImports
            .Where(row => row.EntityType == ProfileType)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .ThenByDescending(row => row.ReceivedAtUtc)
            .ToListAsync(ct);

        var retired = 0;
        foreach (var group in rows.GroupBy(row => row.IdempotencyKey, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var keep = group.First();
            keep.Status = StagingStatus.Promoted;
            foreach (var duplicate in group.Skip(1))
            {
                duplicate.Status = StagingStatus.Archived;
                duplicate.IdempotencyKey = $"{group.Key}:retired:{duplicate.Id:N}";
                duplicate.ReviewedAtUtc = now;
                duplicate.ReviewedBy = actor;
                duplicate.ReviewNote = $"Duplicate historical Tacho driver profile retired; canonical row {keep.Id} retained at {group.Key}.";
                retired++;
            }
        }

        if (retired == 0) return;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Retired/re-keyed {Count} duplicate historical Tacho driver profile staging row(s) before canonical Driver Master sync.", retired);
    }

    internal static string CanonicalType(string? currentType, string? workerType, string? agency)
    {
        if (!string.IsNullOrWhiteSpace(Meaningful(agency))) return "Agency";
        var sources = $"{currentType} {workerType}";
        if (sources.Contains("agency", StringComparison.OrdinalIgnoreCase)) return "Agency";
        if (sources.Contains("casual", StringComparison.OrdinalIgnoreCase) ||
            sources.Contains("zero hour", StringComparison.OrdinalIgnoreCase) ||
            sources.Contains("zero-hour", StringComparison.OrdinalIgnoreCase)) return "Casual";
        return "Employed";
    }

    internal static string? CanonicalGroup(string? currentGroup, string? workerType, string canonicalType, string? agency)
    {
        if (canonicalType == "Agency") return Meaningful(agency) ?? Meaningful(currentGroup) ?? "Agency";

        var candidate = Meaningful(currentGroup) ?? Meaningful(workerType);
        if (candidate is null) return null;
        if (candidate.Contains("tramp", StringComparison.OrdinalIgnoreCase)) return "Trampers";
        if (candidate.Contains("day", StringComparison.OrdinalIgnoreCase)) return "Day Drivers";
        if (candidate.Contains("night", StringComparison.OrdinalIgnoreCase)) return "Night Drivers";
        return candidate.Trim();
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string? Text(JsonObject payload, string name)
    {
        var property = payload.FirstOrDefault(item => NormaliseKey(item.Key) == NormaliseKey(name));
        return property.Value is JsonValue value && value.TryGetValue<string>(out var text) ? Meaningful(text) : null;
    }

    private static bool RemoveProperty(JsonObject payload, string name)
    {
        var key = payload.Select(item => item.Key).FirstOrDefault(key => NormaliseKey(key) == NormaliseKey(name));
        return key is not null && payload.Remove(key);
    }

    private static string? Meaningful(string? value)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean)) return null;
        if (clean.Equals("none", StringComparison.OrdinalIgnoreCase) || clean.Equals("n/a", StringComparison.OrdinalIgnoreCase) || clean == "-") return null;
        return clean;
    }

    private static string NormaliseKey(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed class DriverMasterClassificationBackgroundService(
    IServiceProvider services,
    ILogger<DriverMasterClassificationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<DriverMasterClassificationService>();
                await service.ApplyAsync("system:driver-master-classification", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Driver Master classification pass failed; the next scheduled pass will retry.");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
}