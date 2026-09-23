using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class FalconGeofenceImportService
{
    private static readonly HashSet<string> RenameNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "GEOFENCE", "SITE", "DEPOT", "WAREHOUSE", "BUILDING", "UNIT"
    };

    public static async Task<FalconGeofenceImportPreview> PreviewAsync(TmsDbContext db, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        var existing = await db.SiteGeofences.AsNoTracking().ToListAsync(ct);
        var existingByName = existing
            .GroupBy(x => x.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(item => item.UpdatedAtUtc).First(), StringComparer.OrdinalIgnoreCase);
        var existingByFingerprint = existing
            .Where(x => TryFingerprint(x.PolygonJson, out _))
            .GroupBy(x => Fingerprint(x.PolygonJson), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(item => item.UpdatedAtUtc).First(), StringComparer.Ordinal);

        var duplicateNames = parsed.Rows.Where(x => x.Valid)
            .GroupBy(x => x.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = parsed.Rows.Select(row =>
        {
            if (!row.Valid)
                return Row(row, "Invalid", row.Error);
            if (duplicateNames.Contains(row.NormalizedName))
                return Row(row, "Invalid", "The file contains more than one geofence with this normalized name.");

            existingByName.TryGetValue(row.NormalizedName, out var imported);
            var exactSites = sites.Where(site => Normalize(site.Name) == row.NormalizedName).ToList();
            if (imported is not null)
            {
                var linked = imported.SiteId is null ? null : sites.FirstOrDefault(site => site.Id == imported.SiteId.Value);
                return Row(row, "AlreadyImported", null, linked, imported.Id, null);
            }

            if (exactSites.Count == 1)
                return Row(row, "Matched", null, exactSites[0], null, null);

            existingByFingerprint.TryGetValue(row.Fingerprint, out var sameBoundary);
            if (sameBoundary is not null && NamesSupportRename(row.Name, sameBoundary.Name))
                return Row(row, "PossibleRename", "The polygon and geofence name closely match an existing geofence. Confirm a rename only if it is genuinely the same operational location.", null, null, sameBoundary.Id);

            if (sameBoundary is not null)
                return Row(row, "NeedsLinking", $"This boundary is also used by '{sameBoundary.Name}'. Different buildings/localities may share a site boundary, so this geofence will remain a separate record unless you explicitly link it to a Site.");

            return Row(row, "NeedsLinking", exactSites.Count > 1 ? "More than one Site has the same normalized name." : null);
        }).ToList();

        return new FalconGeofenceImportPreview(parsed.Category, rows.Count, rows);
    }

    public static async Task<FalconGeofenceImportResult> CommitAsync(
        TmsDbContext db,
        FalconGeofenceCommitRequest request,
        string actor,
        CancellationToken ct)
    {
        var parsed = Parse(request.Export);
        var decisions = (request.Decisions ?? [])
            .GroupBy(x => x.ClientKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var duplicateNames = parsed.Rows.Where(x => x.Valid)
            .GroupBy(x => x.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var invalidSelected = parsed.Rows.Any(row =>
            (!row.Valid || duplicateNames.Contains(row.NormalizedName))
            && (!decisions.TryGetValue(row.ClientKey, out var decision) || decision.Skip != true));
        if (invalidSelected)
            throw new FalconGeofenceValidationException("Every invalid or duplicate geofence row must be skipped before import.");

        var sites = await db.Sites.Where(x => x.Active).ToListAsync(ct);
        var fences = await db.SiteGeofences.ToListAsync(ct);
        var existingByName = fences
            .GroupBy(x => x.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(item => item.UpdatedAtUtc).First(), StringComparer.OrdinalIgnoreCase);
        var existingById = fences.ToDictionary(x => x.Id);
        var existingByFingerprint = fences
            .Where(x => TryFingerprint(x.PolygonJson, out _))
            .GroupBy(x => Fingerprint(x.PolygonJson), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(item => item.UpdatedAtUtc).First(), StringComparer.Ordinal);

        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational())
            transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            var created = 0;
            var updated = 0;
            var linked = 0;
            var skipped = 0;
            var affected = new List<Guid>();
            var now = DateTimeOffset.UtcNow;

            foreach (var row in parsed.Rows)
            {
                decisions.TryGetValue(row.ClientKey, out var decision);
                if (decision?.Skip == true)
                {
                    skipped++;
                    continue;
                }

                existingByName.TryGetValue(row.NormalizedName, out var fence);
                if (fence is null && existingByFingerprint.TryGetValue(row.Fingerprint, out var possibleRename) && NamesSupportRename(row.Name, possibleRename.Name))
                {
                    if (decision?.ConfirmRenameGeofenceId != possibleRename.Id)
                        throw new FalconGeofenceValidationException($"Geofence '{row.Name}' is a possible rename and requires confirmation.");
                    fence = possibleRename;
                }

                Site? selectedSite = null;
                if (decision?.SiteId is not null)
                    selectedSite = sites.SingleOrDefault(x => x.Id == decision.SiteId.Value)
                        ?? throw new FalconGeofenceValidationException($"The selected Site for '{row.Name}' does not exist or is inactive.");
                else if (fence?.SiteId is not null)
                    selectedSite = sites.SingleOrDefault(x => x.Id == fence.SiteId.Value);
                else
                {
                    var exactSites = sites.Where(x => Normalize(x.Name) == row.NormalizedName).ToList();
                    if (exactSites.Count == 1) selectedSite = exactSites[0];
                }

                if (selectedSite is null)
                    throw new FalconGeofenceValidationException($"Geofence '{row.Name}' must be linked to an existing or newly created Site, or skipped.");

                var wasCreated = fence is null;
                if (wasCreated)
                {
                    fence = new SiteGeofence
                    {
                        Name = row.Name,
                        NormalizedName = row.NormalizedName,
                        PolygonJson = row.PolygonJson,
                        Active = true
                    };
                    db.SiteGeofences.Add(fence);
                    fences.Add(fence);
                    created++;
                }
                else
                {
                    updated++;
                }

                var oldSiteId = fence.SiteId;
                fence.Name = row.Name;
                fence.NormalizedName = row.NormalizedName;
                fence.Category = row.Category;
                fence.CategoryMaxWaitMinutes = row.CategoryMaxWaitMinutes;
                fence.MaxWaitMinutes = row.MaxWaitMinutes;
                fence.PendingEntryMinutes = row.PendingEntryMinutes;
                fence.PendingExitMinutes = row.PendingExitMinutes;
                fence.PolygonJson = row.PolygonJson;
                fence.SiteId = selectedSite.Id;
                fence.SiteNumber = selectedSite.ExternalCode;
                fence.Active = true;
                fence.UpdatedAtUtc = now;
                if (oldSiteId != selectedSite.Id) linked++;
                affected.Add(fence.Id);

                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Geofence",
                    EntityId = fence.Id,
                    Action = wasCreated ? "ImportedFromDotTracking" : "UpdatedFromDotTracking",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        request.SourceFileName,
                        category = row.Category,
                        siteId = selectedSite.Id,
                        siteCode = selectedSite.ExternalCode,
                        linkChanged = oldSiteId != selectedSite.Id
                    })
                });

                existingByName[row.NormalizedName] = fence;
                existingById[fence.Id] = fence;
                existingByFingerprint[row.Fingerprint] = fence;
            }

            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return new FalconGeofenceImportResult(parsed.Rows.Count, created, updated, linked, skipped, affected);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    internal static bool NamesSupportRename(string? incomingName, string? existingName)
    {
        var incoming = Normalize(incomingName);
        var existing = Normalize(existingName);
        if (incoming.Length == 0 || existing.Length == 0) return false;
        if (string.Equals(incoming, existing, StringComparison.OrdinalIgnoreCase)) return true;

        if ((incoming.Contains(existing, StringComparison.OrdinalIgnoreCase) || existing.Contains(incoming, StringComparison.OrdinalIgnoreCase))
            && Math.Min(incoming.Length, existing.Length) >= Math.Max(incoming.Length, existing.Length) * 0.6)
            return true;

        var left = RenameTokens(incoming);
        var right = RenameTokens(existing);
        if (left.Count == 0 || right.Count == 0) return false;
        var intersection = left.Intersect(right, StringComparer.OrdinalIgnoreCase).Count();
        var union = left.Union(right, StringComparer.OrdinalIgnoreCase).Count();
        return union > 0 && (double)intersection / union >= 0.75;
    }

    private static HashSet<string> RenameTokens(string value) =>
        new(value
            .Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ')
            .ToArray()
            .AsSpan()
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1 && !RenameNoiseTokens.Contains(token)), StringComparer.OrdinalIgnoreCase);

    private static FalconParsedExport Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new FalconGeofenceValidationException("Expected a Falcon geofence JSON object.");
        if (!payload.TryGetProperty("format", out var format) || format.GetString() != "falcon.geofence")
            throw new FalconGeofenceValidationException("Only falcon.geofence exports are supported.");
        if (!payload.TryGetProperty("version", out var version) || !version.TryGetInt32(out var versionNumber) || versionNumber != 1)
            throw new FalconGeofenceValidationException("Only Falcon geofence export version 1 is supported.");
        if (!payload.TryGetProperty("geofences", out var geofences) || geofences.ValueKind != JsonValueKind.Array)
            throw new FalconGeofenceValidationException("Expected a geofences array.");

        var category = Text(payload, "category")?.Trim();
        var categoryMax = Integer(payload, "category_max_wait_time");
        var rows = new List<FalconParsedRow>();
        var index = 0;
        foreach (var item in geofences.EnumerateArray())
        {
            var name = Text(item, "name")?.Trim() ?? string.Empty;
            var normalized = Normalize(name);
            var clientKey = normalized.Length > 0 ? normalized : $"ROW-{index + 1}";
            var error = ValidatePoints(item, out var polygonJson);
            if (name.Length == 0) error = "A geofence name is required.";
            rows.Add(new FalconParsedRow(
                index,
                clientKey,
                name,
                normalized,
                category,
                categoryMax,
                Integer(item, "max_wait_time"),
                Integer(item, "pending_entry_minutes") ?? 0,
                Integer(item, "pending_exit_minutes") ?? 0,
                polygonJson,
                error is null ? Fingerprint(polygonJson) : string.Empty,
                error is null,
                error));
            index++;
        }

        return new FalconParsedExport(category, rows);
    }

    private static string? ValidatePoints(JsonElement item, out string polygonJson)
    {
        polygonJson = "[]";
        if (!item.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            return "A polygon points array is required.";

        var parsed = new List<double[]>();
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2)
                return "Every polygon point must contain longitude and latitude.";
            var values = point.EnumerateArray().Take(2).ToArray();
            if (!values[0].TryGetDouble(out var longitude) || !values[1].TryGetDouble(out var latitude)
                || !double.IsFinite(longitude) || !double.IsFinite(latitude)
                || longitude < -180 || longitude > 180 || latitude < -90 || latitude > 90)
                return "Polygon coordinates must be finite longitude/latitude values in range.";
            parsed.Add([longitude, latitude]);
        }

        if (parsed.DistinctBy(x => $"{x[0]:R}|{x[1]:R}").Count() < 3)
            return "A polygon requires at least three distinct points.";

        polygonJson = JsonSerializer.Serialize(parsed);
        return null;
    }

    private static FalconGeofenceImportRow Row(
        FalconParsedRow row,
        string status,
        string? error,
        Site? site = null,
        Guid? existingGeofenceId = null,
        Guid? possibleRenameGeofenceId = null) =>
        new(row.Index, row.ClientKey, row.Name, status, error, site?.Id, site?.ExternalCode, site?.Name, existingGeofenceId, possibleRenameGeofenceId);

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ToString()
            : null;

    private static int? Integer(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string Fingerprint(string polygonJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(polygonJson)));

    private static bool TryFingerprint(string polygonJson, out string fingerprint)
    {
        try
        {
            using var document = JsonDocument.Parse(polygonJson);
            var holder = JsonSerializer.SerializeToElement(new { points = document.RootElement });
            var error = ValidatePoints(holder, out var canonical);
            fingerprint = error is null ? Fingerprint(canonical) : string.Empty;
            return error is null;
        }
        catch (JsonException)
        {
            fingerprint = string.Empty;
            return false;
        }
    }

    private sealed record FalconParsedExport(string? Category, IReadOnlyList<FalconParsedRow> Rows);
    private sealed record FalconParsedRow(
        int Index,
        string ClientKey,
        string Name,
        string NormalizedName,
        string? Category,
        int? CategoryMaxWaitMinutes,
        int? MaxWaitMinutes,
        int PendingEntryMinutes,
        int PendingExitMinutes,
        string PolygonJson,
        string Fingerprint,
        bool Valid,
        string? Error);
}

public sealed record FalconGeofenceImportPreview(string? Category, int Total, IReadOnlyList<FalconGeofenceImportRow> Rows);
public sealed record FalconGeofenceImportRow(
    int Index,
    string ClientKey,
    string Name,
    string Status,
    string? Error,
    Guid? SuggestedSiteId,
    string? SuggestedSiteCode,
    string? SuggestedSiteName,
    Guid? ExistingGeofenceId,
    Guid? PossibleRenameGeofenceId);
public sealed record FalconGeofenceCommitRequest(
    string? SourceFileName,
    JsonElement Export,
    IReadOnlyList<FalconGeofenceImportDecision>? Decisions);
public sealed record FalconGeofenceImportDecision(
    string ClientKey,
    Guid? SiteId,
    bool Skip = false,
    Guid? ConfirmRenameGeofenceId = null);
public sealed record FalconGeofenceImportResult(
    int Supplied,
    int Created,
    int Updated,
    int Linked,
    int Skipped,
    IReadOnlyList<Guid> AffectedGeofenceIds);

public sealed class FalconGeofenceValidationException(string message) : Exception(message);
