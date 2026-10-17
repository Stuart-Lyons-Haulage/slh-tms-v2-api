using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/master-documents"), Authorize(Policy = "TmsWrite")]
public sealed class MasterDocumentsController(TmsDbContext db) : ControllerBase
{
    private const string StoreType = "masterdocument";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [HttpGet("{entityType}/{entityId:guid}")]
    public async Task<IActionResult> List(string entityType, Guid entityId, [FromQuery] bool includeArchived = false, CancellationToken ct = default)
    {
        var normalizedType = NormalizeEntityType(entityType);
        if (normalizedType is null) return BadRequest(new { code = "MASTER_DOCUMENT_ENTITY_TYPE", message = "Entity type must be Site, Customer or Driver." });
        if (!await EntityExists(normalizedType, entityId, ct)) return NotFound();

        var prefix = $"masterdocument:{normalizedType}:{entityId:N}:";
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == StoreType && x.IdempotencyKey.StartsWith(prefix))
            .OrderByDescending(x => x.ReceivedAtUtc)
            .ToListAsync(ct);

        var documents = rows.Select(Parse).Where(x => x is not null).Cast<MasterDocumentState>()
            .Where(x => includeArchived || x.Active)
            .OrderBy(x => x.DocumentType).ThenBy(x => x.FileName)
            .ToArray();
        return Ok(documents);
    }

    [HttpPost("{entityType}/{entityId:guid}")]
    public async Task<IActionResult> Add(string entityType, Guid entityId, MasterDocumentRequest request, CancellationToken ct)
    {
        var normalizedType = NormalizeEntityType(entityType);
        if (normalizedType is null) return BadRequest(new { code = "MASTER_DOCUMENT_ENTITY_TYPE", message = "Entity type must be Site, Customer or Driver." });
        if (!await EntityExists(normalizedType, entityId, ct)) return NotFound();
        if (!ValidHttpsUrl(request.StorageUrl)) return BadRequest(new { code = "MASTER_DOCUMENT_URL", message = "Use an HTTPS SharePoint or OneDrive document link." });
        if (string.IsNullOrWhiteSpace(request.FileName)) return BadRequest(new { code = "MASTER_DOCUMENT_FILENAME", message = "File name is required." });

        var now = DateTimeOffset.UtcNow;
        var state = new MasterDocumentState(
            Guid.NewGuid(), normalizedType, entityId, Clip(request.FileName, 260)!, Clip(request.DocumentType, 120) ?? "Other",
            Clip(request.Description, 1000), request.StorageUrl.Trim(), Clip(request.StorageItemId, 300), request.ExpiryOrReviewDate,
            true, now, User.Identity?.Name, now, User.Identity?.Name);
        await Save(state, ct);
        return Ok(state);
    }

    [HttpPut("{documentId:guid}")]
    public async Task<IActionResult> Update(Guid documentId, MasterDocumentRequest request, CancellationToken ct)
    {
        var row = await FindRow(documentId, ct);
        if (row is null) return NotFound();
        var current = Parse(row);
        if (current is null) return NotFound();
        if (!ValidHttpsUrl(request.StorageUrl)) return BadRequest(new { code = "MASTER_DOCUMENT_URL", message = "Use an HTTPS SharePoint or OneDrive document link." });
        if (string.IsNullOrWhiteSpace(request.FileName)) return BadRequest(new { code = "MASTER_DOCUMENT_FILENAME", message = "File name is required." });

        var updated = current with
        {
            FileName = Clip(request.FileName, 260)!,
            DocumentType = Clip(request.DocumentType, 120) ?? "Other",
            Description = Clip(request.Description, 1000),
            StorageUrl = request.StorageUrl.Trim(),
            StorageItemId = Clip(request.StorageItemId, 300),
            ExpiryOrReviewDate = request.ExpiryOrReviewDate,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedBy = User.Identity?.Name
        };
        await Save(updated, ct, row);
        return Ok(updated);
    }

    [HttpPost("{documentId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid documentId, CancellationToken ct)
    {
        var row = await FindRow(documentId, ct);
        if (row is null) return NotFound();
        var current = Parse(row);
        if (current is null) return NotFound();
        var updated = current with { Active = false, UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = User.Identity?.Name };
        await Save(updated, ct, row);
        return Ok(updated);
    }

    [HttpPost("{documentId:guid}/restore")]
    public async Task<IActionResult> Restore(Guid documentId, CancellationToken ct)
    {
        var row = await FindRow(documentId, ct);
        if (row is null) return NotFound();
        var current = Parse(row);
        if (current is null) return NotFound();
        var updated = current with { Active = true, UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = User.Identity?.Name };
        await Save(updated, ct, row);
        return Ok(updated);
    }

    private async Task<bool> EntityExists(string entityType, Guid id, CancellationToken ct) => entityType switch
    {
        "Site" => await db.Sites.AsNoTracking().AnyAsync(x => x.Id == id, ct),
        "Customer" => await db.Customers.AsNoTracking().AnyAsync(x => x.Id == id, ct),
        "Driver" => await db.Drivers.AsNoTracking().AnyAsync(x => x.Id == id, ct),
        _ => false
    };

    private async Task<StagedImport?> FindRow(Guid documentId, CancellationToken ct)
    {
        var suffix = $":{documentId:N}";
        return await db.StagedImports.SingleOrDefaultAsync(x => x.EntityType == StoreType && x.IdempotencyKey.EndsWith(suffix), ct);
    }

    private async Task Save(MasterDocumentState state, CancellationToken ct, StagedImport? row = null)
    {
        row ??= await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == Key(state), ct);
        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = StoreType,
                IdempotencyKey = Key(state),
                PayloadJson = "{}",
                Source = "Master Data document library",
                ReceivedAtUtc = state.CreatedAtUtc
            };
            db.StagedImports.Add(row);
        }
        row.PayloadJson = JsonSerializer.Serialize(state, JsonOptions);
        row.Status = state.Active ? StagingStatus.Promoted : StagingStatus.Archived;
        row.ReviewedAtUtc = state.UpdatedAtUtc;
        row.ReviewedBy = state.UpdatedBy;
        row.ReviewNote = state.Active ? "Document library metadata active." : "Document library metadata archived; SharePoint/OneDrive file retained.";
        await db.SaveChangesAsync(ct);
    }

    private static string Key(MasterDocumentState state) => $"masterdocument:{state.EntityType}:{state.EntityId:N}:{state.Id:N}";
    private static MasterDocumentState? Parse(StagedImport row)
    {
        try { return JsonSerializer.Deserialize<MasterDocumentState>(row.PayloadJson, JsonOptions); }
        catch (JsonException) { return null; }
    }
    private static string? NormalizeEntityType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "site" or "sites" => "Site",
        "customer" or "customers" => "Customer",
        "driver" or "drivers" => "Driver",
        _ => null
    };
    private static bool ValidHttpsUrl(string? value) => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];

    public sealed record MasterDocumentRequest(string FileName, string? DocumentType, string? Description, string StorageUrl, string? StorageItemId, DateOnly? ExpiryOrReviewDate);
    public sealed record MasterDocumentState(Guid Id, string EntityType, Guid EntityId, string FileName, string DocumentType, string? Description,
        string StorageUrl, string? StorageItemId, DateOnly? ExpiryOrReviewDate, bool Active, DateTimeOffset CreatedAtUtc, string? CreatedBy,
        DateTimeOffset UpdatedAtUtc, string? UpdatedBy);
}
