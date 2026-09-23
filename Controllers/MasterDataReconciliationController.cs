using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/master-data/reconcile"), Authorize]
public sealed class MasterDataReconciliationController(TmsDbContext db, StagingService staging) : ControllerBase
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "driver", "vehicle", "trailer", "fuelcard", "site", "marketcontact", "sitetimingrule"
    };

    [HttpGet, Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Current([FromQuery] string entityType, CancellationToken ct)
    {
        if (!SupportedTypes.Contains(entityType))
            return BadRequest(new ErrorResponse("unsupported_master_type", $"Unsupported master-data type '{entityType}'.", HttpContext.TraceIdentifier));

        return Ok(new { entityType = entityType.ToLowerInvariant(), records = await CurrentRecordsAsync(entityType, ct) });
    }

    [HttpGet("export"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var entityTypes = new[] { "driver", "vehicle", "trailer", "fuelcard", "site", "marketcontact", "sitetimingrule" };
        var records = new Dictionary<string, List<JsonObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entityType in entityTypes)
            records[entityType] = await CurrentRecordsAsync(entityType, ct);

        return Ok(new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            source = "TMS SQL operational projection",
            entityTypes,
            records
        });
    }

    [HttpPost("apply"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Apply(List<StageImportRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0 || requests.Count > 10000)
            return BadRequest(new ErrorResponse("invalid_batch", "Submit between 1 and 10000 master-data records.", HttpContext.TraceIdentifier));

        var results = new List<object>();
        var applied = 0;
        var failed = 0;

        foreach (var request in requests)
        {
            if (!SupportedTypes.Contains(request.EntityType))
            {
                failed++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = false, error = "Unsupported reconciliation master-data type." });
                continue;
            }

            try
            {
                if (request.EntityType.Equals("sitetimingrule", StringComparison.OrdinalIgnoreCase))
                {
                    await UpsertSiteTimingRuleAsync(request.Payload, request.Source, ct);
                }
                else
                {
                    var current = await FindCurrentRecordAsync(request.EntityType, request.Payload, ct);
                    var merged = MasterDataReconcileMerge.Merge(current, request.Payload);
                    using var document = JsonDocument.Parse(merged.ToJsonString());
                    await staging.PromoteDirect(request.EntityType, document.RootElement.Clone(), ct);
                }

                applied++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                staging.ClearTrackedChanges();
                failed++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = false, error = ex.GetBaseException().Message });
            }
        }

        return Ok(new { received = requests.Count, applied, registered = 0, failed, linked = 0, results });
    }

    private async Task<List<JsonObject>> CurrentRecordsAsync(string entityType, CancellationToken ct)
    {
        switch (entityType.ToLowerInvariant())
        {
            case "driver":
            {
                var rows = await db.Drivers.AsNoTracking().OrderBy(x => x.DisplayName).Take(5000).ToListAsync(ct);
                await MasterDetailStore.EnrichDriversAsync(db, rows, ct);
                return rows.Select(x => JsonObjectOf(new
                {
                    x.Id, x.EmployeeNumber, x.DisplayName, x.TachoName, x.TachoMasterDriverId, x.TachoCardNumber,
                    x.MobileNumber, x.DriverType, x.DriverGroup, x.Skills, x.Coding, x.AgencyName,
                    x.NorthEligible, x.PreloadEligible, x.Notes, x.DrivingLicenceNumber, x.LicenceExpiry,
                    x.LicenceStatus, x.LastTachoSyncUtc, x.Active
                })).ToList();
            }
            case "vehicle":
            {
                var rows = await db.Vehicles.AsNoTracking().OrderBy(x => x.Registration).Take(5000).ToListAsync(ct);
                return rows.Select(x => JsonObjectOf(new
                {
                    x.Id, x.Registration, x.FleetNumber, x.Abbreviation, x.Transmission, x.DvsCompliant, x.CabMobile,
                    x.FuelProvider, x.FuelPin, x.ShellCard, x.BpRedCard, x.BpPlainCard, x.FuelPinSecretName,
                    x.FuelCardLastFour, x.FleetioId, x.FleetioName, x.FleetioStatus, x.Notes, x.Active
                })).ToList();
            }
            case "trailer":
            {
                var rows = await db.Trailers.AsNoTracking().OrderBy(x => x.TrailerNumber).Take(5000).ToListAsync(ct);
                return rows.Select(x => JsonObjectOf(new
                {
                    x.Id, x.TrailerNumber, x.Type, x.StandardCapacity, x.EuroCapacity, x.Notes, x.Active
                })).ToList();
            }
            case "fuelcard":
            {
                var rows = await db.Vehicles.AsNoTracking()
                    .Where(x => x.FuelProvider != null || x.FuelPin != null || x.ShellCard != null ||
                                x.BpRedCard != null || x.BpPlainCard != null || x.FuelCardLastFour != null)
                    .OrderBy(x => x.Registration).Take(5000).ToListAsync(ct);
                return rows.Select(x => JsonObjectOf(new
                {
                    vehicleRegistration = x.Registration,
                    x.FuelProvider, x.FuelPin, x.ShellCard, x.BpRedCard, x.BpPlainCard,
                    x.FuelPinSecretName, x.FuelCardLastFour
                })).ToList();
            }
            case "site":
            {
                var rows = await db.Sites.AsNoTracking().OrderBy(x => x.Name).Take(10000).ToListAsync(ct);
                await MasterDetailStore.EnrichSitesAsync(db, rows, ct);
                return rows.Select(x => JsonObjectOf(new
                {
                    x.Id, x.ExternalCode, x.Name, x.DriverTextName, x.CollectionAddress, x.CollectionInstructions,
                    x.MapLink, x.Aliases, x.CustomField1, x.CustomField2, x.CustomField3, x.OperationalRegion,
                    x.Latitude, x.Longitude, x.Active
                })).ToList();
            }
            case "marketcontact":
            {
                var rows = await db.MarketContacts.AsNoTracking().OrderBy(x => x.Market).ThenBy(x => x.Name).Take(10000).ToListAsync(ct);
                return rows.Select(x => JsonObjectOf(new
                {
                    x.Id, x.MarketKey, x.Market, x.Name, x.StandOrLocation, x.Salesman, x.Sender, x.Active
                })).ToList();
            }
            case "sitetimingrule":
            {
                var rows = await db.StagedImports.AsNoTracking()
                    .Where(x => x.EntityType == "masterdetail:sitetimingrule" && x.Status == StagingStatus.Promoted)
                    .OrderByDescending(x => x.ReviewedAtUtc ?? x.ReceivedAtUtc)
                    .Take(10000)
                    .ToListAsync(ct);

                var unique = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    var node = JsonNode.Parse(row.PayloadJson) as JsonObject;
                    var route = node?["routeCombination"]?.ToString();
                    if (node is null || string.IsNullOrWhiteSpace(route)) continue;
                    unique.TryAdd(Normalise(route), node);
                }
                return unique.Values.ToList();
            }
            default:
                return [];
        }
    }

    private async Task<JsonObject?> FindCurrentRecordAsync(string entityType, JsonElement payload, CancellationToken ct)
    {
        var records = await CurrentRecordsAsync(entityType, ct);
        var incoming = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        return records.FirstOrDefault(record => IdentityMatches(entityType, record, incoming));
    }

    private static bool IdentityMatches(string entityType, JsonObject current, JsonObject incoming)
    {
        return entityType.ToLowerInvariant() switch
        {
            "driver" => DriverIdentityMatches(current, incoming),
            "vehicle" => SameCompact(Value(current, "registration"), Value(incoming, "registration")),
            "trailer" => Same(Value(current, "trailerNumber"), Value(incoming, "trailerNumber")),
            "site" => SiteIdentityMatches(current, incoming),
            "marketcontact" => MarketIdentityMatches(current, incoming),
            _ => false
        };
    }

    private static bool DriverIdentityMatches(JsonObject current, JsonObject incoming)
    {
        var incomingMember = Value(incoming, "tachoMasterDriverId") ?? Value(incoming, "tachomasterDriverId");
        if (!string.IsNullOrWhiteSpace(incomingMember))
            return Same(Value(current, "tachoMasterDriverId"), incomingMember);

        return Same(Value(current, "employeeNumber"), Value(incoming, "employeeNumber")) ||
               (string.IsNullOrWhiteSpace(Value(incoming, "employeeNumber")) && Same(Value(current, "displayName"), Value(incoming, "displayName")));
    }

    private static bool SiteIdentityMatches(JsonObject current, JsonObject incoming)
    {
        if (Same(Value(current, "externalCode"), Value(incoming, "externalCode"))) return true;
        if (!Same(Value(current, "name"), Value(incoming, "name"))) return false;

        var incomingAddress = Value(incoming, "collectionAddress") ?? Value(incoming, "address");
        var currentAddress = Value(current, "collectionAddress") ?? Value(current, "address");
        return string.IsNullOrWhiteSpace(incomingAddress) || string.IsNullOrWhiteSpace(currentAddress) || SameCompact(currentAddress, incomingAddress);
    }

    private static bool MarketIdentityMatches(JsonObject current, JsonObject incoming)
    {
        var incomingKey = Value(incoming, "marketKey");
        if (!string.IsNullOrWhiteSpace(incomingKey) && Same(Value(current, "marketKey"), incomingKey)) return true;

        return Same(Value(current, "market"), Value(incoming, "market")) &&
               Same(Value(current, "name"), Value(incoming, "name")) &&
               Same(Value(current, "standOrLocation"), Value(incoming, "standOrLocation"));
    }

    private async Task UpsertSiteTimingRuleAsync(JsonElement payload, string? source, CancellationToken ct)
    {
        var routeCombination = Required(payload, "routeCombination");
        var idempotencyKey = $"masterdetail:sitetimingrule:{Normalise(routeCombination)}";
        var existing = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
        var current = existing is null ? null : JsonNode.Parse(existing.PayloadJson) as JsonObject;
        var merged = MasterDataReconcileMerge.Merge(current, payload);

        if (existing is null)
        {
            existing = new StagedImport
            {
                EntityType = "masterdetail:sitetimingrule",
                IdempotencyKey = idempotencyKey,
                PayloadJson = "{}"
            };
            db.StagedImports.Add(existing);
        }

        existing.PayloadJson = merged.ToJsonString();
        existing.Source = source ?? "SLH site timing master CSV";
        existing.Status = StagingStatus.Promoted;
        existing.ReviewedAtUtc = DateTimeOffset.UtcNow;
        existing.ReviewNote = "Reviewed site timing rule retained as protected master data.";
        await db.SaveChangesAsync(ct);
    }

    private static JsonObject JsonObjectOf<T>(T value) =>
        JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)) as JsonObject ?? new JsonObject();

    private static string? Value(JsonObject item, string name) => item[name]?.ToString();

    private static string Required(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (Normalise(property.Name) == Normalise(name) &&
                property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
                return property.Value.GetString()!.Trim();
        }
        throw new JsonException($"Payload requires {name}.");
    }

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameCompact(string? left, string? right) =>
        Same(left is null ? null : Normalise(left), right is null ? null : Normalise(right));

    private static string Normalise(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public static class MasterDataReconcileMerge
{
    public static JsonObject Merge(JsonObject? current, JsonElement incoming)
    {
        var result = current?.DeepClone() as JsonObject ?? new JsonObject();
        var incomingObject = JsonNode.Parse(incoming.GetRawText()) as JsonObject ?? new JsonObject();
        MergeInto(result, incomingObject);
        return result;
    }

    public static void MergeInto(JsonObject target, JsonObject incoming)
    {
        foreach (var pair in incoming)
        {
            if (pair.Value is null) continue;
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text)) continue;
            target[pair.Key] = pair.Value.DeepClone();
        }
    }
}
