using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record SiteMasterConsolidationResult(
    int PromotedCustomers,
    int ArchivedDuplicates,
    int LinkedGeofences,
    int CanonicalizedGeofences,
    int NeedsReview);

/// <summary>
/// Conservatively aligns the legacy customer/location register, Site Master and geofences.
/// Site is the canonical physical-location identity. Ambiguous matches are never guessed:
/// they are written to the staging register for human review.
/// </summary>
public static class SiteMasterConsolidation
{
    private const string LocationOnly = "LOCATION_ONLY";
    private const string SiteReviewType = "masterdata:site-review";
    private const string GeofenceReviewType = "masterdata:geofence-review";
    private const string MergedDuplicateAction = "MergedDuplicate";

    public static async Task<SiteMasterConsolidationResult> ReconcileAsync(
        TmsDbContext db,
        string source,
        CancellationToken ct)
    {
        var promotedCustomers = 0;
        var archivedDuplicates = 0;
        var linkedGeofences = 0;
        var canonicalizedGeofences = 0;
        var reviewKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Reconciliation can touch hundreds of Site aliases and geofence review rows.
        // Load the small, relevant staging subset once so the per-record helpers use
        // EF's tracked Local set instead of issuing one SQL query per alias/review.
        await db.StagedImports
            .Where(x => x.EntityType == "masterdetail:site" ||
                        x.EntityType == SiteReviewType ||
                        x.EntityType == GeofenceReviewType)
            .LoadAsync(ct);

        var staleReviews = db.StagedImports.Local
            .Where(x => (x.EntityType == SiteReviewType || x.EntityType == GeofenceReviewType) &&
                        x.Status == StagingStatus.PendingReview)
            .ToList();
        foreach (var review in staleReviews)
        {
            review.Status = StagingStatus.Archived;
            review.ReviewedAtUtc = DateTimeOffset.UtcNow;
            review.ReviewNote = "Superseded by a fresh Site Master reconciliation pass.";
        }
        if (staleReviews.Count > 0)
            await db.SaveChangesAsync(ct);

        // 1. Remove only high-confidence duplicate Sites. Aliases live in the audited
        // master-detail register, so enrich Sites before comparing identities. A duplicate
        // group is auto-merged only when exactly one member owns an active geofence.
        // One Site's primary Name must exactly match another Site's Name/driver name/alias;
        // shared alias-to-alias text alone is deliberately not enough to merge Sites.
        var activeSites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, activeSites, ct);

        var activeFences = await db.SiteGeofences.Where(x => x.Active).ToListAsync(ct);
        foreach (var rows in DuplicateGroups(activeSites))
        {
            var fenceBacked = rows
                .Where(site => activeFences.Any(fence => fence.SiteId == site.Id))
                .ToList();
            var noGeofencesAtAll = fenceBacked.Count == 0;

            // Auto-merge when exactly one member owns an active geofence (clear canonical).
            // Also auto-merge when no member has any geofence AND all members share the same
            // ExternalCode or have identical normalised names — broken-import duplicates that
            // are safe to collapse without geofence evidence.
            var shouldAutoMerge = fenceBacked.Count == 1 ||
                (noGeofencesAtAll && IsSafeNoGeofenceSiteDuplicate(rows));

            if (shouldAutoMerge)
            {
                var canonical = fenceBacked.Count == 1
                    ? fenceBacked[0]
                    : rows.OrderByDescending(site => SiteCompleteness(site)).ThenBy(site => site.ExternalCode, StringComparer.OrdinalIgnoreCase).First();

                foreach (var duplicate in rows.Where(x => x.Id != canonical.Id))
                {
                    await AddSiteAliasesAsync(
                        db,
                        canonical,
                        SiteNames(duplicate).Append(duplicate.ExternalCode),
                        source,
                        ct);

                    // Preserve address/routing data from duplicate before archiving it
                    canonical.CollectionAddress ??= duplicate.CollectionAddress;
                    canonical.Latitude ??= duplicate.Latitude;
                    canonical.Longitude ??= duplicate.Longitude;
                    canonical.MapLink ??= duplicate.MapLink;
                    canonical.CollectionInstructions ??= duplicate.CollectionInstructions;
                    canonical.DriverTextName ??= duplicate.DriverTextName;

                    // Reassign any geofences from the duplicate to the canonical
                    var duplicateFences = activeFences.Where(fence => fence.SiteId == duplicate.Id).ToList();
                    foreach (var fence in duplicateFences)
                    {
                        fence.SiteId = canonical.Id;
                        fence.SiteNumber = canonical.ExternalCode;
                        fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    }

                    duplicate.Active = false;
                    db.MasterDataAudits.Add(new MasterDataAudit
                    {
                        EntityType = "Site",
                        EntityId = duplicate.Id,
                        Action = MergedDuplicateAction,
                        ChangedBy = source,
                        ChangesJson = JsonSerializer.Serialize(new
                        {
                            canonicalSiteId = canonical.Id,
                            canonicalSiteCode = canonical.ExternalCode,
                            canonicalSiteName = canonical.Name,
                            mergedSiteId = duplicate.Id,
                            mergedSiteCode = duplicate.ExternalCode,
                            mergedSiteName = duplicate.Name,
                            geofencesReassigned = duplicateFences.Count,
                            mergeReason = fenceBacked.Count == 1 ? "single-geofence-backed" : "safe-no-geofence-duplicate"
                        })
                    });
                    archivedDuplicates++;
                }
            }
            else
            {
                var reviewSuffix = string.Join(
                    "-",
                    rows.Select(x => x.Id.ToString("N")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                await FlagReviewAsync(db, SiteReviewType, $"duplicate:{reviewSuffix}", new
                {
                    reason = "duplicate_site_identity_ambiguous",
                    identityNames = rows.SelectMany(SiteNames)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    candidates = rows.Select(x => new { x.Id, x.ExternalCode, x.Name, x.DriverTextName, x.Aliases }).ToArray(),
                    geofenceBackedSiteIds = fenceBacked.Select(x => x.Id).ToArray()
                }, source, reviewKeys, ct);
            }
        }
        await db.SaveChangesAsync(ct);

        // 2. Fold the legacy Customer/location register into Site Master without deleting
        // Customer rows, because historical/order commercial references still use CustomerCode.
        // All Sites are considered for code ownership so an archived Site can never be duplicated.
        // Explicit operator archives and duplicate merges are authoritative: reconciliation must
        // not silently reactivate them. A later explicit Restore audit removes that protection.
        var allSites = await db.Sites.ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, allSites, ct);

        activeSites = allSites.Where(x => x.Active).ToList();
        var archivedDispositions = await LoadArchivedSiteDispositionsAsync(db, ct);
        var customers = await db.Customers.Where(x => x.Active).ToListAsync(ct);
        foreach (var customer in customers)
        {
            var codeKey = Normalize(customer.Code);
            var nameKey = Normalize(customer.Name);
            if (codeKey.Length == 0 || nameKey.Length == 0)
                continue;

            var codeMatches = allSites.Where(x => Normalize(x.ExternalCode) == codeKey).ToList();
            if (codeMatches.Count == 1)
            {
                var codeMatch = codeMatches[0];
                if (!codeMatch.Active)
                {
                    if (archivedDispositions.TryGetValue(codeMatch.Id, out var canonicalSiteId))
                    {
                        var canonicalMatches = canonicalSiteId is Guid canonicalId
                            ? activeSites.Where(x => x.Id == canonicalId).ToList()
                            : activeSites.Where(site => SiteNames(site).Any(name => Normalize(name) == nameKey)).ToList();

                        canonicalMatches = canonicalMatches
                            .GroupBy(x => x.Id)
                            .Select(x => x.First())
                            .ToList();

                        if (canonicalMatches.Count == 1)
                        {
                            await AddSiteAliasesAsync(
                                db,
                                canonicalMatches[0],
                                new[] { customer.Code, customer.Name, codeMatch.ExternalCode, codeMatch.Name, codeMatch.DriverTextName },
                                source,
                                ct);
                            continue;
                        }

                        await FlagReviewAsync(db, SiteReviewType, $"archived-code:{codeKey}", new
                        {
                            reason = "customer_code_owned_by_intentionally_archived_site",
                            customer = new { customer.Id, customer.Code, customer.Name },
                            archivedSite = new { codeMatch.Id, codeMatch.ExternalCode, codeMatch.Name, codeMatch.DriverTextName },
                            canonicalSiteId,
                            candidates = canonicalMatches.Select(x => new { x.Id, x.ExternalCode, x.Name }).ToArray(),
                            requestedAction = "Choose the active canonical Site or explicitly restore the archived Site."
                        }, source, reviewKeys, ct);
                        continue;
                    }

                    if (Normalize(codeMatch.Name) != nameKey && Normalize(codeMatch.DriverTextName) != nameKey)
                    {
                        await FlagReviewAsync(db, SiteReviewType, $"archived-code:{codeKey}", new
                        {
                            reason = "customer_code_owned_by_different_archived_site",
                            customer = new { customer.Id, customer.Code, customer.Name },
                            archivedSite = new { codeMatch.Id, codeMatch.ExternalCode, codeMatch.Name, codeMatch.DriverTextName },
                            requestedAction = "Confirm whether the archived Site should be restored or the Customer/location needs a different Site code."
                        }, source, reviewKeys, ct);
                        continue;
                    }

                    codeMatch.Active = true;
                    activeSites.Add(codeMatch);
                }

                await AddSiteAliasesAsync(db, codeMatch, new[] { customer.Code, customer.Name }, source, ct);
                continue;
            }
            if (codeMatches.Count > 1)
            {
                await FlagReviewAsync(db, SiteReviewType, $"customer-code:{codeKey}", new
                {
                    reason = "customer_code_matches_multiple_sites",
                    customer = new { customer.Id, customer.Code, customer.Name },
                    candidates = codeMatches.Select(x => new { x.Id, x.ExternalCode, x.Name, x.Active }).ToArray()
                }, source, reviewKeys, ct);
                continue;
            }

            var nameMatches = activeSites
                .Where(site => SiteNames(site).Any(name => Normalize(name) == nameKey))
                .GroupBy(x => x.Id)
                .Select(x => x.First())
                .ToList();
            if (nameMatches.Count == 1)
            {
                await AddSiteAliasesAsync(db, nameMatches[0], new[] { customer.Code, customer.Name }, source, ct);
                continue;
            }
            if (nameMatches.Count > 1)
            {
                await FlagReviewAsync(db, SiteReviewType, $"customer-name:{nameKey}", new
                {
                    reason = "customer_location_matches_multiple_sites",
                    customer = new { customer.Id, customer.Code, customer.Name },
                    candidates = nameMatches.Select(x => new { x.Id, x.ExternalCode, x.Name }).ToArray()
                }, source, reviewKeys, ct);
                continue;
            }

            var site = new Site
            {
                ExternalCode = customer.Code.Trim(),
                Name = customer.Name.Trim(),
                DriverTextName = customer.Name.Trim(),
                Active = true
            };
            db.Sites.Add(site);
            allSites.Add(site);
            activeSites.Add(site);
            promotedCustomers++;
            await AddSiteAliasesAsync(db, site, new[] { customer.Code, customer.Name }, source, ct);
        }
        await db.SaveChangesAsync(ct);

        // 3. Sync every active geofence to the canonical Site identity. SiteId is authoritative;
        // SiteNumber is corrected to the canonical Site code. Unlinked fences use unique code/name/
        // alias matches only. Anything else is flagged for review.
        activeSites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, activeSites, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            activeSites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        }

        var fences = await db.SiteGeofences.Where(x => x.Active).ToListAsync(ct);
        var sitesById = activeSites.ToDictionary(x => x.Id);
        foreach (var fence in fences)
        {
            if (string.Equals(fence.SiteNumber?.Trim(), LocationOnly, StringComparison.OrdinalIgnoreCase))
                continue;

            if (fence.SiteId is Guid linkedId && sitesById.TryGetValue(linkedId, out var linkedSite))
            {
                if (!string.Equals(fence.SiteNumber?.Trim(), linkedSite.ExternalCode.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    fence.SiteNumber = linkedSite.ExternalCode;
                    fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    canonicalizedGeofences++;
                }
                continue;
            }

            var candidates = new List<Site>();
            var siteNumberKey = Normalize(fence.SiteNumber);
            if (siteNumberKey.Length > 0 && siteNumberKey != "1")
                candidates.AddRange(activeSites.Where(x => Normalize(x.ExternalCode) == siteNumberKey));

            if (candidates.Select(x => x.Id).Distinct().Count() != 1)
            {
                candidates.Clear();
                var fenceNameKey = Normalize(fence.Name);
                candidates.AddRange(activeSites.Where(site => SiteNames(site).Any(name => Normalize(name) == fenceNameKey)));
            }

            candidates = candidates.GroupBy(x => x.Id).Select(x => x.First()).ToList();
            if (candidates.Count == 1)
            {
                fence.SiteId = candidates[0].Id;
                fence.SiteNumber = candidates[0].ExternalCode;
                fence.UpdatedAtUtc = DateTimeOffset.UtcNow;
                linkedGeofences++;
                continue;
            }

            await FlagReviewAsync(db, GeofenceReviewType, $"geofence:{fence.Id:N}", new
            {
                reason = candidates.Count == 0 ? "geofence_has_no_site_match" : "geofence_site_match_ambiguous",
                geofence = new { fence.Id, fence.Name, fence.SiteNumber },
                candidates = candidates.Select(x => new { x.Id, x.ExternalCode, x.Name }).ToArray(),
                requestedAction = "Enter the canonical Site code or geofence reference."
            }, source, reviewKeys, ct);
        }

        await db.SaveChangesAsync(ct);
        return new SiteMasterConsolidationResult(
            promotedCustomers,
            archivedDuplicates,
            linkedGeofences,
            canonicalizedGeofences,
            reviewKeys.Count);
    }

    private static IReadOnlyList<IReadOnlyList<Site>> DuplicateGroups(IReadOnlyList<Site> sites)
    {
        var remaining = sites.ToDictionary(x => x.Id);
        var groups = new List<IReadOnlyList<Site>>();

        foreach (var seed in sites)
        {
            if (!remaining.Remove(seed.Id))
                continue;

            var group = new List<Site> { seed };
            var queue = new Queue<Site>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var matches = remaining.Values
                    .Where(candidate => SharesDuplicateIdentity(current, candidate))
                    .ToList();

                foreach (var match in matches)
                {
                    remaining.Remove(match.Id);
                    group.Add(match);
                    queue.Enqueue(match);
                }
            }

            if (group.Count > 1)
                groups.Add(group);
        }

        return groups;
    }

    private static bool SharesDuplicateIdentity(Site left, Site right)
    {
        var leftName = Normalize(left.Name);
        var rightName = Normalize(right.Name);
        if (leftName.Length == 0 || rightName.Length == 0)
            return false;

        return SiteNames(right).Any(name => Normalize(name) == leftName) ||
               SiteNames(left).Any(name => Normalize(name) == rightName);
    }

    private static async Task<Dictionary<Guid, Guid?>> LoadArchivedSiteDispositionsAsync(
        TmsDbContext db,
        CancellationToken ct)
    {
        var audits = await db.MasterDataAudits.AsNoTracking()
            .Where(x => x.EntityType == "Site" &&
                        (x.Action == "Archived" ||
                         x.Action == "Restored" ||
                         x.Action == MergedDuplicateAction))
            .ToListAsync(ct);

        // MasterDataAudit writes are now transactionally captured in AuditOutbox and
        // materialised asynchronously. Reconciliation decisions cannot wait for the
        // background processor: a durable pending disposition is already authoritative.
        // Read valid site-disposition payloads directly from the outbox as well as the
        // materialised audit table, deduplicating by deterministic audit Id below.
        var pendingAuditPayloads = await db.AuditOutboxes.AsNoTracking()
            .Where(x => x.EventType == AuditOutboxEventTypes.MasterDataAudit &&
                        x.ProcessedAt == null)
            .Select(x => x.Payload)
            .ToListAsync(ct);

        foreach (var payload in pendingAuditPayloads)
        {
            try
            {
                var pendingAudit = JsonSerializer.Deserialize<MasterDataAudit>(payload);
                if (pendingAudit is not null &&
                    string.Equals(pendingAudit.EntityType, "Site", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(pendingAudit.Action, "Archived", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(pendingAudit.Action, "Restored", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(pendingAudit.Action, MergedDuplicateAction, StringComparison.OrdinalIgnoreCase)) &&
                    audits.All(existing => existing.Id != pendingAudit.Id))
                {
                    audits.Add(pendingAudit);
                }
            }
            catch (JsonException)
            {
                // Poison events are handled by the outbox retry policy. They cannot safely
                // influence Site restoration decisions until their payload is valid.
            }
        }

        var result = new Dictionary<Guid, Guid?>();
        foreach (var group in audits.GroupBy(x => x.EntityId))
        {
            var latest = group
                .OrderByDescending(x => x.ChangedAtUtc)
                .ThenByDescending(x => x.Id)
                .First();

            if (string.Equals(latest.Action, "Restored", StringComparison.OrdinalIgnoreCase))
                continue;

            Guid? canonicalSiteId = null;
            if (string.Equals(latest.Action, MergedDuplicateAction, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(latest.ChangesJson))
            {
                try
                {
                    using var document = JsonDocument.Parse(latest.ChangesJson);
                    if (document.RootElement.TryGetProperty("canonicalSiteId", out var canonical) &&
                        canonical.TryGetGuid(out var parsed))
                    {
                        canonicalSiteId = parsed;
                    }
                }
                catch (JsonException)
                {
                    // The archive itself remains authoritative even if old audit metadata
                    // cannot identify the canonical replacement Site.
                }
            }

            result[group.Key] = canonicalSiteId;
        }

        return result;
    }

    /// <summary>
    /// Returns true when a group of sites with no geofences is safe to auto-merge:
    /// all members share the same ExternalCode, OR all have identical normalised names
    /// and no conflicting postcodes or addresses — classic broken-import duplicates.
    /// </summary>
    private static bool IsSafeNoGeofenceSiteDuplicate(IReadOnlyList<Site> rows)
    {
        if (rows.Count < 2) return false;

        // Same ExternalCode — definitively the same site entered twice
        var codes = rows.Select(s => Normalize(s.ExternalCode)).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count == 1) return true;

        // Same normalised name with no conflicting postcode or address evidence
        var names = rows.Select(s => Normalize(s.Name)).Where(n => n.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count != 1) return false;

        var postcodes = rows
            .Select(s => ExtractPostcode(s.CollectionAddress))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (postcodes.Count > 1) return false; // conflicting postcodes — different sites

        var addresses = rows
            .Select(s => NormalizeAddress(s.CollectionAddress))
            .Where(a => a.Length >= 8)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (addresses.Count > 1) return false; // conflicting addresses — different sites

        return true;
    }

    private static int SiteCompleteness(Site site) => new object?[]
    {
        site.CollectionAddress, site.MapLink, site.Latitude, site.Longitude,
        site.CollectionInstructions, site.DriverTextName, site.Aliases,
        site.OperationalRegion, site.CustomerCode
    }.Count(value => value is not null && !string.IsNullOrWhiteSpace(value?.ToString()));

    private static string? ExtractPostcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            value.ToUpperInvariant(),
            @"\b([A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\b");
        return match.Success
            ? System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, @"\s+", string.Empty)
            : null;
    }

    private static string NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var compact = System.Text.RegularExpressions.Regex.Replace(
            value, @"\b(road|rd|street|st|avenue|ave|lane|ln|drive|dr|unit|industrial|estate)\b",
            string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return Normalize(compact);
    }

    private static IEnumerable<string?> SiteNames(Site site)
    {
        yield return site.Name;
        yield return site.DriverTextName;
        if (!string.IsNullOrWhiteSpace(site.Aliases))
        {
            foreach (var alias in site.Aliases.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return alias;
        }
    }

    private static Task AddSiteAliasesAsync(
        TmsDbContext db,
        Site site,
        IEnumerable<string?> aliases,
        string source,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = $"masterdetail:site:{Normalize(site.ExternalCode).ToLowerInvariant()}";
        var row = db.StagedImports.Local.FirstOrDefault(x => string.Equals(x.IdempotencyKey, key, StringComparison.OrdinalIgnoreCase));
        JsonObject payload;
        try
        {
            payload = row is null ? new JsonObject() : JsonNode.Parse(row.PayloadJson)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            payload = new JsonObject();
        }

        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (payload["aliases"] is JsonValue aliasNode && aliasNode.TryGetValue<string>(out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            foreach (var alias in existing.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                merged.Add(alias);
        }
        foreach (var alias in aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
            merged.Add(alias!.Trim());

        var aliasText = string.Join("; ", merged.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        payload["externalCode"] = site.ExternalCode;
        payload["aliases"] = aliasText;
        site.Aliases = aliasText;

        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = "masterdetail:site",
                IdempotencyKey = key,
                PayloadJson = payload.ToJsonString(),
                Status = StagingStatus.Promoted,
                Source = source,
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewedBy = source,
                ReviewNote = "Canonical Site aliases consolidated from legacy location masters."
            };
            db.StagedImports.Add(row);
        }
        else
        {
            row.EntityType = "masterdetail:site";
            row.PayloadJson = payload.ToJsonString();
            row.Status = StagingStatus.Promoted;
            row.Source ??= source;
            row.ReviewedAtUtc = DateTimeOffset.UtcNow;
            row.ReviewedBy = source;
        }

        return Task.CompletedTask;
    }

    private static Task FlagReviewAsync(
        TmsDbContext db,
        string entityType,
        string suffix,
        object payload,
        string source,
        ISet<string> reviewKeys,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullKey = $"{entityType}:{suffix}";
        var key = fullKey.Length <= 200 ? fullKey : fullKey[..200];
        reviewKeys.Add(key);
        var row = db.StagedImports.Local.FirstOrDefault(x => string.Equals(x.IdempotencyKey, key, StringComparison.OrdinalIgnoreCase));
        var json = JsonSerializer.Serialize(payload);
        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = entityType,
                IdempotencyKey = key,
                PayloadJson = json,
                Status = StagingStatus.PendingReview,
                Source = source,
                ReviewNote = "Site/geofence identity requires manual confirmation."
            };
            db.StagedImports.Add(row);
        }
        else
        {
            row.EntityType = entityType;
            row.PayloadJson = json;
            row.Status = StagingStatus.PendingReview;
            row.Source ??= source;
            row.ReviewedAtUtc = null;
            row.ReviewedBy = null;
            row.ReviewNote = "Site/geofence identity requires manual confirmation.";
        }

        return Task.CompletedTask;
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
