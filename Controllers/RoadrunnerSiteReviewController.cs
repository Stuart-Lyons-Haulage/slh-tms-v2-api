using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Slh.Tms.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/master-data/roadrunner-site-review")]
[Authorize]
public sealed class RoadrunnerSiteReviewController(TmsDbContext db) : ControllerBase
{
    private const string ReviewType = "masterdata:roadrunner-site-review";

    /// <summary>
    /// Returns the outstanding RoadRunner Site Master proposals.
    /// This endpoint is read-only: it never mutates canonical Site Master data.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPending(CancellationToken ct)
    {
        var rows = await db.StagedImports
            .AsNoTracking()
            .Where(x =>
                x.EntityType == ReviewType &&
                x.Status == StagingStatus.PendingReview)
            .OrderBy(x => x.ReceivedAtUtc)
            .ToListAsync(ct);

        var result = rows.Select(row => new
        {
            row.Id,
            row.IdempotencyKey,
            row.Source,
            row.ReceivedAtUtc,
            row.ReviewNote,
            Proposal = ParsePayload(row.PayloadJson)
        });

        return Ok(result);
    }

    /// <summary>
    /// Lightweight count used by the Master Data review badge.
    /// </summary>
    [HttpGet("count")]
    public async Task<IActionResult> GetPendingCount(CancellationToken ct)
    {
        var count = await db.StagedImports
            .AsNoTracking()
            .CountAsync(x =>
                x.EntityType == ReviewType &&
                x.Status == StagingStatus.PendingReview,
                ct);

        return Ok(new
        {
            count,
            entityType = ReviewType
        });
    }

    /// <summary>
    /// Links a RoadRunner address identity to an existing canonical TMS Site.
    /// Canonical Site fields are deliberately not changed.
    /// </summary>
    [HttpPost("{id:guid}/link")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> LinkOnly(
        Guid id,
        [FromBody] RoadrunnerSiteLinkRequest request,
        CancellationToken ct)
    {
        var review = await db.StagedImports
            .SingleOrDefaultAsync(x =>
                x.Id == id &&
                x.EntityType == ReviewType,
                ct);

        if (review is null)
            return NotFound(new { message = "RoadRunner site review was not found." });

        if (review.Status != StagingStatus.PendingReview)
            return Conflict(new
            {
                message = "This RoadRunner proposal has already been reviewed.",
                status = review.Status.ToString()
            });

        var site = await db.Sites
            .SingleOrDefaultAsync(x => x.Id == request.SiteId, ct);

        if (site is null)
            return NotFound(new { message = "The selected canonical TMS Site was not found." });

        if (!site.Active)
            return Conflict(new { message = "The selected canonical TMS Site is inactive." });

        JsonObject proposal;
        JsonObject roadRunner;

        try
        {
            proposal = JsonNode.Parse(review.PayloadJson)?.AsObject()
                ?? throw new JsonException("Proposal payload is empty.");

            roadRunner = proposal["roadRunner"]?.AsObject()
                ?? throw new JsonException("RoadRunner payload is missing.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Conflict(new
            {
                message = "The stored RoadRunner proposal is invalid and cannot be approved.",
                detail = ex.Message
            });
        }

        var roadRunnerCode = roadRunner["Code"]?.GetValue<string?>()
            ?? roadRunner["code"]?.GetValue<string?>();

        if (string.IsNullOrWhiteSpace(roadRunnerCode))
            return Conflict(new { message = "The RoadRunner proposal does not contain a RoadRunner Code." });

        roadRunnerCode = roadRunnerCode.Trim();

        // RoadrunnerCode is stored in master-detail data, so enrich Sites before
        // checking whether this external identity is already linked elsewhere.
        var sites = await db.Sites.ToListAsync(ct);

        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        var conflictingSite = sites.FirstOrDefault(x =>
            x.Id != site.Id &&
            !string.IsNullOrWhiteSpace(x.RoadrunnerCode) &&
            string.Equals(
                x.RoadrunnerCode.Trim(),
                roadRunnerCode,
                StringComparison.OrdinalIgnoreCase));

        if (conflictingSite is not null)
        {
            return Conflict(new
            {
                message = "This RoadRunner Code is already linked to another canonical TMS Site.",
                roadRunnerCode,
                existingSiteId = conflictingSite.Id,
                existingSiteCode = conflictingSite.ExternalCode,
                existingSiteName = conflictingSite.Name
            });
        }

        var actor = Actor();
        var now = DateTimeOffset.UtcNow;
        var roadRunnerProfileJson = roadRunner.ToJsonString();

        // Persist integration-only fields against the canonical SITE### record.
        // MasterDetailStore merges populated values and does not replace unrelated
        // master-detail fields such as aliases or coordinates.
        var detailPayload = JsonSerializer.Serialize(new
        {
            externalCode = site.ExternalCode,
            roadrunnerCode = roadRunnerCode,
            roadrunnerProfileJson = roadRunnerProfileJson
        });

        await using var transaction = await BeginApprovalTransactionAsync(ct);
        await MasterDetailStore.SaveAsync(
            db,
            "site",
            site.ExternalCode,
            detailPayload,
            "RoadRunner Site Master review",
            actor,
            ct);

        var previousStatus = review.Status;
        review.Status = StagingStatus.Promoted;
        review.ReviewedAtUtc = now;
        review.ReviewedBy = actor;
        review.ReviewNote = string.IsNullOrWhiteSpace(request.Note)
            ? $"RoadRunner {roadRunnerCode} linked to {site.ExternalCode} without changing canonical Site fields."
            : request.Note.Trim();

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = site.Id,
            Action = "RoadrunnerLinked",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new
            {
                source = "RoadRunner Site Master review",
                reviewId = review.Id,
                roadRunnerCode,
                siteId = site.Id,
                siteCode = site.ExternalCode,
                siteName = site.Name,
                action = "link-only",
                canonicalFieldsChanged = false
            })
        });

        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = review.Id,
            EventType = "RoadrunnerLinked",
            PreviousStatus = previousStatus,
            NewStatus = StagingStatus.Promoted,
            PayloadJson = JsonSerializer.Serialize(new
            {
                roadRunnerCode,
                siteId = site.Id,
                siteCode = site.ExternalCode,
                action = "link-only"
            }),
            Note = review.ReviewNote,
            Actor = actor,
            OccurredAtUtc = now
        });

        await db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return Ok(new
        {
            reviewId = review.Id,
            status = review.Status.ToString(),
            action = "link-only",
            roadRunnerCode,
            site = new
            {
                site.Id,
                site.ExternalCode,
                site.Name
            },
            canonicalFieldsChanged = false
        });
    }

    /// <summary>
    /// Dismisses a RoadRunner proposal without changing Site Master data.
    /// </summary>
    [HttpPost("{id:guid}/dismiss")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Dismiss(
        Guid id,
        [FromBody] RoadrunnerSiteDismissRequest? request,
        CancellationToken ct)
    {
        var review = await db.StagedImports
            .SingleOrDefaultAsync(x =>
                x.Id == id &&
                x.EntityType == ReviewType,
                ct);

        if (review is null)
            return NotFound(new { message = "RoadRunner site review was not found." });

        if (review.Status != StagingStatus.PendingReview)
            return Conflict(new
            {
                message = "This RoadRunner proposal has already been reviewed.",
                status = review.Status.ToString()
            });

        var actor = Actor();
        var now = DateTimeOffset.UtcNow;
        var previousStatus = review.Status;

        review.Status = StagingStatus.Rejected;
        review.ReviewedAtUtc = now;
        review.ReviewedBy = actor;
        review.ReviewNote = string.IsNullOrWhiteSpace(request?.Note)
            ? "RoadRunner Site Master proposal dismissed."
            : request!.Note!.Trim();

        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = review.Id,
            EventType = "RoadrunnerDismissed",
            PreviousStatus = previousStatus,
            NewStatus = StagingStatus.Rejected,
            PayloadJson = JsonSerializer.Serialize(new
            {
                action = "dismiss",
                canonicalFieldsChanged = false
            }),
            Note = review.ReviewNote,
            Actor = actor,
            OccurredAtUtc = now
        });

        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            reviewId = review.Id,
            status = review.Status.ToString(),
            action = "dismiss",
            canonicalFieldsChanged = false
        });
    }

    /// <summary>
    /// Links the RoadRunner identity and adds the RoadRunner company name as an
    /// alias of an existing canonical TMS Site. Canonical name/address remain unchanged.
    /// </summary>
    [HttpPost("{id:guid}/add-alias")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> AddAlias(
        Guid id,
        [FromBody] RoadrunnerSiteAliasRequest request,
        CancellationToken ct)
    {
        var review = await GetPendingReview(id, ct);
        if (review is null)
            return NotFound(new { message = "Pending RoadRunner site review was not found." });

        var site = await db.Sites.SingleOrDefaultAsync(x => x.Id == request.SiteId, ct);
        if (site is null)
            return NotFound(new { message = "The selected canonical TMS Site was not found." });

        if (!site.Active)
            return Conflict(new { message = "The selected canonical TMS Site is inactive." });

        if (!TryGetRoadRunner(review, out var roadRunner, out var error))
            return Conflict(new { message = error });

        var roadRunnerCode = Text(roadRunner!, "Code", "code");
        if (string.IsNullOrWhiteSpace(roadRunnerCode))
            return Conflict(new { message = "The RoadRunner proposal does not contain a RoadRunner Code." });

        roadRunnerCode = roadRunnerCode.Trim();

        var sites = await db.Sites.ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        var conflict = sites.FirstOrDefault(x =>
            x.Id != site.Id &&
            !string.IsNullOrWhiteSpace(x.RoadrunnerCode) &&
            string.Equals(x.RoadrunnerCode.Trim(), roadRunnerCode, StringComparison.OrdinalIgnoreCase));

        if (conflict is not null)
            return Conflict(new
            {
                message = "This RoadRunner Code is already linked to another canonical TMS Site.",
                roadRunnerCode,
                existingSiteId = conflict.Id,
                existingSiteCode = conflict.ExternalCode,
                existingSiteName = conflict.Name
            });

        var proposedAlias = string.IsNullOrWhiteSpace(request.Alias)
            ? Text(roadRunner!, "Company", "company")
            : request.Alias.Trim();

        if (string.IsNullOrWhiteSpace(proposedAlias))
            return BadRequest(new { message = "No alias was supplied and the RoadRunner Company field is empty." });

        var aliases = MergeAlias(site.Aliases, proposedAlias);
        var actor = Actor();
        var now = DateTimeOffset.UtcNow;

        var detailPayload = JsonSerializer.Serialize(new
        {
            externalCode = site.ExternalCode,
            aliases,
            roadrunnerCode = roadRunnerCode,
            roadrunnerProfileJson = roadRunner!.ToJsonString()
        });

        await using var transaction = await BeginApprovalTransactionAsync(ct);
        await MasterDetailStore.SaveAsync(
            db, "site", site.ExternalCode, detailPayload,
            "RoadRunner Site Master review", actor, ct);

        var previousStatus = review.Status;
        review.Status = StagingStatus.Promoted;
        review.ReviewedAtUtc = now;
        review.ReviewedBy = actor;
        review.ReviewNote = string.IsNullOrWhiteSpace(request.Note)
            ? $"RoadRunner {roadRunnerCode} linked to {site.ExternalCode}; alias '{proposedAlias}' added."
            : request.Note.Trim();

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = site.Id,
            Action = "RoadrunnerAliasAdded",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new
            {
                source = "RoadRunner Site Master review",
                reviewId = review.Id,
                roadRunnerCode,
                siteCode = site.ExternalCode,
                action = "add-alias",
                aliasAdded = proposedAlias,
                previousAliases = site.Aliases,
                aliases
            })
        });

        AddReviewEvent(
            review, previousStatus, StagingStatus.Promoted,
            "RoadrunnerAliasAdded", actor, review.ReviewNote,
            new { roadRunnerCode, siteId = site.Id, siteCode = site.ExternalCode, aliasAdded = proposedAlias });

        await db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return Ok(new
        {
            reviewId = review.Id,
            status = review.Status.ToString(),
            action = "add-alias",
            roadRunnerCode,
            site = new { site.Id, site.ExternalCode, site.Name },
            aliasAdded = proposedAlias
        });
    }

    /// <summary>
    /// Applies only explicitly selected, allow-listed RoadRunner fields.
    /// RoadRunner identity is linked at the same time.
    /// </summary>
    [HttpPost("{id:guid}/accept-fields")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> AcceptSelectedFields(
        Guid id,
        [FromBody] RoadrunnerSiteAcceptFieldsRequest request,
        CancellationToken ct)
    {
        var review = await GetPendingReview(id, ct);
        if (review is null)
            return NotFound(new { message = "Pending RoadRunner site review was not found." });

        if (request.Fields is null || request.Fields.Count == 0)
            return BadRequest(new { message = "Select at least one field to accept." });

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Name",
            "DriverTextName",
            "CollectionAddress",
            "Aliases",
            "Latitude",
            "Longitude"
        };

        var invalid = request.Fields
            .Where(x => string.IsNullOrWhiteSpace(x) || !allowed.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (invalid.Length > 0)
            return BadRequest(new
            {
                message = "One or more requested fields are not permitted for RoadRunner Master Data approval.",
                invalidFields = invalid,
                allowedFields = allowed.OrderBy(x => x)
            });

        var site = await db.Sites.SingleOrDefaultAsync(x => x.Id == request.SiteId, ct);
        if (site is null)
            return NotFound(new { message = "The selected canonical TMS Site was not found." });

        if (!site.Active)
            return Conflict(new { message = "The selected canonical TMS Site is inactive." });

        if (!TryGetRoadRunner(review, out var roadRunner, out var error))
            return Conflict(new { message = error });

        var roadRunnerCode = Text(roadRunner!, "Code", "code");
        if (string.IsNullOrWhiteSpace(roadRunnerCode))
            return Conflict(new { message = "The RoadRunner proposal does not contain a RoadRunner Code." });

        roadRunnerCode = roadRunnerCode.Trim();

        var sites = await db.Sites.ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        var conflict = sites.FirstOrDefault(x =>
            x.Id != site.Id &&
            !string.IsNullOrWhiteSpace(x.RoadrunnerCode) &&
            string.Equals(x.RoadrunnerCode.Trim(), roadRunnerCode, StringComparison.OrdinalIgnoreCase));

        if (conflict is not null)
            return Conflict(new
            {
                message = "This RoadRunner Code is already linked to another canonical TMS Site.",
                roadRunnerCode,
                existingSiteId = conflict.Id,
                existingSiteCode = conflict.ExternalCode,
                existingSiteName = conflict.Name
            });

        var selected = new HashSet<string>(request.Fields, StringComparer.OrdinalIgnoreCase);

        var rrCompany = Text(roadRunner!, "Company", "company");
        var rrAddress = BuildAddress(roadRunner!);
        var rrLatitude = DecimalValue(roadRunner!, "Latitude", "latitude");
        var rrLongitude = DecimalValue(roadRunner!, "Longitude", "longitude");

        var before = new
        {
            site.Name,
            site.DriverTextName,
            site.CollectionAddress,
            site.Aliases,
            site.Latitude,
            site.Longitude
        };

        if (selected.Contains("Name") && !string.IsNullOrWhiteSpace(rrCompany))
            site.Name = rrCompany.Trim();

        if (selected.Contains("DriverTextName") && !string.IsNullOrWhiteSpace(rrCompany))
            site.DriverTextName = rrCompany.Trim();

        if (selected.Contains("CollectionAddress") && !string.IsNullOrWhiteSpace(rrAddress))
            site.CollectionAddress = rrAddress;

        var aliases = site.Aliases;
        if (selected.Contains("Aliases") && !string.IsNullOrWhiteSpace(rrCompany))
            aliases = MergeAlias(aliases, rrCompany);

        var latitude = selected.Contains("Latitude") && rrLatitude.HasValue
            ? rrLatitude
            : site.Latitude;

        var longitude = selected.Contains("Longitude") && rrLongitude.HasValue
            ? rrLongitude
            : site.Longitude;

        var actor = Actor();
        var now = DateTimeOffset.UtcNow;

        var detailPayload = JsonSerializer.Serialize(new
        {
            externalCode = site.ExternalCode,
            aliases,
            latitude,
            longitude,
            roadrunnerCode = roadRunnerCode,
            roadrunnerProfileJson = roadRunner!.ToJsonString()
        });

        await using var transaction = await BeginApprovalTransactionAsync(ct);
        await MasterDetailStore.SaveAsync(
            db, "site", site.ExternalCode, detailPayload,
            "RoadRunner Site Master review", actor, ct);

        var previousStatus = review.Status;
        review.Status = StagingStatus.Promoted;
        review.ReviewedAtUtc = now;
        review.ReviewedBy = actor;
        review.ReviewNote = string.IsNullOrWhiteSpace(request.Note)
            ? $"Selected RoadRunner fields accepted for {site.ExternalCode}: {string.Join(", ", selected)}."
            : request.Note.Trim();

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = site.Id,
            Action = "RoadrunnerSelectedFieldsAccepted",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new
            {
                source = "RoadRunner Site Master review",
                reviewId = review.Id,
                roadRunnerCode,
                siteCode = site.ExternalCode,
                action = "accept-selected-fields",
                selectedFields = selected.OrderBy(x => x).ToArray(),
                before,
                accepted = new
                {
                    site.Name,
                    site.DriverTextName,
                    site.CollectionAddress,
                    aliases,
                    latitude,
                    longitude
                }
            })
        });

        AddReviewEvent(
            review, previousStatus, StagingStatus.Promoted,
            "RoadrunnerFieldsAccepted", actor, review.ReviewNote,
            new
            {
                roadRunnerCode,
                siteId = site.Id,
                siteCode = site.ExternalCode,
                selectedFields = selected.OrderBy(x => x).ToArray()
            });

        await db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return Ok(new
        {
            reviewId = review.Id,
            status = review.Status.ToString(),
            action = "accept-selected-fields",
            roadRunnerCode,
            site = new { site.Id, site.ExternalCode, site.Name },
            selectedFields = selected.OrderBy(x => x).ToArray()
        });
    }

    /// <summary>
    /// Creates a new canonical TMS Site only after explicit approval.
    /// RoadRunner Code is stored as integration identity, never as ExternalCode.
    /// </summary>
    [HttpPost("{id:guid}/create-site")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> CreateSite(
        Guid id,
        [FromBody] RoadrunnerSiteCreateRequest? request,
        CancellationToken ct)
    {
        var review = await GetPendingReview(id, ct);
        if (review is null)
            return NotFound(new { message = "Pending RoadRunner site review was not found." });

        if (!TryGetRoadRunner(review, out var roadRunner, out var error))
            return Conflict(new { message = error });

        var roadRunnerCode = Text(roadRunner!, "Code", "code");
        if (string.IsNullOrWhiteSpace(roadRunnerCode))
            return Conflict(new { message = "The RoadRunner proposal does not contain a RoadRunner Code." });

        roadRunnerCode = roadRunnerCode.Trim();

        var existingSites = await db.Sites.ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, existingSites, ct);

        var alreadyLinked = existingSites.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.RoadrunnerCode) &&
            string.Equals(x.RoadrunnerCode.Trim(), roadRunnerCode, StringComparison.OrdinalIgnoreCase));

        if (alreadyLinked is not null)
            return Conflict(new
            {
                message = "This RoadRunner Code is already linked to a canonical TMS Site.",
                roadRunnerCode,
                existingSiteId = alreadyLinked.Id,
                existingSiteCode = alreadyLinked.ExternalCode,
                existingSiteName = alreadyLinked.Name
            });

        var rrCompany = Text(roadRunner!, "Company", "company");
        var rrAddress = BuildAddress(roadRunner!);

        var name = !string.IsNullOrWhiteSpace(request?.Name)
            ? request!.Name!.Trim()
            : rrCompany?.Trim();

        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "A canonical Site name is required." });

        var driverTextName = !string.IsNullOrWhiteSpace(request?.DriverTextName)
            ? request!.DriverTextName!.Trim()
            : name;

        var collectionAddress = !string.IsNullOrWhiteSpace(request?.CollectionAddress)
            ? request!.CollectionAddress!.Trim()
            : rrAddress;

        var actor = Actor();
        var now = DateTimeOffset.UtcNow;

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        // Re-read the canonical codes inside the serializable transaction.
        var codes = await db.Sites
            .AsNoTracking()
            .Select(x => x.ExternalCode)
            .ToListAsync(ct);

        var externalCode = NextSiteCode(codes);

        var site = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = externalCode,
            Name = name,
            DriverTextName = driverTextName,
            CollectionAddress = collectionAddress,
            Active = true
        };

        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);

        var aliases = !string.IsNullOrWhiteSpace(request?.Alias)
            ? request!.Alias!.Trim()
            : null;

        var latitude = DecimalValue(roadRunner!, "Latitude", "latitude");
        var longitude = DecimalValue(roadRunner!, "Longitude", "longitude");

        var detailPayload = JsonSerializer.Serialize(new
        {
            externalCode = site.ExternalCode,
            aliases,
            latitude,
            longitude,
            roadrunnerCode = roadRunnerCode,
            roadrunnerProfileJson = roadRunner!.ToJsonString()
        });

        await MasterDetailStore.SaveAsync(
            db, "site", site.ExternalCode, detailPayload,
            "RoadRunner Site Master review", actor, ct);

        var previousStatus = review.Status;
        review.Status = StagingStatus.Promoted;
        review.ReviewedAtUtc = now;
        review.ReviewedBy = actor;
        review.ReviewNote = string.IsNullOrWhiteSpace(request?.Note)
            ? $"New canonical Site {site.ExternalCode} created from approved RoadRunner proposal {roadRunnerCode}."
            : request!.Note!.Trim();

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Site",
            EntityId = site.Id,
            Action = "CreatedFromRoadrunnerReview",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new
            {
                source = "RoadRunner Site Master review",
                reviewId = review.Id,
                roadRunnerCode,
                action = "create-site",
                site = new
                {
                    site.Id,
                    site.ExternalCode,
                    site.Name,
                    site.DriverTextName,
                    site.CollectionAddress
                }
            })
        });

        AddReviewEvent(
            review, previousStatus, StagingStatus.Promoted,
            "RoadrunnerSiteCreated", actor, review.ReviewNote,
            new { roadRunnerCode, siteId = site.Id, siteCode = site.ExternalCode });

        await db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return Ok(new
        {
            reviewId = review.Id,
            status = review.Status.ToString(),
            action = "create-site",
            roadRunnerCode,
            site = new
            {
                site.Id,
                site.ExternalCode,
                site.Name,
                site.DriverTextName,
                site.CollectionAddress
            }
        });
    }

    private async Task<StagedImport?> GetPendingReview(Guid id, CancellationToken ct) =>
        await db.StagedImports.SingleOrDefaultAsync(x =>
            x.Id == id &&
            x.EntityType == ReviewType &&
            x.Status == StagingStatus.PendingReview,
            ct);

    private async Task<IDbContextTransaction?> BeginApprovalTransactionAsync(CancellationToken ct) =>
        db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

    private static bool TryGetRoadRunner(
        StagedImport review,
        out JsonObject? roadRunner,
        out string? error)
    {
        roadRunner = null;
        error = null;

        try
        {
            var proposal = JsonNode.Parse(review.PayloadJson)?.AsObject();
            roadRunner = proposal?["roadRunner"]?.AsObject();

            if (roadRunner is null)
            {
                error = "The stored RoadRunner proposal does not contain RoadRunner data.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            error = $"The stored RoadRunner proposal is invalid: {ex.Message}";
            return false;
        }
    }

    private static string? Text(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj[name] is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static decimal? DecimalValue(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj[name] is not JsonValue value)
                continue;

            if (value.TryGetValue<decimal>(out var number))
                return number;

            if (value.TryGetValue<string>(out var text) &&
                decimal.TryParse(
                    text,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out number))
                return number;
        }

        return null;
    }

    private static string? BuildAddress(JsonObject roadRunner)
    {
        var parts = new[]
        {
            Text(roadRunner, "Add1", "add1"),
            Text(roadRunner, "Add2", "add2"),
            Text(roadRunner, "Add3", "add3"),
            Text(roadRunner, "AddTown", "addTown"),
            Text(roadRunner, "AddCounty", "addCounty"),
            Text(roadRunner, "AddPostcode", "addPostcode"),
            Text(roadRunner, "AddCountry", "addCountry")
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase);

        var address = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(address) ? null : address;
    }

    private static string MergeAlias(string? existing, string alias)
    {
        var aliases = (existing ?? string.Empty)
            .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!aliases.Any(x => string.Equals(x, alias, StringComparison.OrdinalIgnoreCase)))
            aliases.Add(alias.Trim());

        return string.Join("; ", aliases);
    }

    private static string NextSiteCode(IEnumerable<string> existingCodes)
    {
        var used = new HashSet<int>();

        foreach (var code in existingCodes)
        {
            if (string.IsNullOrWhiteSpace(code))
                continue;

            var value = code.Trim();

            if (!value.StartsWith("SITE", StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = value[4..];

            if (int.TryParse(suffix, out var number) && number > 0)
                used.Add(number);
        }

        var next = 1;
        while (used.Contains(next))
            next++;

        return $"SITE{next:D3}";
    }

    private void AddReviewEvent(
        StagedImport review,
        StagingStatus previousStatus,
        StagingStatus newStatus,
        string eventType,
        string actor,
        string? note,
        object payload)
    {
        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = review.Id,
            EventType = eventType,
            PreviousStatus = previousStatus,
            NewStatus = newStatus,
            PayloadJson = JsonSerializer.Serialize(payload),
            Note = note,
            Actor = actor,
            OccurredAtUtc = DateTimeOffset.UtcNow
        });
    }

    private string Actor() =>
        User.FindFirst("preferred_username")?.Value
        ?? User.Identity?.Name
        ?? "unknown";

    private static JsonNode? ParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;

        try
        {
            return JsonNode.Parse(payloadJson);
        }
        catch
        {
            // A malformed historical proposal must not break the whole review queue.
            return new JsonObject
            {
                ["invalidPayload"] = true
            };
        }
    }
}


public sealed record RoadrunnerSiteLinkRequest(
    Guid SiteId,
    string? Note);

public sealed record RoadrunnerSiteDismissRequest(
    string? Note);


public sealed record RoadrunnerSiteAliasRequest(
    Guid SiteId,
    string? Alias,
    string? Note);

public sealed record RoadrunnerSiteAcceptFieldsRequest(
    Guid SiteId,
    IReadOnlyCollection<string> Fields,
    string? Note);

public sealed record RoadrunnerSiteCreateRequest(
    string? Name,
    string? DriverTextName,
    string? CollectionAddress,
    string? Alias,
    string? Note);
