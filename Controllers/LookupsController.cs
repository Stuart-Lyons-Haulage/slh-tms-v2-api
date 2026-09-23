using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Controllers;
[ApiController, Route("api/v1")]
[Authorize]
public sealed class LookupsController(TmsDbContext db, ILogger<LookupsController> logger) : ControllerBase
{
    [HttpGet("customers")] public async Task<IActionResult> Customers([FromQuery] string? q, CancellationToken ct) => Ok(await db.Customers.AsNoTracking().Where(x => x.Active && (q == null || x.Code.Contains(q) || x.Name.Contains(q))).OrderBy(x => x.Name).Take(5000).ToListAsync(ct));
    [HttpGet("customer-contacts")] public async Task<IActionResult> CustomerContacts([FromQuery] string? q, CancellationToken ct) => Ok(await db.CustomerContacts.AsNoTracking().Where(x => x.Active && (q == null || x.CustomerCode.Contains(q) || x.Name.Contains(q) || (x.Email != null && x.Email.Contains(q)))).OrderBy(x => x.CustomerCode).ThenBy(x => x.Name).Take(5000).ToListAsync(ct));
    [HttpGet("vehicles")] public async Task<IActionResult> Vehicles([FromQuery] string? q, CancellationToken ct)
    {
        var rows = await db.Vehicles.AsNoTracking().Where(x => x.Active && (q == null || x.Registration.Contains(q) || (x.FleetNumber != null && x.FleetNumber.Contains(q)))).OrderBy(x => x.Registration).Take(5000).ToListAsync(ct);
        return Ok(rows.Where(vehicle => !Regex.IsMatch(vehicle.Registration, "^C\\d{5,}$", RegexOptions.IgnoreCase)));
    }
    [HttpGet("drivers")] public async Task<IActionResult> Drivers([FromQuery] string? q, CancellationToken ct)
    {
        var rows = await db.Drivers.AsNoTracking().Where(x => x.Active && (q == null || x.EmployeeNumber.Contains(q) || x.DisplayName.Contains(q))).OrderBy(x => x.DisplayName).Take(5000).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, rows, ct);
        return Ok(rows.Where(DriverPopulationRules.IsDriver)
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.TachoMasterDriverId) ? $"member:{x.TachoMasterDriverId}" :
                          !string.IsNullOrWhiteSpace(x.TachoCardNumber) ? $"card:{x.TachoCardNumber}" : $"employee:{x.EmployeeNumber}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()));
    }
    [HttpGet("trailers")] public async Task<IActionResult> Trailers([FromQuery] string? q, CancellationToken ct) => Ok(await db.Trailers.AsNoTracking().Where(x => x.Active && (q == null || x.TrailerNumber.Contains(q) || (x.Type != null && x.Type.Contains(q)))).OrderBy(x => x.TrailerNumber).Take(5000).ToListAsync(ct));
    [HttpGet("sites")] public async Task<IActionResult> Sites([FromQuery] string? q, CancellationToken ct)
    {
        List<Site> rows;
        try
        {
            rows = await db.Sites.AsNoTracking().Where(x => x.Active && (q == null || x.Name.Contains(q) || (x.DriverTextName != null && x.DriverTextName.Contains(q)))).OrderBy(x => x.Name).Take(5000).ToListAsync(ct);
        }
        catch (Exception ex) when (IsLookupSchemaUnavailable(ex))
        {
            logger.LogWarning(ex, "Site lookup table is unavailable; returning an empty site list so live planning can continue.");
            db.ChangeTracker.Clear();
            return Ok(Array.Empty<Site>());
        }

        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, rows, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Site lookup detail enrichment failed; returning base site records so live planning can continue.");
            db.ChangeTracker.Clear();
        }
        return Ok(rows);
    }

    [HttpPost("sites/roadrunner-master/reconcile"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> ReconcileRoadrunnerSites([FromBody] IReadOnlyList<RoadrunnerSiteProfileRequest> records, CancellationToken ct)
    {
        if (records.Count == 0)
            return Ok(new { received = 0, linked = 0, review = 0, unmatched = 0, results = Array.Empty<object>() });

        if (records.Count > 1000)
            return BadRequest(new { message = "Roadrunner Site Master import is limited to 1,000 rows per request." });

        const string reviewType = "masterdata:roadrunner-site-review";
        const string reviewSource = "Roadrunner Site Master reconciliation";

        var allSites = await db.Sites
            .OrderBy(site => site.Name)
            .ToListAsync(ct);

        await MasterDetailStore.EnrichSitesAsync(db, allSites, ct);
        var sites = allSites.Where(site => site.Active).ToList();

        // Roadrunner reconciliation is proposal-only.
        // It must never mutate canonical Site Master data.
        var existingReviews = await db.StagedImports
            .Where(x => x.EntityType == reviewType)
            .ToListAsync(ct);

        var reviewsByKey = existingReviews
            .GroupBy(x => x.IdempotencyKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var results = new List<object>();
        var linked = 0;
        var review = 0;
        var unmatched = 0;

        void StageReview(
            string roadRunnerCode,
            string company,
            string reason,
            int confidence,
            string proposedAction,
            IEnumerable<Site> candidates,
            RoadrunnerSiteProfileRequest record)
        {
            var normalCode = SiteMasterIdentityResolver.Normalise(roadRunnerCode);
            var key = $"{reviewType}:{normalCode}";
            if (key.Length > 200) key = key[..200];

            var candidateRows = candidates
                .GroupBy(x => x.Id)
                .Select(group => group.First())
                .Select(site => new
                {
                    site.Id,
                    site.ExternalCode,
                    site.Name,
                    site.DriverTextName,
                    site.CollectionAddress,
                    site.RoadrunnerCode
                })
                .ToArray();

            var payload = JsonSerializer.Serialize(new
            {
                reason,
                confidence,
                proposedAction,
                roadRunner = record,
                candidates = candidateRows
            });

            if (!reviewsByKey.TryGetValue(key, out var staged))
            {
                staged = new StagedImport
                {
                    EntityType = reviewType,
                    IdempotencyKey = key,
                    PayloadJson = payload,
                    Status = StagingStatus.PendingReview,
                    Source = reviewSource,
                    ReviewNote = "Roadrunner Site identity requires explicit Master Data approval."
                };

                db.StagedImports.Add(staged);
                reviewsByKey[key] = staged;
            }
            else if (staged.Status == StagingStatus.PendingReview)
            {
                staged.PayloadJson = payload;
                staged.Source ??= reviewSource;

                // Pending proposals are refreshed; historical decisions retain
                // their original evidence and review trail unchanged.
                staged.ReviewedAtUtc = null;
                staged.ReviewedBy = null;
                staged.ReviewNote = "Roadrunner Site identity requires explicit Master Data approval.";
            }
        }

        foreach (var record in records)
        {
            var roadRunnerCode = Clip(record.Code, 80);
            var company = Clip(record.Company, 200);

            if (string.IsNullOrWhiteSpace(roadRunnerCode))
            {
                unmatched++;
                results.Add(new
                {
                    code = record.Code,
                    company,
                    status = "unmatched",
                    confidence = 0,
                    reason = "Roadrunner row has no Code; no persistent link can be created."
                });
                continue;
            }

            var address = string.Join(", ", new[]
            {
                record.Add1,
                record.Add2,
                record.Add3,
                record.AddTown,
                record.AddCounty,
                record.AddPostcode,
                record.AddCountry
            }.Where(value => !string.IsNullOrWhiteSpace(value))
             .Select(value => value!.Trim()));

            var normalRoadrunnerCode = SiteMasterIdentityResolver.Normalise(roadRunnerCode);

            var existingRoadrunnerMatches = allSites
                .Where(site =>
                    !string.IsNullOrWhiteSpace(site.RoadrunnerCode) &&
                    SiteMasterIdentityResolver.Normalise(site.RoadrunnerCode) == normalRoadrunnerCode)
                .ToList();

            // A previously approved one-to-one Roadrunner link is authoritative.
            // Reading it is safe; reconciliation does not refresh or modify the Site.
            if (existingRoadrunnerMatches.Count == 1)
            {
                var site = existingRoadrunnerMatches[0];
                linked++;

                results.Add(new
                {
                    code = roadRunnerCode,
                    company,
                    status = "linked",
                    confidence = 100,
                    reason = "Already explicitly linked by Roadrunner Code.",
                    siteId = site.Id,
                    siteCode = site.ExternalCode,
                    siteName = site.Name
                });

                continue;
            }

            if (existingRoadrunnerMatches.Count > 1)
            {
                review++;

                const string reason = "Roadrunner Code is linked to more than one canonical Site Master record.";

                StageReview(
                    roadRunnerCode,
                    company ?? string.Empty,
                    reason,
                    100,
                    "Resolve duplicate Roadrunner Code links and select one canonical Site.",
                    existingRoadrunnerMatches,
                    record);

                results.Add(new
                {
                    code = roadRunnerCode,
                    company,
                    status = "review",
                    confidence = 100,
                    reason,
                    candidates = existingRoadrunnerMatches
                        .Select(site => new { site.Id, site.ExternalCode, site.Name })
                        .ToArray()
                });

                continue;
            }

            var suggestedCandidates = new List<Site>();
            var confidence = 0;
            var reasonText = string.Empty;
            var proposedAction = "Create new Site or select an existing canonical Site.";

            if (!string.IsNullOrWhiteSpace(record.AddPostcode))
            {
                var postcode = SiteMasterIdentityResolver.Normalise(record.AddPostcode);

                var postcodeMatches = sites
                    .Where(site => SiteMasterIdentityResolver.ExtractPostcode(site.CollectionAddress) == postcode)
                    .ToList();

                if (postcodeMatches.Count == 1)
                {
                    suggestedCandidates.Add(postcodeMatches[0]);
                    confidence = 99;
                    reasonText = "Unique postcode suggests an existing Site Master record.";
                    proposedAction = "Link Roadrunner identity to the suggested canonical Site.";
                }
                else if (postcodeMatches.Count > 1)
                {
                    suggestedCandidates.AddRange(postcodeMatches);
                    confidence = 95;
                    reasonText = "Postcode matches multiple Site Master records.";
                    proposedAction = "Choose the correct canonical Site.";
                }
            }

            SiteIdentityResolution? resolution = null;

            if (suggestedCandidates.Count == 0)
            {
                resolution = SiteMasterIdentityResolver.Resolve(
                    new IncomingSiteIdentity(
                        null,
                        company,
                        company,
                        address,
                        SiteMasterIdentityResolver.MergeAliases(
                            record.Code,
                            record.LookupCode,
                            record.Company,
                            record.AddTown),
                        null),
                    sites);

                if (resolution.Matched && resolution.Site is not null)
                {
                    suggestedCandidates.Add(resolution.Site);
                    confidence = resolution.Confidence;
                    reasonText = resolution.Reason;
                    proposedAction = "Link Roadrunner identity to the suggested canonical Site.";
                }

                if (resolution.PossibleDuplicates.Count > 0)
                {
                    suggestedCandidates.AddRange(resolution.PossibleDuplicates);

                    if (confidence == 0)
                        confidence = resolution.Confidence;

                    if (string.IsNullOrWhiteSpace(reasonText))
                        reasonText = resolution.Reason;

                    if (suggestedCandidates.Select(x => x.Id).Distinct().Count() > 1)
                        proposedAction = "Choose the correct canonical Site.";
                }
            }

            suggestedCandidates = suggestedCandidates
                .GroupBy(site => site.Id)
                .Select(group => group.First())
                .ToList();

            if (suggestedCandidates.Count == 0)
            {
                unmatched++;
                reasonText = "No existing Site Master record could be matched safely.";

                StageReview(
                    roadRunnerCode,
                    company ?? string.Empty,
                    reasonText,
                    confidence,
                    "Review and create a new canonical Site if required.",
                    Array.Empty<Site>(),
                    record);

                results.Add(new
                {
                    code = roadRunnerCode,
                    company,
                    status = "unmatched",
                    confidence,
                    reason = reasonText,
                    reviewCreated = true
                });

                continue;
            }

            review++;

            StageReview(
                roadRunnerCode,
                company ?? string.Empty,
                reasonText,
                confidence,
                proposedAction,
                suggestedCandidates,
                record);

            results.Add(new
            {
                code = roadRunnerCode,
                company,
                status = "review",
                confidence,
                reason = reasonText,
                candidates = suggestedCandidates
                    .Select(site => new { site.Id, site.ExternalCode, site.Name })
                    .ToArray(),
                reviewCreated = true
            });
        }

        // This SaveChanges persists review proposals only.
        // Canonical Sites are deliberately untouched by this endpoint.
        await db.SaveChangesAsync(ct);

        return Ok(new { received = records.Count, linked, review, unmatched, results });
    }

    [HttpGet("market-contacts")] public async Task<IActionResult> MarketContacts([FromQuery] string? q, CancellationToken ct)
    {
        var rows = await db.MarketContacts.AsNoTracking().Where(x => x.Active && (q == null || x.Name.Contains(q) || x.Market.Contains(q))).OrderBy(x => x.Market).ThenBy(x => x.Name).Take(5000).ToListAsync(ct);
        foreach (var row in rows) row.Market = CanonicalMarket(row.Market);
        return Ok(rows.OrderBy(row => row.Market).ThenBy(row => row.Name));
    }

    [HttpPut("market-contacts/{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateMarketContact(Guid id, [FromBody] MarketContactUpdateRequest request, CancellationToken ct)
    {
        var contact = await db.MarketContacts.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (contact is null) return NotFound();
        var market = ClipRequired(request.Market ?? string.Empty, 80);
        var name = ClipRequired(request.Name ?? string.Empty, 200);
        if (string.IsNullOrWhiteSpace(market) || string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Market and seller/sender name are required." });

        var before = JsonSerializer.Serialize(contact);
        contact.MarketKey ??= id.ToString("N");
        contact.Market = market;
        contact.Name = name;
        contact.StandOrLocation = Clip(request.StandOrLocation, 200);
        contact.Salesman = Clip(request.Salesman, 200);
        contact.Sender = Clip(request.Sender, 200);
        contact.Active = request.Active;
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "MarketContact",
            EntityId = id,
            Action = "Updated",
            ChangesJson = JsonSerializer.Serialize(new { before = JsonDocument.Parse(before).RootElement, after = contact }),
            ChangedBy = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "unknown"
        });
        await db.SaveChangesAsync(ct);
        return Ok(contact);
    }

    [HttpPut("vehicles/{id:guid}")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateVehicle(Guid id, [FromBody] LookupVehicleUpdateRequest request, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (vehicle is null) return NotFound();
        var registration = ClipRequired((request.Registration ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant(), 20);
        if (string.IsNullOrWhiteSpace(registration)) return BadRequest(new { message = "Registration is required." });
        if (await db.Vehicles.AnyAsync(item => item.Id != id && item.Registration == registration, ct)) return Conflict(new { message = $"Registration {registration} already exists." });
        vehicle.Registration = registration;
        vehicle.FleetNumber = Clip(request.FleetNumber, 40);
        vehicle.Abbreviation = Clip(request.Abbreviation, 20);
        vehicle.Transmission = Clip(request.Transmission, 20);
        vehicle.DvsCompliant = request.DvsCompliant;
        vehicle.FuelProvider = Clip(request.FuelProvider, 30);
        vehicle.CabMobile = Clip(request.CabMobile, 40);
        vehicle.FuelPin = Clip(request.FuelPin, 80);
        vehicle.ShellCard = Clip(request.ShellCard, 80);
        vehicle.BpRedCard = Clip(request.BpRedCard, 80);
        vehicle.BpPlainCard = Clip(request.BpPlainCard, 80);
        vehicle.Notes = Clip(request.Notes, 500);
        vehicle.FuelPinSecretName = Clip(request.FuelPinSecretName, 120);
        vehicle.FuelCardLastFour = Clip(request.FuelCardLastFour, 4);
        vehicle.Active = request.Active;
        await db.SaveChangesAsync(ct);
        return Ok(vehicle);
    }

    [HttpPut("drivers/{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateDriver(Guid id, [FromBody] LookupDriverUpdateRequest request, CancellationToken ct)
    {
        var driver = await db.Drivers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (driver is null) return NotFound();
        await MasterDetailStore.EnrichDriversAsync(db, new[] { driver }, ct);
        var employeeNumber = ClipRequired(request.EmployeeNumber ?? string.Empty, 40);
        if (string.IsNullOrWhiteSpace(employeeNumber) || string.IsNullOrWhiteSpace(request.DisplayName)) return BadRequest(new { message = "Employee number and display name are required." });
        if (await db.Drivers.AnyAsync(x => x.Id != id && x.EmployeeNumber == employeeNumber, ct)) return Conflict(new { message = $"Employee number {employeeNumber} already exists." });
        driver.EmployeeNumber = employeeNumber; driver.DisplayName = ClipRequired(request.DisplayName, 160); driver.TachoName = Clip(request.TachoName, 160); driver.MobileNumber = Clip(request.MobileNumber, 40); driver.DriverType = Clip(request.DriverType, 80); driver.DriverGroup = Clip(request.DriverGroup, 80); driver.Skills = Clip(request.Skills, 160); driver.Coding = Clip(request.Coding, 80); driver.AgencyName = Clip(request.AgencyName, 160); driver.NorthEligible = request.NorthEligible; driver.PreloadEligible = request.PreloadEligible; driver.Notes = Clip(request.Notes, 500); driver.TachoMasterDriverId = Clip(request.TachoMasterDriverId, 80); driver.DrivingLicenceNumber = Clip(request.DrivingLicenceNumber, 80); driver.LicenceExpiry = request.LicenceExpiry; driver.LicenceStatus = Clip(request.LicenceStatus, 40); driver.Active = request.Active;
        await db.SaveChangesAsync(ct);
        await MasterDetailStore.SaveAsync(db, "driver", employeeNumber, JsonSerializer.Serialize(driver), "SLH driver editor", User.Identity?.Name, ct);
        return Ok(driver);
    }

    [HttpPut("customers/{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateCustomer(Guid id, [FromBody] LookupCustomerUpdateRequest request, CancellationToken ct)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (customer is null) return NotFound();
        var code = ClipRequired(request.Code ?? string.Empty, 40).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { message = "Customer code and name are required." });
        if (await db.Customers.AnyAsync(x => x.Id != id && x.Code == code, ct)) return Conflict(new { message = $"Customer code {code} already exists." });
        customer.Code = code; customer.Name = ClipRequired(request.Name, 200); customer.Active = request.Active;
        await db.SaveChangesAsync(ct);
        return Ok(customer);
    }

    [HttpPut("customer-contacts/{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateCustomerContact(Guid id, [FromBody] CustomerContactUpdateRequest request, CancellationToken ct)
    {
        var contact = await db.CustomerContacts.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (contact is null) return NotFound();
        var customerCode = ClipRequired(request.CustomerCode ?? string.Empty, 40).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(customerCode) || string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { message = "Customer code and contact name are required." });
        if (!await db.Customers.AnyAsync(x => x.Code == customerCode, ct)) return BadRequest(new { message = $"Customer {customerCode} does not exist." });
        contact.CustomerCode = customerCode; contact.Name = ClipRequired(request.Name, 200); contact.Email = Clip(request.Email, 320)?.ToLowerInvariant(); contact.MobileNumber = Clip(request.MobileNumber, 40); contact.ReceivesEtaUpdates = request.ReceivesEtaUpdates; contact.Active = request.Active;
        await db.SaveChangesAsync(ct);
        return Ok(contact);
    }

    [HttpPut("trailers/{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateTrailer(Guid id, [FromBody] LookupTrailerUpdateRequest request, CancellationToken ct)
    {
        var trailer = await db.Trailers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (trailer is null) return NotFound();
        var number = ClipRequired(request.TrailerNumber ?? string.Empty, 40);
        if (string.IsNullOrWhiteSpace(number)) return BadRequest(new { message = "Trailer number is required." });
        if (await db.Trailers.AnyAsync(x => x.Id != id && x.TrailerNumber == number, ct)) return Conflict(new { message = $"Trailer {number} already exists." });
        trailer.TrailerNumber = number; trailer.Type = Clip(request.Type, 80); trailer.StandardCapacity = request.StandardCapacity; trailer.EuroCapacity = request.EuroCapacity; trailer.Notes = Clip(request.Notes, 500); trailer.Active = request.Active;
        await db.SaveChangesAsync(ct); return Ok(trailer);
    }

    [HttpPut("sites/{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateSite(Guid id, [FromBody] LookupSiteUpdateRequest request, CancellationToken ct)
    {
        var site = await db.Sites.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (site is null) return NotFound();
        var code = ClipRequired(request.ExternalCode ?? string.Empty, 40);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { message = "Site code and name are required." });
        if (await db.Sites.AnyAsync(x => x.Id != id && x.ExternalCode == code, ct)) return Conflict(new { message = $"Site code {code} already exists." });
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180) return BadRequest(new { message = "Map point is outside the valid latitude/longitude range." });
        site.ExternalCode = code; site.Name = ClipRequired(request.Name, 200); site.DriverTextName = Clip(request.DriverTextName, 200); site.Aliases = Clip(request.Aliases, 500); site.CollectionAddress = Clip(request.CollectionAddress, 500); site.CollectionInstructions = Clip(request.CollectionInstructions, 1000); site.MapLink = Clip(request.MapLink, 1000); site.Latitude = request.Latitude; site.Longitude = request.Longitude; site.CustomField1 = Clip(request.CustomField1, 200); site.CustomField2 = Clip(request.CustomField2, 200); site.CustomField3 = Clip(request.CustomField3, 200); site.RoadrunnerCode = Clip(request.RoadrunnerCode, 80); site.RoadrunnerProfileJson = request.RoadrunnerProfileJson; site.Active = request.Active;
        await db.SaveChangesAsync(ct);
        await MasterDetailStore.SaveAsync(db, "site", code, JsonSerializer.Serialize(site), "SLH site editor", User.Identity?.Name, ct);
        return Ok(site);
    }

    private static string? Clip(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
    private static string ClipRequired(string value, int maxLength) => value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
    private static bool IsLookupSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
    private static string CanonicalMarket(string value)
    {
        var normal = new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (normal.Contains("covent")) return "Covent";
        if (normal.Contains("spit")) return "Spit";
        if (normal.Contains("western")) return "Western";
        if (normal.Contains("sender")) return "Sender";
        return string.IsNullOrWhiteSpace(value) ? "General" : value.Trim();
    }
}

public sealed record LookupVehicleUpdateRequest(string Registration, string? FleetNumber, string? Abbreviation, string? Transmission, bool? DvsCompliant, string? FuelProvider, string? CabMobile, string? FuelPin, string? ShellCard, string? BpRedCard, string? BpPlainCard, string? Notes, string? FuelPinSecretName, string? FuelCardLastFour, bool Active);
public sealed record LookupDriverUpdateRequest(string? EmployeeNumber, string? DisplayName, string? TachoName, string? MobileNumber, string? DriverType, string? DriverGroup, string? Skills, string? Coding, string? AgencyName, bool? NorthEligible, bool? PreloadEligible, string? Notes, string? TachoMasterDriverId, string? DrivingLicenceNumber, DateOnly? LicenceExpiry, string? LicenceStatus, bool Active);
public sealed record LookupCustomerUpdateRequest(string? Code, string? Name, bool Active);
public sealed record CustomerContactUpdateRequest(string? CustomerCode, string? Name, string? Email, string? MobileNumber, bool ReceivesEtaUpdates, bool Active);
public sealed record LookupTrailerUpdateRequest(string? TrailerNumber, string? Type, int? StandardCapacity, int? EuroCapacity, string? Notes, bool Active);
public sealed record LookupSiteUpdateRequest(string? ExternalCode, string? Name, string? DriverTextName, string? Aliases, string? CollectionAddress, string? CollectionInstructions, string? MapLink, decimal? Latitude, decimal? Longitude, string? CustomField1, string? CustomField2, string? CustomField3, string? RoadrunnerCode, string? RoadrunnerProfileJson, bool Active);

public sealed record RoadrunnerSiteProfileRequest(
    string? Code,
    string? LookupCode,
    string? CompanyLetter,
    string? Company,
    string? Add1,
    string? Add2,
    string? Add3,
    string? AddTown,
    string? AddCounty,
    string? AddPostcode,
    string? AddCountry,
    decimal? Latitude,
    decimal? Longitude,
    string? Contact1,
    string? Contact2,
    string? Telephone,
    string? Fax,
    string? Email,
    string? CollectTimeFrom1,
    string? CollectTimeTo1,
    string? CollectTimeFrom2,
    string? CollectTimeTo2,
    string? DeliverTimeFrom1,
    string? DeliverTimeTo1,
    string? DeliverTimeFrom2,
    string? DeliverTimeTo2,
    string? CollectTurnaround,
    string? CollectTurnaroundPerPallet,
    string? DeliverTurnaround,
    string? DeliverTurnaroundPerPallet,
    string? VehicleType,
    bool? TailLiftRequired,
    string? GridRef,
    string? RateArea,
    bool? BookingRequired,
    string? VanRouteName);
public sealed record MarketContactUpdateRequest(string? Market, string? Name, string? StandOrLocation, string? Salesman, string? Sender, bool Active);
