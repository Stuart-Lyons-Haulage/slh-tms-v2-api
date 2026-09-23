using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class AssistantSafeFixService(
    TmsDbContext db,
    AzureMapsRouteClient maps,
    ILogger<AssistantSafeFixService> logger)
{
    public async Task<SafeFixResult> Apply(CancellationToken ct)
    {
        var changes = new List<string>();
        var skipped = new List<string>();

        await SafeStep("Vehicles", () => NormaliseVehicleRegistrations(changes, skipped, ct), changes, skipped);
        await SafeStep("Sites", () => RepairSites(changes, skipped, ct), changes, skipped);
        await SafeStep("Geofence site links", () => RepairGeofenceSiteLinks(changes, skipped, ct), changes, skipped);
        await SafeStep("Markets", () => RepairMarkets(changes, skipped, ct), changes, skipped);
        await SafeStep("Customer contacts", () => NormaliseCustomerEmails(changes, ct), changes, skipped);

        try
        {
            if (changes.Count > 0 || skipped.Count > 0)
            {
                db.StagedImports.Add(new StagedImport
                {
                    EntityType = "assistantfix",
                    IdempotencyKey = $"assistantfix:{Guid.NewGuid():N}",
                    PayloadJson = JsonSerializer.Serialize(new { changes, skipped, appliedAtUtc = DateTimeOffset.UtcNow }),
                    Source = "SLH Assistant safe validation fixes",
                    Status = StagingStatus.Promoted,
                    ReviewedAtUtc = DateTimeOffset.UtcNow,
                    ReviewNote = $"Attempted {changes.Count} deterministic validation fixes; {skipped.Count} items left for review."
                });
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant audit write was unavailable after master-data repairs.");
            db.ChangeTracker.Clear();
            skipped.Add($"Audit log unavailable: {ex.GetBaseException().Message}");
        }

        return new SafeFixResult(changes.Count, skipped.Count, changes.Take(150).ToList(), skipped.Take(150).ToList());
    }

    private async Task SafeStep(string area, Func<Task> action, List<string> changes, List<string> skipped)
    {
        var changeStart = changes.Count;
        var skippedStart = skipped.Count;
        try
        {
            await action();
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant skipped unavailable master-data area {Area} and continued with the remaining fixes.", area);
            if (changes.Count > changeStart) changes.RemoveRange(changeStart, changes.Count - changeStart);
            if (skipped.Count > skippedStart) skipped.RemoveRange(skippedStart, skipped.Count - skippedStart);
            skipped.Add($"{area}: repair unavailable - {ex.GetBaseException().Message}");
            db.ChangeTracker.Clear();
        }
    }

    private async Task NormaliseVehicleRegistrations(List<string> changes, List<string> skipped, CancellationToken ct)
    {
        var vehicles = await db.Vehicles.ToListAsync(ct);
        var counts = vehicles.GroupBy(x => TmsAssistantService.NormaliseRegistration(x.Registration)).ToDictionary(x => x.Key, x => x.Count());
        foreach (var vehicle in vehicles.Where(x => x.Active))
        {
            var normalised = TmsAssistantService.NormaliseRegistration(vehicle.Registration);
            if (vehicle.Registration == normalised) continue;
            if (counts[normalised] > 1)
            {
                skipped.Add($"Vehicle {vehicle.Registration}: normalised registration conflicts with another vehicle.");
                continue;
            }
            changes.Add($"Vehicle {vehicle.Registration} normalised to {normalised}.");
            vehicle.Registration = normalised;
        }
    }

    private async Task RepairSites(List<string> changes, List<string> skipped, CancellationToken ct)
    {
        await GeofenceRuntimeRepair.EnsureAsync(db, ct);
        var sites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        foreach (var site in sites.Where(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && string.IsNullOrWhiteSpace(x.MapLink)))
        {
            site.MapLink = $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(site.CollectionAddress!.Trim())}";
            await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode, JsonSerializer.Serialize(site), "SLH Assistant map-link repair", null, ct);
            changes.Add($"Created a driver map link for {site.Name}.");
        }
        var geocoded = 0;
        foreach (var site in sites.Where(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && (x.Latitude is null || x.Longitude is null)))
        {
            if (geocoded >= 20) { skipped.Add($"Site {site.Name}: map point left for the next Assistant run (20-site safety limit)."); continue; }
            try
            {
                var coordinate = await maps.SearchCoordinate(site.CollectionAddress!, ct);
                if (coordinate is null) { skipped.Add($"Site {site.Name}: Azure Maps could not confidently locate this address."); continue; }
                site.Latitude = coordinate.Value.Latitude;
                site.Longitude = coordinate.Value.Longitude;
                await MasterDetailStore.SaveAsync(db, "site", site.ExternalCode, JsonSerializer.Serialize(site), "SLH Assistant Azure Maps validation", null, ct);
                changes.Add($"Added map point {site.Latitude:F6}, {site.Longitude:F6} for {site.Name}.");
                geocoded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Assistant could not geocode site {SiteCode}.", site.ExternalCode);
                skipped.Add($"Site {site.Name}: map point lookup failed and no coordinate was changed.");
            }
        }
        var safeGroups = FindSafeSiteDuplicateGroups(sites);
        foreach (var group in safeGroups)
        {
            var ordered = group.OrderByDescending(Completeness).ThenBy(x => x.ExternalCode, StringComparer.OrdinalIgnoreCase).ToList();
            var canonical = ordered[0];
            var duplicates = ordered.Skip(1).ToList();
            canonical.MapLink ??= duplicates.Select(x => x.MapLink).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            canonical.CollectionInstructions ??= duplicates.Select(x => x.CollectionInstructions).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            canonical.DriverTextName ??= duplicates.Select(x => x.DriverTextName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            canonical.CollectionAddress ??= duplicates.Select(x => x.CollectionAddress).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            canonical.Latitude ??= duplicates.Select(x => x.Latitude).FirstOrDefault(x => x is not null);
            canonical.Longitude ??= duplicates.Select(x => x.Longitude).FirstOrDefault(x => x is not null);
            canonical.Aliases = MergeAliases(canonical, duplicates);
            foreach (var duplicate in duplicates)
            {
                var geofences = await db.SiteGeofences.Where(x => x.SiteId == duplicate.Id).ToListAsync(ct);
                foreach (var geofence in geofences) geofence.SiteId = canonical.Id;
                var mappings = await db.IntegrationMappings.Where(x => x.TmsEntityType == "Site" && x.TmsEntityId == duplicate.Id).ToListAsync(ct);
                foreach (var mapping in mappings) { mapping.TmsEntityId = canonical.Id; mapping.UpdatedAtUtc = DateTimeOffset.UtcNow; mapping.UpdatedBy = "SLH Assistant"; }
                duplicate.Active = false;
                await MasterDetailStore.SaveAsync(db, "site", duplicate.ExternalCode, JsonSerializer.Serialize(duplicate), "SLH Assistant duplicate consolidation", null, ct);
            }
            await MasterDetailStore.SaveAsync(db, "site", canonical.ExternalCode, JsonSerializer.Serialize(canonical), "SLH Assistant duplicate consolidation", null, ct);
            db.MasterDataAudits.Add(new MasterDataAudit { EntityType = "Site", EntityId = canonical.Id, Action = "AssistantDuplicateMerge", ChangedBy = "SLH Assistant", ChangesJson = JsonSerializer.Serialize(new { canonical = canonical.ExternalCode, merged = duplicates.Select(x => x.ExternalCode).ToList() }) });
            changes.Add($"Consolidated {duplicates.Count} safe duplicate site record{(duplicates.Count == 1 ? "" : "s")} into {canonical.Name} ({canonical.ExternalCode}); linked geofences and integration mappings were retained.");
        }
        var remainingLikely = FindLikelySiteDuplicateGroups(sites.Where(x => x.Active).ToList()).Where(group => !safeGroups.Any(safe => safe.Select(x => x.Id).OrderBy(x => x).SequenceEqual(group.Select(x => x.Id).OrderBy(x => x)))).Take(20);
        foreach (var group in remainingLikely) skipped.Add($"Possible site duplicate left for review: {string.Join(" / ", group.Select(x => $"{x.Name} ({x.ExternalCode})"))}.");
    }

    private async Task RepairGeofenceSiteLinks(List<string> changes, List<string> skipped, CancellationToken ct)
    {
        var result = await SiteGeofenceMasterSync.SyncAsync(db, ct);
        if (result.SitesCoded > 0) changes.Add($"Canonicalised {result.SitesCoded} Site Master code{(result.SitesCoded == 1 ? "" : "s")} to the SITE### geofence format.");
        if (result.GeofencesLinked > 0) changes.Add($"Synced {result.GeofencesLinked} geofence-to-site link{(result.GeofencesLinked == 1 ? "" : "s")}.");
        if (result.GeofencesCanonicalized > 0) changes.Add($"Updated {result.GeofencesCanonicalized} geofence site code{(result.GeofencesCanonicalized == 1 ? "" : "s")} to the canonical Site Master code.");
        if (result.GeofencesUnlinked > 0) skipped.Add($"{result.GeofencesUnlinked} stale geofence link{(result.GeofencesUnlinked == 1 ? " was" : "s were")} cleared for manual review.");
        if (result.SitesMissingGeofence > 0) skipped.Add($"{result.SitesMissingGeofence} active site{(result.SitesMissingGeofence == 1 ? " is" : "s are")} still missing a confirmed geofence link.");
    }

    private async Task RepairMarkets(List<string> changes, List<string> skipped, CancellationToken ct)
    {
        var contacts = await db.MarketContacts.Where(x => x.Active).ToListAsync(ct);
        var identityGroups = contacts
            .Select(contact => new
            {
                Contact = contact,
                Market = CanonicalMarket(contact.Market),
                Name = Clean(contact.Name) ?? contact.Name,
                Stand = Clean(contact.StandOrLocation) ?? InferStand(contact.Name)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Market) && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => $"{Normalise(item.Market)}|{Normalise(item.Name)}|{Normalise(item.Stand)}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in identityGroups)
        {
            var ordered = group
                .OrderByDescending(item => string.Equals(item.Contact.Market, item.Market, StringComparison.OrdinalIgnoreCase) && string.Equals(item.Contact.Name, item.Name, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => MarketCompleteness(item.Contact))
                .ThenBy(item => item.Contact.Id)
                .ToList();
            var canonicalItem = ordered[0];
            var canonical = canonicalItem.Contact;
            var originalMarket = canonical.Market;
            var originalName = canonical.Name;
            canonical.Market = canonicalItem.Market;
            canonical.Name = canonicalItem.Name;
            canonical.StandOrLocation = Clean(canonical.StandOrLocation) ?? InferStand(canonical.Name);
            canonical.Salesman = Clean(canonical.Salesman);
            canonical.Sender = Clean(canonical.Sender);
            if (!string.Equals(originalMarket, canonical.Market, StringComparison.Ordinal) || !string.Equals(originalName, canonical.Name, StringComparison.Ordinal)) changes.Add($"Normalised market record {originalName} to {canonical.Market} / {canonical.Name}.");

            var duplicates = ordered.Skip(1).Select(item => item.Contact).ToList();
            if (duplicates.Count == 0) continue;
            canonical.StandOrLocation ??= duplicates.Select(x => Clean(x.StandOrLocation) ?? InferStand(x.Name)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            canonical.Salesman ??= duplicates.Select(x => Clean(x.Salesman)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            canonical.Sender ??= duplicates.Select(x => Clean(x.Sender)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            foreach (var duplicate in duplicates)
            {
                duplicate.StandOrLocation = Clean(duplicate.StandOrLocation);
                duplicate.Salesman = Clean(duplicate.Salesman);
                duplicate.Sender = Clean(duplicate.Sender);
                duplicate.Active = false;
            }
            changes.Add($"Consolidated {duplicates.Count} duplicate market record{(duplicates.Count == 1 ? "" : "s")} for {canonical.Market} / {canonical.Name} / {canonical.StandOrLocation ?? "no stand"}.");
        }

        foreach (var contact in contacts.Where(x => x.Active && !identityGroups.SelectMany(group => group).Any(item => item.Contact.Id == x.Id)))
        {
            var originalMarket = contact.Market;
            var originalName = contact.Name;
            contact.Market = CanonicalMarket(contact.Market);
            contact.Name = Clean(contact.Name) ?? contact.Name;
            contact.StandOrLocation = Clean(contact.StandOrLocation) ?? InferStand(contact.Name);
            contact.Salesman = Clean(contact.Salesman);
            contact.Sender = Clean(contact.Sender);
            if (!string.Equals(originalMarket, contact.Market, StringComparison.Ordinal) || !string.Equals(originalName, contact.Name, StringComparison.Ordinal)) changes.Add($"Normalised market record {originalName} to {contact.Market} / {contact.Name}.");
        }
        foreach (var contact in contacts.Where(x => x.Active && (string.IsNullOrWhiteSpace(x.Market) || string.IsNullOrWhiteSpace(x.Name)))) skipped.Add($"Market record {contact.Id}: market/name is missing and requires manual review.");
        foreach (var contact in contacts.Where(x => x.Active && !string.Equals(x.Market, "Sender", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(x.StandOrLocation)).Take(30)) skipped.Add($"Market {contact.Market} / {contact.Name}: stand/location still needs enrichment.");
    }

    private async Task NormaliseCustomerEmails(List<string> changes, CancellationToken ct)
    {
        var contacts = await db.CustomerContacts.Where(x => x.Active && x.Email != null).ToListAsync(ct);
        foreach (var contact in contacts) { var normalised = contact.Email!.Trim().ToLowerInvariant(); if (normalised == contact.Email) continue; contact.Email = normalised; changes.Add($"Normalised the ETA email for {contact.CustomerCode} / {contact.Name}."); }
    }

    private static List<List<Site>> FindSafeSiteDuplicateGroups(IReadOnlyCollection<Site> sites)
    {
        var groups = new Dictionary<string, HashSet<Site>>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            var name = Normalise(site.Name); var address = NormaliseAddress(site.CollectionAddress); var postcode = ExtractPostcode(site.CollectionAddress); var keys = new List<string>();
            if (name.Length >= 5 && address.Length >= 8) keys.Add($"nameaddress:{name}|{address}"); if (name.Length >= 5 && !string.IsNullOrWhiteSpace(postcode)) keys.Add($"namepostcode:{name}|{postcode}");
            foreach (var key in keys) { if (!groups.TryGetValue(key, out var set)) groups[key] = set = []; set.Add(site); }
        }
        return DistinctGroups(groups.Values.Where(x => x.Count > 1));
    }
    private static List<List<Site>> FindLikelySiteDuplicateGroups(IReadOnlyCollection<Site> sites)
    {
        var groups = new Dictionary<string, HashSet<Site>>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites) { var name = Normalise(site.Name); var address = NormaliseAddress(site.CollectionAddress); foreach (var key in new[] { name.Length >= 5 ? $"name:{name}" : "", address.Length >= 8 ? $"address:{address}" : "" }.Where(x => x.Length > 0)) { if (!groups.TryGetValue(key, out var set)) groups[key] = set = []; set.Add(site); } }
        return DistinctGroups(groups.Values.Where(x => x.Count > 1));
    }
    private static List<List<Site>> DistinctGroups(IEnumerable<HashSet<Site>> source) { var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); return source.Select(group => group.OrderBy(x => x.ExternalCode, StringComparer.OrdinalIgnoreCase).ToList()).Where(group => seen.Add(string.Join('|', group.Select(x => x.Id).OrderBy(x => x)))).ToList(); }
    private static int Completeness(Site site) => new object?[] { site.MapLink, site.CollectionInstructions, site.DriverTextName, site.CollectionAddress, site.Latitude, site.Longitude, site.Aliases }.Count(x => x is not null && !string.IsNullOrWhiteSpace(x.ToString()));
    private static int MarketCompleteness(MarketContact contact) => new[] { contact.StandOrLocation, contact.Salesman, contact.Sender }.Count(x => !string.IsNullOrWhiteSpace(x));
    private static string MergeAliases(Site canonical, IReadOnlyCollection<Site> duplicates) => string.Join(", ", new[] { canonical.Name, canonical.DriverTextName, canonical.Aliases }.Concat(duplicates.SelectMany(x => new[] { x.Name, x.DriverTextName, x.Aliases, x.ExternalCode })).Where(x => !string.IsNullOrWhiteSpace(x)).SelectMany(x => x!.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase));
    private static string CanonicalMarket(string? value) { var clean = Clean(value) ?? "General"; var normal = Normalise(clean); if (normal.Contains("covent")) return "Covent"; if (normal.Contains("spit")) return "Spitalfields"; if (normal.Contains("western")) return "Western"; if (normal.Contains("sender")) return "Sender"; return clean; }
    private static string? InferStand(string? name) { if (string.IsNullOrWhiteSpace(name)) return null; var bracket = Regex.Match(name, @"\(([^)]+)\)\s*$", RegexOptions.IgnoreCase); if (bracket.Success) return bracket.Groups[1].Value.Trim(); var labelled = Regex.Match(name, @"\b(?:stall|stand)\s*#?\s*([a-z]?\d{1,4}[a-z]?)\s*$", RegexOptions.IgnoreCase); return labelled.Success ? labelled.Groups[1].Value.Trim() : null; }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value.Trim(), @"\s+", " ");
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string NormaliseAddress(string? value) => Normalise(Regex.Replace(value ?? string.Empty, @"\b(road|rd|street|st|avenue|ave|lane|ln|drive|dr)\b", string.Empty, RegexOptions.IgnoreCase));
    private static string? ExtractPostcode(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; var match = Regex.Match(value.ToUpperInvariant(), @"\b([A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\b"); return match.Success ? Regex.Replace(match.Groups[1].Value, @"\s+", string.Empty) : null; }
}