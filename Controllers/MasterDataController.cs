using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/master-data")]
[Authorize]
public sealed class MasterDataController(StagingService staging, TmsDbContext db) : ControllerBase
{
    private static readonly HashSet<string> DirectTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "customer",
        "customercontact",
        "emailroute",
        "vehicle",
        "driver",
        "trailer",
        "site",
        "marketcontact",
        "fuelprice",
        "sitetimingrule"
    };

    [HttpPost("apply"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Apply(List<StageImportRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0 || requests.Count > 10000) return BadRequest(new ErrorResponse("invalid_batch", "Submit between 1 and 10000 master-data records.", HttpContext.TraceIdentifier));
        requests = requests
            .OrderBy(request => request.EntityType.ToLowerInvariant() switch
            {
                "customer" => 0,
                "site" => 1,
                "sitetimingrule" => 2,
                "customercontact" => 3,
                "emailroute" => 4,
                "driver" => 5,
                "vehicle" => 6,
                "trailer" => 7,
                "marketcontact" => 8,
                "fuelprice" => 9,
                _ => 99
            })
            .ToList();
        var results = new List<object>();
        var applied = 0;
        var registered = 0;
        var failed = 0;
        foreach (var request in requests)
        {
            if (!DirectTypes.Contains(request.EntityType))
            {
                failed++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = false, error = "This endpoint only applies master-data records." });
                continue;
            }

            try
            {
                if (IsSiteTimingRule(request.EntityType))
                    await ApplySiteTimingRule(request, ct);
                else
                    await staging.PromoteDirect(request.EntityType, request.Payload, ct);

                applied++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = true });
            }
            catch (Exception ex)
            {
                staging.ClearTrackedChanges();
                if (IsDatabaseUnavailable(ex))
                {
                    try
                    {
                        await staging.RegisterFallback(request.EntityType, request.Payload, request.Source, ct);
                        registered++;
                        results.Add(new { request.EntityType, request.IdempotencyKey, applied = false, registered = true, error = "Accepted into the recovery register, but not yet available in the live table. The database schema must be repaired before this row is operational." });
                        continue;
                    }
                    catch (Exception registerException)
                    {
                        ex = registerException;
                    }
                }
                failed++;
                results.Add(new { request.EntityType, request.IdempotencyKey, applied = false, error = ex.GetBaseException().Message });
            }
        }

        // Recovery-register linking is deliberately separate and bounded. Running the
        // entire historic register after every workbook chunk caused otherwise-successful
        // imports to exceed the Azure gateway request timeout.
        return Ok(new { received = requests.Count, applied, registered, failed, linked = 0, results });
    }

    [HttpPost("register/link"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> LinkRegister([FromQuery] int batchSize, CancellationToken ct)
    {
        batchSize = Math.Clamp(batchSize <= 0 ? 100 : batchSize, 1, 200);
        var linked = await staging.LinkRegistered(batchSize, ct);
        return Ok(new { linked, batchSize, message = linked == 0 ? "No registered rows could be linked yet." : $"Linked {linked} registered rows into the live master tables. Run again if more recovery rows remain." });
    }

    private async Task ApplySiteTimingRule(StageImportRequest request, CancellationToken ct)
    {
        var payload = request.Payload;
        var routeCombination = Text(payload, "routeCombination")
            ?? JoinRoute(Text(payload, "collectionSite") ?? Text(payload, "collection") ?? Text(payload, "from"),
                Text(payload, "deliverySite") ?? Text(payload, "delivery") ?? Text(payload, "to"));

        if (string.IsNullOrWhiteSpace(routeCombination))
            throw new JsonException("Site timing rule requires routeCombination or collectionSite and deliverySite.");

        var normalised = new
        {
            routeCombination = routeCombination.Trim(),
            collectionSite = Text(payload, "collectionSite") ?? Text(payload, "collection") ?? Text(payload, "from"),
            deliverySite = Text(payload, "deliverySite") ?? Text(payload, "delivery") ?? Text(payload, "to"),
            palletType = Text(payload, "palletType") ?? Text(payload, "palletsType") ?? Text(payload, "palletFormat"),
            lastDespatch = Text(payload, "lastDespatch") ?? Text(payload, "lastDispatch") ?? Text(payload, "lastStart") ?? Text(payload, "lastStartTime"),
            collectFrom = Text(payload, "collectFrom") ?? Text(payload, "collectionFrom"),
            collectTo = Text(payload, "collectTo") ?? Text(payload, "collectionTo") ?? Text(payload, "collectionDeadline"),
            depotDeadline = Text(payload, "depotDeadline") ?? Text(payload, "deliveryDeadline") ?? Text(payload, "deliveryCutoff") ?? Text(payload, "cutoff"),
            etaMinutes = Text(payload, "etaMinutes") ?? Text(payload, "durationMinutes") ?? Text(payload, "travelMinutes"),
            notes = Text(payload, "notes") ?? Text(payload, "instructions")
        };

        var json = JsonSerializer.Serialize(normalised);
        var key = $"sitetimingrule:{Key(normalised.routeCombination)}:{Key(normalised.palletType)}";
        var existing = await db.StagedImports.FirstOrDefaultAsync(item =>
            item.EntityType == "masterdetail:sitetimingrule" && item.IdempotencyKey == key, ct);

        if (existing is null)
        {
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "masterdetail:sitetimingrule",
                IdempotencyKey = key,
                PayloadJson = json,
                Source = request.Source ?? "Master data CSV · Run Times",
                Status = StagingStatus.Promoted,
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewNote = "Imported as a promoted site timing rule for dispatch/order timing."
            });
        }
        else
        {
            existing.PayloadJson = json;
            existing.Source = request.Source ?? existing.Source;
            existing.Status = StagingStatus.Promoted;
            existing.ReviewedAtUtc = DateTimeOffset.UtcNow;
            existing.ReviewNote = "Updated from Master data CSV · Run Times.";
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool IsSiteTimingRule(string entityType) =>
        entityType.Equals("sitetimingrule", StringComparison.OrdinalIgnoreCase)
        || entityType.Equals("siteTimingRule", StringComparison.OrdinalIgnoreCase)
        || entityType.Equals("site-timing-rule", StringComparison.OrdinalIgnoreCase);

    private static string? JoinRoute(string? collection, string? delivery) =>
        string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(delivery) ? null : $"{collection.Trim()} to {delivery.Trim()}";

    private static string Key(string? value) =>
        new string((value ?? string.Empty).Trim().ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');

    private static string? Text(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => string.IsNullOrWhiteSpace(property.Value.GetString()) ? null : property.Value.GetString()!.Trim(),
                _ => property.Value.ToString()
            };
        }

        return null;
    }

    private static bool IsDatabaseUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not exist or you do not have permissions", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission was denied", StringComparison.OrdinalIgnoreCase);
    }
}
