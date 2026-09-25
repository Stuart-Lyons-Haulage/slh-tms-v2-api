using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExcelDataReader;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/master-data/workbook")]
public sealed class MasterDataWorkbookImportController(TmsDbContext db, StagingService staging, ILogger<MasterDataWorkbookImportController> logger) : ControllerBase
{
    [HttpPost("preview")]
    [RequestSizeLimit(30_000_000)]
    public async Task<IActionResult> Preview([FromForm] IFormFile file, CancellationToken ct)
    {
        return Ok(await ProcessAsync(file, commit: false, ct));
    }

    [HttpPost("commit")]
    [RequestSizeLimit(30_000_000)]
    public async Task<IActionResult> Commit([FromForm] IFormFile file, CancellationToken ct)
    {
        return Ok(await ProcessAsync(file, commit: true, ct));
    }

    private async Task<WorkbookImportResult> ProcessAsync(IFormFile file, bool commit, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new InvalidOperationException("Upload a populated master-data workbook.");

        var workbook = await ReadWorkbookAsync(file, ct);
        var result = new WorkbookImportResult(commit ? "commit" : "preview");

        await ProcessSitesAsync(workbook, result, commit, ct);
        await ProcessPlannerListsAsync(workbook, result, commit, ct);
        await ProcessCustomerContactsAsync(workbook, result, commit, ct);
        await ProcessMarketContactsAsync(workbook, result, commit, ct);
        await ProcessSiteCutoffsAsync(workbook, result, commit, ct);
        await ProcessRunTimesAsync(workbook, result, commit, ct);
        await ProcessVehiclesAsync(workbook, result, commit, ct);
        await ProcessFuelPricesAsync(workbook, result, commit, ct);
        await ProcessDriversAsync(workbook, result, commit, ct);

        result.Warnings.Add("Drivers are update-only from this workbook. TachoMaster remains the authority for driver identity and live tacho readings.");
        result.Warnings.Add("Vehicles are update-only from this workbook. Fleet remains the authority for vehicle identity and registrations.");
        result.Warnings.Add("Sites with weak or conflicting matches are held for review and are not created during commit.");
        result.Warnings.Add("Collection Sites, Customers For Deliveries, Site Cutoffs and Run Times are imported as master detail records for intake/planner matching.");
        result.Warnings.Add("Timing rules now retain latestCollectionTime for wall boards. Dispatch can still set an earlier planned start; once the first geofence is hit live ETA/ETO takes over for downstream stops.");
        return result;
    }

    private async Task ProcessSitesAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => SheetIs(sheet.Key, "sites")).SelectMany(sheet => sheet.Value).ToList();
        if (rows.Count == 0) return;

        var liveSites = await db.Sites.ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, liveSites, ct);

        foreach (var row in rows)
        {
            var externalCode = row.Text("siteid", "site id", "sitecode", "site code", "externalcode", "external code");
            var name = row.Text("site", "sitename", "site name", "name", "delivery name", "customer delivery name");
            var driverText = row.Text("driver text name", "drivertextname", "driver name", "driver facing name");
            var address = row.Text("collection address", "collection addresses", "address", "delivery address", "physical address", "site address");
            var mapLink = row.Text("map link", "maplink", "google maps", "maps");
            var aliases = row.Text("aliases", "alias", "delivery names", "collection names");
            var instructions = row.Text("collection notes", "collection instructions", "instructions", "notes");
            var active = row.Bool("active") ?? true;

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(externalCode)) continue;

            var identity = new IncomingSiteIdentity(externalCode, name, driverText, address, aliases, mapLink);
            var resolution = SiteMasterIdentityResolver.Resolve(identity, liveSites);
            var detail = new WorkbookRowResult("Sites", row.RowNumber, name ?? externalCode ?? "site", resolution.Outcome, resolution.Reason, resolution.Confidence);
            if (resolution.PossibleDuplicates.Count > 0)
                detail.RelatedRecords.AddRange(resolution.PossibleDuplicates.Select(site => $"{site.ExternalCode} - {site.Name}"));

            if (commit && resolution.Matched)
            {
                var site = resolution.Site!;
                site.CustomerCode = row.Text("customer code", "customercode") ?? site.CustomerCode;
                site.Name = string.IsNullOrWhiteSpace(name) ? site.Name : name.Trim();
                site.DriverTextName = driverText ?? site.DriverTextName;
                site.CollectionAddress = address ?? site.CollectionAddress;
                site.CollectionInstructions = instructions ?? site.CollectionInstructions;
                site.MapLink = mapLink ?? site.MapLink;
                site.Aliases = SiteMasterIdentityResolver.MergeAliases(site.Aliases, aliases, name, driverText);
                site.Active = active;
                await SaveMasterDetailAsync("site", site.ExternalCode, new
                {
                    externalCode = site.ExternalCode,
                    name = site.Name,
                    driverTextName = site.DriverTextName,
                    collectionAddress = site.CollectionAddress,
                    collectionInstructions = site.CollectionInstructions,
                    mapLink = site.MapLink,
                    aliases = site.Aliases,
                    active = site.Active,
                    addressCheck = row.Text("address check", "address status", "credential check"),
                    sourceWorkbookSheet = row.SheetName,
                    sourceWorkbookRow = row.RowNumber
                }, "SLH master workbook safe import", ct);
                detail.ActionTaken = "updated existing live site";
            }
            else if (commit && resolution.CanCreate)
            {
                var site = new Site
                {
                    ExternalCode = string.IsNullOrWhiteSpace(externalCode) ? $"SITE-{Guid.NewGuid():N}"[..13].ToUpperInvariant() : externalCode.Trim(),
                    Name = name!.Trim(),
                    DriverTextName = driverText,
                    CollectionAddress = address,
                    CollectionInstructions = instructions,
                    MapLink = mapLink,
                    Aliases = SiteMasterIdentityResolver.MergeAliases(aliases, name, driverText),
                    Active = active,
                    CustomerCode = row.Text("customer code", "customercode")
                };
                db.Sites.Add(site);
                liveSites.Add(site);
                await SaveMasterDetailAsync("site", site.ExternalCode, new
                {
                    externalCode = site.ExternalCode,
                    name = site.Name,
                    driverTextName = site.DriverTextName,
                    collectionAddress = site.CollectionAddress,
                    collectionInstructions = site.CollectionInstructions,
                    mapLink = site.MapLink,
                    aliases = site.Aliases,
                    active = site.Active,
                    addressCheck = row.Text("address check", "address status", "credential check"),
                    sourceWorkbookSheet = row.SheetName,
                    sourceWorkbookRow = row.RowNumber
                }, "SLH master workbook safe import", ct);
                detail.ActionTaken = "created new site";
            }
            else if (resolution.RequiresReview)
            {
                detail.ActionTaken = commit ? "held for review - not written" : "would hold for review";
            }
            else detail.ActionTaken = commit ? "no write" : "would update/create";

            result.Rows.Add(detail);
        }

        if (commit) await db.SaveChangesAsync(ct);
    }

    private async Task ProcessPlannerListsAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var listDefinitions = new[]
        {
            new { Sheet = "Collection Sites", Entity = "plannercollectionsite", Header = "collection sites", Field = "collectionSite" },
            new { Sheet = "Customers For Deliveries", Entity = "plannerdeliverypoint", Header = "deliveries", Field = "deliveryPoint" }
        };

        foreach (var definition in listDefinitions)
        {
            var rows = workbook.Sheets.Where(sheet => SheetIs(sheet.Key, definition.Sheet)).SelectMany(sheet => sheet.Value).ToList();
            foreach (var row in rows)
            {
                var value = row.Text(definition.Header, definition.Field, "name", "site", "customer", "delivery", "collection");
                if (string.IsNullOrWhiteSpace(value) || value.Equals("Customers", StringComparison.OrdinalIgnoreCase)) continue;
                var key = Canonical(value);
                var payload = new
                {
                    key,
                    name = value.Trim(),
                    sourceWorkbookSheet = row.SheetName,
                    sourceWorkbookRow = row.RowNumber,
                    active = true
                };
                if (commit)
                    await SaveMasterDetailAsync(definition.Entity, key, payload, "SLH master workbook planner list", ct);
                result.Rows.Add(new WorkbookRowResult(definition.Sheet, row.RowNumber, value.Trim(), commit ? "imported" : "ready", $"{definition.Sheet} entry ready for planner/import matching.", 90) { ActionTaken = commit ? "upserted planner list item" : "would upsert planner list item" });
            }
        }
    }

    private async Task ProcessRunTimesAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("run", StringComparison.OrdinalIgnoreCase) && sheet.Key.Contains("time", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        foreach (var row in rows)
        {
            var route = row.Text("alltimes", "route", "route combination", "routecombination", "run", "run name", "selsey");
            if (string.IsNullOrWhiteSpace(route)) continue;

            var palletType = row.Text("pallet type", "pallettype");
            var lastDespatch = row.Time("last despatch time", "lastdespatchtime", "last dispatch time", "lastdispatchtime");
            var collectFrom = row.Time("planned collect time from", "planned collect from", "collectfrom", "plannedcollecttimefrom");
            var collectTo = row.Time("planned collect time to", "planned collect to", "collectto", "plannedcollecttimeto");
            var depotDeadline = row.Time("depot delivery - no later than", "depot delivery no later than", "depot deadline", "depotdelivery", "depotdeliverynolaterthan");
            var latestCollectionTime = collectTo ?? collectFrom;
            var collectionContext = row.Text("collection context", "collectioncontext");
            var collectionKey = RoutePart(route, 0) ?? collectionContext;
            var deliveryKey = RoutePart(route, -1);
            var key = $"{Canonical(collectionContext)}:{Canonical(route)}:{Canonical(palletType)}";

            var payload = new
            {
                routeCombination = route,
                normalisedRouteKey = Canonical(route),
                collectionContext,
                collectionKey,
                deliveryKey,
                palletType,
                lastDespatch,
                collectFrom,
                collectTo,
                latestCollectionTime,
                depotDeadline,
                dispatchPlanningMode = "Manual planned start may be earlier; latestCollectionTime is the wall-board risk/deadline until first geofence hit updates live ETO/ETA.",
                firstGeofenceResetsLiveEtos = true,
                sourceWorkbookSheet = row.SheetName,
                sourceWorkbookRow = row.RowNumber
            };

            if (commit)
                await SaveMasterDetailAsync("sitetimingrule", key, payload, "SLH master workbook run times", ct);
            result.Rows.Add(new WorkbookRowResult("Run Times", row.RowNumber, route, commit ? "imported" : "ready", commit ? "Route timing rule saved with latestCollectionTime for wall-board planning." : "Route timing rule is ready to import with latestCollectionTime.", 92) { ActionTaken = commit ? "upserted timing rule" : "would upsert timing rule" });
        }
    }

    private async Task ProcessSiteCutoffsAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("cutoff", StringComparison.OrdinalIgnoreCase) || sheet.Key.Contains("cut off", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        var liveSites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, liveSites, ct);

        foreach (var row in rows)
        {
            var siteCode = row.Text("siteid", "site id", "sitecode", "site code", "externalcode");
            var siteName = row.Text("site", "sitename", "site name", "name");
            var identity = new IncomingSiteIdentity(siteCode, siteName, row.Text("driver text name", "drivertextname"), row.Text("collection address", "address", "physical address"), row.Text("aliases", "alias"), row.Text("map link", "maplink"));
            var resolution = SiteMasterIdentityResolver.Resolve(identity, liveSites);
            var cutoffCheck = row.Time("cutoff check", "cut off check", "cutoffcheck");
            var standardCutoff = row.Time("standard cutoff", "standardcutoff") ?? cutoffCheck;
            var extendedCutoff = row.Time("extended cutoff", "extendedcutoff");
            var collectFrom = row.Time("planned collect from", "planned collect time from", "collectfrom", "plannedcollecttimefrom");
            var collectTo = row.Time("planned collect to", "planned collect time to", "collectto", "plannedcollecttimeto");
            var latestCollectionTime = collectTo ?? collectFrom ?? extendedCutoff ?? standardCutoff ?? cutoffCheck;
            var siteKey = resolution.Site?.ExternalCode ?? siteCode ?? siteName;
            var key = $"{Canonical(siteKey)}:{Canonical(row.Text("plan", "plantype", "plan type"))}:{Canonical(row.Text("temperature", "temp"))}:{Canonical(row.Text("pallet type", "pallettype"))}";
            var detail = new WorkbookRowResult("Site Cutoffs", row.RowNumber, siteName ?? siteCode ?? "cutoff", resolution.Matched ? "matched" : "ready", resolution.Matched ? "Cut-off matched to live Site Master." : "Cut-off saved as timing detail for later site association.", resolution.Matched ? 90 : 65);
            if (commit)
            {
                await SaveMasterDetailAsync("sitecutoff", key, new
                {
                    siteId = resolution.Site?.ExternalCode ?? siteCode,
                    siteName = resolution.Site?.Name ?? siteName,
                    matchedLiveSite = resolution.Matched,
                    plan = row.Text("plan", "plan type", "plantype"),
                    standardCutoff,
                    extendedCutoff,
                    cutoffCheck,
                    fallbackCutoff = standardCutoff ?? extendedCutoff ?? cutoffCheck,
                    latestCollectionTime,
                    contact = row.Text("contact"),
                    notes = row.Text("notes"),
                    temperature = row.Text("temperature", "temp"),
                    palletType = row.Text("pallet type", "pallettype"),
                    lastDespatch = row.Time("last despatch time", "lastdespatchtime", "last dispatch time"),
                    collectFrom,
                    collectTo,
                    depotDeadline = row.Time("depot delivery deadline", "depot delivery - no later than", "depot deadline", "depotdelivery", "depotdeliverynolaterthan"),
                    wallBoardDeadline = latestCollectionTime,
                    dispatchPlanningMode = "Manual planned start can be earlier; latestCollectionTime is used as wall-board risk time until live geofence tracking takes over.",
                    firstGeofenceResetsLiveEtos = true,
                    sourceWorkbookSheet = row.SheetName,
                    sourceWorkbookRow = row.RowNumber
                }, "SLH master workbook site cutoffs", ct);
                detail.ActionTaken = resolution.Matched ? "upserted site cutoff detail" : "upserted unmatched cutoff detail";
            }
            else detail.ActionTaken = resolution.Matched ? "would upsert site cutoff detail" : "would save unmatched timing detail for review/association";
            result.Rows.Add(detail);
        }
    }

    private async Task ProcessVehiclesAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets
            .Where(sheet => sheet.Key.Contains("vehicle", StringComparison.OrdinalIgnoreCase)
                || sheet.Key.Contains("fuel", StringComparison.OrdinalIgnoreCase)
                || sheet.Key.Contains("cab", StringComparison.OrdinalIgnoreCase))
            .SelectMany(sheet => sheet.Value)
            .ToList();
        if (rows.Count == 0) return;

        var vehicles = await db.Vehicles.ToListAsync(ct);
        foreach (var row in rows)
        {
            var registration = row.Text("registration", "reg", "vehicle registration");
            if (string.IsNullOrWhiteSpace(registration)) continue;

            var canonical = IntegrationSyncCoordinator.CanonicalVehicleRegistration(registration);
            var vehicle = vehicles.FirstOrDefault(item =>
                string.Equals(
                    IntegrationSyncCoordinator.CanonicalVehicleRegistration(item.Registration),
                    canonical,
                    StringComparison.OrdinalIgnoreCase));

            if (vehicle is null)
            {
                result.Rows.Add(new WorkbookRowResult(
                    "Vehicles & Fuel",
                    row.RowNumber,
                    registration,
                    "skipped",
                    "No existing Fleet vehicle matched this normalised registration. Workbook vehicle rows are update-only and cannot create vehicles.",
                    30) { ActionTaken = "not written" });
                continue;
            }

            if (commit)
            {
                vehicle.Abbreviation = row.Text("abbreviation", "reg last 3", "last 3") ?? vehicle.Abbreviation;
                vehicle.Transmission = row.Text("transmission") ?? vehicle.Transmission;
                vehicle.DvsCompliant = row.Bool("dvs") ?? vehicle.DvsCompliant;
                vehicle.CabMobile = row.Text("cab mobile", "cab phone", "cabmobile") ?? vehicle.CabMobile;
                vehicle.FuelPin = row.Text("fuel pin", "fuelpin") ?? vehicle.FuelPin;
                vehicle.ShellCard = row.Text("shell card", "shellcard") ?? vehicle.ShellCard;
                vehicle.BpRedCard = row.Text("bp red card", "bpredcard") ?? vehicle.BpRedCard;
                vehicle.BpPlainCard = row.Text("bp plain card", "bpplaincard") ?? vehicle.BpPlainCard;
                vehicle.Notes = row.Text("notes") ?? vehicle.Notes;
                await MasterDetailStore.SaveAsync(
                    db,
                    "vehicle",
                    vehicle.Registration,
                    JsonSerializer.Serialize(new
                    {
                        registration = vehicle.Registration,
                        vehicle.Abbreviation,
                        vehicle.Transmission,
                        vehicle.DvsCompliant,
                        vehicle.CabMobile,
                        vehicle.FuelPin,
                        vehicle.ShellCard,
                        vehicle.BpRedCard,
                        vehicle.BpPlainCard,
                        vehicle.Notes,
                        sourceWorkbookSheet = row.SheetName,
                        sourceWorkbookRow = row.RowNumber
                    }),
                    "SLH master workbook vehicle overlay",
                    User.Identity?.Name,
                    ct);
            }

            result.Rows.Add(new WorkbookRowResult(
                "Vehicles & Fuel",
                row.RowNumber,
                vehicle.Registration,
                commit ? "updated" : "matched",
                "Matched existing Fleet vehicle; workbook fields are an operational overlay only.",
                95) { ActionTaken = commit ? "updated vehicle/fuel overlay" : "would update vehicle/fuel overlay" });
        }

        if (commit) await db.SaveChangesAsync(ct);
    }

    private async Task ProcessCustomerContactsAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("customer", StringComparison.OrdinalIgnoreCase) && sheet.Key.Contains("contact", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        foreach (var row in rows)
        {
            var email = row.Text("email", "email address");
            var name = row.Text("name", "contact", "contact name");
            var customerCode = (row.Text("customer code", "customercode") ?? row.Text("customer", "customer name") ?? "UNKNOWN").Trim().ToUpperInvariant().Replace(" ", "-");
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(email)) continue;
            var payload = new Dictionary<string, object?> { ["customerCode"] = customerCode, ["customerName"] = row.Text("customer", "customer name") ?? customerCode, ["name"] = name ?? email!, ["email"] = email, ["mobileNumber"] = row.Text("mobile", "phone", "telephone"), ["active"] = row.Bool("active") ?? true };
            if (commit)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                await staging.PromoteDirect("customercontact", doc.RootElement, ct);
            }
            result.Rows.Add(new WorkbookRowResult("Customer Contacts", row.RowNumber, name ?? email!, commit ? "imported" : "ready", "Customer contact ready for upsert.", 85) { ActionTaken = commit ? "upserted customer contact" : "would upsert customer contact" });
        }
    }

    private async Task ProcessMarketContactsAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("market", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        foreach (var entry in ExpandMarketRows(rows))
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            var payload = new Dictionary<string, object?>
            {
                ["market"] = entry.Market,
                ["name"] = entry.Name,
                ["standOrLocation"] = entry.StandOrLocation,
                ["salesman"] = entry.Salesman,
                ["sender"] = entry.Sender,
                ["readOnlyMapPdfUrl"] = entry.ReadOnlyMapPdfUrl,
                ["active"] = true
            };
            if (commit)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                await staging.PromoteDirect("marketcontact", doc.RootElement, ct);
            }
            result.Rows.Add(new WorkbookRowResult("Market Contacts", entry.RowNumber, $"{entry.Market} - {entry.Name}", commit ? "imported" : "ready", "Market contact ready for upsert, including cross-tabbed Covent/Spit/Western/Sales/Sender layouts.", 88) { ActionTaken = commit ? "upserted market contact" : "would upsert market contact" });
        }
    }

    private async Task ProcessFuelPricesAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets.Where(sheet => sheet.Key.Contains("fuel price", StringComparison.OrdinalIgnoreCase)).SelectMany(sheet => sheet.Value).ToList();
        foreach (var row in rows)
        {
            var provider = row.Text("provider", "supplier", "fuel provider");
            var week = row.Date("week commencing", "date", "effective date");
            var price = row.Decimal("price", "price pence per litre", "pence per litre");
            if (string.IsNullOrWhiteSpace(provider) || week is null || price is null) continue;
            var payload = new Dictionary<string, object?> { ["provider"] = provider, ["weekCommencing"] = week.Value.ToString("yyyy-MM-dd"), ["pricePencePerLitre"] = price.Value, ["isPricingMaximum"] = row.Bool("is pricing maximum", "pricing maximum", "use this max") ?? false, ["source"] = row.Text("source"), ["notes"] = row.Text("notes") };
            if (commit)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                await staging.PromoteDirect("fuelprice", doc.RootElement, ct);
            }
            result.Rows.Add(new WorkbookRowResult("Fuel Price History", row.RowNumber, $"{provider} {week:yyyy-MM-dd}", commit ? "imported" : "ready", "Fuel price ready for provider/week upsert.", 85) { ActionTaken = commit ? "upserted fuel price" : "would upsert fuel price" });
        }
    }

    private async Task ProcessDriversAsync(Workbook workbook, WorkbookImportResult result, bool commit, CancellationToken ct)
    {
        var rows = workbook.Sheets
            .Where(sheet => sheet.Key.Contains("driver", StringComparison.OrdinalIgnoreCase)
                || sheet.Value.Any(row =>
                    row.Text("member code", "tacho member", "worker name", "driver card no.", "driver card number") is not null))
            .SelectMany(sheet => sheet.Value)
            .Where(row => row.Text("member code", "tacho member", "worker name", "display name", "driver name", "driver card no.", "driver card number", "employee number", "driverid") is not null)
            .ToList();
        if (rows.Count == 0) return;
        var drivers = await db.Drivers.ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        foreach (var row in rows)
        {
            var tachoId = row.Text("tachomasterdriverid", "tacho master driver id", "member code", "tacho member");
            var tachoCard = row.Text("tachocardnumber", "tacho card number", "card number", "driver card", "driver card no.", "driver card no", "digicard");
            var employee = row.Text("employee number", "employee no", "driverid", "driver id", "payroll number");
            var name = row.Text("display name", "driver", "driver name", "name", "worker name");

            var driver = drivers.FirstOrDefault(item => !string.IsNullOrWhiteSpace(tachoId) && TachoDriverIdentityRules.MemberMatches(item.TachoMasterDriverId, tachoId))
                ?? drivers.FirstOrDefault(item => !string.IsNullOrWhiteSpace(tachoCard) && TachoDriverIdentityRules.CardsMatch(item.TachoCardNumber, tachoCard))
                ?? drivers.FirstOrDefault(item => !string.IsNullOrWhiteSpace(employee) && SiteMasterIdentityResolver.Normalise(item.EmployeeNumber) == SiteMasterIdentityResolver.Normalise(employee));

            if (driver is null)
            {
                result.Rows.Add(new WorkbookRowResult("Drivers", row.RowNumber, name ?? employee ?? tachoId ?? tachoCard ?? "driver", "skipped", "No existing live driver matched by TachoMaster Member Code, DB Tacho card number or existing employee/DriverID. Workbook driver rows are update-only and cannot create drivers.", 30) { ActionTaken = "not written" });
                continue;
            }

            if (commit)
            {
                driver.TachoMasterDriverId = tachoId ?? driver.TachoMasterDriverId;
                driver.TachoCardNumber = tachoCard ?? driver.TachoCardNumber;
                driver.TachoName = row.Text("tacho name", "worker name") ?? driver.TachoName;
                driver.MobileNumber = row.Text("phone number", "mobile", "mobile number") ?? driver.MobileNumber;
                driver.DriverType = row.Text("driver type", "type") ?? driver.DriverType;
                driver.DriverGroup = row.Text("driver group", "group") ?? driver.DriverGroup;
                driver.AgencyName = row.Text("agency", "agency name") ?? driver.AgencyName;
                driver.Email = row.Text("email", "email address", "e-mail") ?? driver.Email;
                driver.Skills = row.Text("skills", "driver skills") ?? driver.Skills;
                driver.DigitalTachoCardExpiry = ParseWorkbookDate(row.Text("driver card exp.", "driver card exp", "driver card expiry", "digital tacho card expiry")) ?? driver.DigitalTachoCardExpiry;
                driver.LicenceExpiry = ParseWorkbookDate(row.Text("driving licence exp.", "driving licence exp", "driving licence expiry", "licence expiry")) ?? driver.LicenceExpiry;
                driver.CPCExpiry = ParseWorkbookDate(row.Text("dqc expiry", "cpc expiry")) ?? driver.CPCExpiry;
                var payload = new
                {
                    employeeNumber = driver.EmployeeNumber,
                    displayName = driver.DisplayName,
                    tachoName = driver.TachoName,
                    tachoMasterDriverId = driver.TachoMasterDriverId,
                    tachoCardNumber = driver.TachoCardNumber,
                    digitalTachoCardExpiry = driver.DigitalTachoCardExpiry,
                    licenceExpiry = driver.LicenceExpiry,
                    cpcExpiry = driver.CPCExpiry,
                    phoneNumber = driver.MobileNumber,
                    email = driver.Email,
                    coding = row.Text("coding", "code", "driver code"),
                    driverType = driver.DriverType,
                    driverGroup = driver.DriverGroup,
                    agencyName = driver.AgencyName,
                    skills = driver.Skills,
                    cardLastRead = row.Text("card last read"),
                    started = row.Text("started"),
                    northEligible = row.Bool("north eligible", "northeligible"),
                    preloadEligible = row.Bool("preload eligible", "preloadeligible"),
                    notes = row.Text("notes"),
                    sourceWorkbookSheet = row.SheetName,
                    sourceWorkbookRow = row.RowNumber
                };
                await SaveMasterDetailAsync("driver", driver.EmployeeNumber ?? driver.Id.ToString(), payload, "SLH master workbook driver overlay", ct);
            }
            result.Rows.Add(new WorkbookRowResult("Drivers", row.RowNumber, driver.DisplayName, commit ? "updated" : "matched", "Matched existing live driver; operational overlay only, no driver creation.", 95) { ActionTaken = commit ? "updated driver overlay" : "would update driver overlay" });
        }
        if (commit) await db.SaveChangesAsync(ct);
    }

    private static DateOnly? ParseWorkbookDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[] { "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd" };
        return DateOnly.TryParseExact(value.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : DateOnly.TryParse(value.Trim(), out parsed) ? parsed : null;
    }

    private async Task SaveMasterDetailAsync(string entityType, string key, object payload, string source, CancellationToken ct)
    {
        await MasterDetailStore.SaveAsync(db, entityType, key, SiteMasterIdentityResolver.ToJson(payload), source, User.Identity?.Name, ct);
    }

    private static async Task<Workbook> ReadWorkbookAsync(IFormFile file, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await using var stream = file.OpenReadStream();
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var sheets = new Dictionary<string, List<WorkbookRow>>(StringComparer.OrdinalIgnoreCase);
        do
        {
            var sheetName = reader.Name ?? "Sheet";
            var rawRows = new List<List<string?>>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var values = new List<string?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    values.Add(reader.GetValue(i)?.ToString()?.Trim());
                rawRows.Add(values);
            }
            var parsed = ParseRows(sheetName, rawRows);
            if (parsed.Count > 0) sheets[sheetName] = parsed;
        } while (reader.NextResult());
        return new Workbook(sheets);
    }

    private static List<WorkbookRow> ParseRows(string sheetName, List<List<string?>> rawRows)
    {
        var canonicalSheet = Canonical(sheetName);
        var minimumHeaderCells = canonicalSheet is "collectionsites" or "customersfordeliveries" ? 1 : 2;
        var headerIndex = canonicalSheet.Contains("runtime")
            ? rawRows.FindIndex(row => row.Any(cell => Canonical(cell) == "pallettype"))
            : rawRows.FindIndex(row => row.Count(value => !string.IsNullOrWhiteSpace(value)) >= minimumHeaderCells && LooksLikeHeader(sheetName, row));
        if (headerIndex < 0) return [];

        var headers = rawRows[headerIndex].Select((value, index) => HeaderName(sheetName, value, index)).ToList();
        var rows = new List<WorkbookRow>();
        string? collectionContext = null;

        for (var r = headerIndex + 1; r < rawRows.Count; r++)
        {
            var raw = rawRows[r];
            if (raw.All(string.IsNullOrWhiteSpace)) continue;

            if (canonicalSheet.Contains("runtime") && IsRunTimeSectionRow(raw))
            {
                collectionContext = raw.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
                continue;
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < Math.Min(headers.Count, raw.Count); c++)
                if (!string.IsNullOrWhiteSpace(headers[c])) values[headers[c]] = raw[c];

            if (canonicalSheet.Contains("runtime") && !string.IsNullOrWhiteSpace(collectionContext))
                values["collectioncontext"] = collectionContext;

            rows.Add(new WorkbookRow(sheetName, r + 1, values));
        }
        return rows;
    }

    private static string HeaderName(string sheetName, string? value, int index)
    {
        if (index == 0 && sheetName.Contains("run", StringComparison.OrdinalIgnoreCase) && sheetName.Contains("time", StringComparison.OrdinalIgnoreCase))
            return "alltimes";
        return string.IsNullOrWhiteSpace(value) ? $"column{index}" : Canonical(value);
    }

    private static bool LooksLikeHeader(string sheetName, List<string?> row)
    {
        var sheet = Canonical(sheetName);
        var text = string.Join("|", row.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Canonical));
        if (sheet is "collectionsites" && text.Contains("collectionsites")) return true;
        if (sheet is "customersfordeliveries" && text.Contains("deliveries")) return true;
        if (sheet.Contains("runtime")) return text.Contains("pallettype");
        if (sheet.Contains("market") && (text.Contains("covent") || text.Contains("spit") || text.Contains("spital") || text.Contains("western") || text.Contains("salesmen") || text.Contains("salesman") || text.Contains("sender"))) return true;
        return text.Contains("siteid") || text.Contains("vehicleid") || text.Contains("registration") || text.Contains("driverid") || text.Contains("pallettype") || text.Contains("market") || text.Contains("customer") || text.Contains("provider") || text.Contains("cutoffcheck");
    }

    private static bool IsRunTimeSectionRow(List<string?> row)
    {
        var nonBlank = row.Select((value, index) => new { value, index }).Where(cell => !string.IsNullOrWhiteSpace(cell.value)).ToList();
        if (nonBlank.Count != 1 || nonBlank[0].index != 0) return false;
        var value = nonBlank[0].value!.Trim();
        return !value.Contains('-', StringComparison.OrdinalIgnoreCase) && !value.Contains("AllTimes", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<MarketWorkbookEntry> ExpandMarketRows(IEnumerable<WorkbookRow> rows)
    {
        foreach (var row in rows)
        {
            var directMarket = row.Text("market", "market name");
            var directName = row.Text("name", "seller", "seller name", "contact name");
            if (!string.IsNullOrWhiteSpace(directName))
            {
                yield return new MarketWorkbookEntry(row.RowNumber, directMarket ?? "General", directName.Trim(), row.Text("stand", "stall", "stall number", "location") ?? InferStand(directName), row.Text("salesman"), row.Text("sender", "email sender"), row.Text("map", "map pdf", "readonlymappdfurl"));
                continue;
            }

            foreach (var column in row.Values)
            {
                if (string.IsNullOrWhiteSpace(column.Value)) continue;
                var market = MarketFromColumn(column.Key);
                if (market is null) continue;
                var rawName = column.Value.Trim();
                var stand = InferStand(rawName);
                var name = RemoveTrailingStand(rawName, stand);
                var isSender = market.Equals("Sender", StringComparison.OrdinalIgnoreCase);
                yield return new MarketWorkbookEntry(row.RowNumber, market, name, stand, isSender ? null : name, isSender ? name : null, null);
            }
        }
    }

    private static string? MarketFromColumn(string key)
    {
        var canonical = Canonical(key);
        if (canonical.Contains("covent")) return "Covent";
        if (canonical.Contains("spit") || canonical.Contains("spital")) return "Spitalfields";
        if (canonical.Contains("western")) return "Western";
        if (canonical.Contains("salesmen") || canonical.Contains("salesman")) return "Sales";
        if (canonical.Contains("sender")) return "Sender";
        return null;
    }

    private static string? InferStand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bracket = Regex.Match(value, @"\(([^)]+)\)\s*$", RegexOptions.IgnoreCase);
        if (bracket.Success) return bracket.Groups[1].Value.Trim();
        var labelled = Regex.Match(value, @"\b(?:stall|stand|unit|units)\s*#?\s*([a-z]?\d{1,4}[a-z]?(?:\s*(?:-|–|—|&|and)\s*[a-z]?\d{1,4}[a-z]?)?)\s*$", RegexOptions.IgnoreCase);
        return labelled.Success ? labelled.Groups[1].Value.Trim() : null;
    }

    private static string RemoveTrailingStand(string value, string? stand)
    {
        if (string.IsNullOrWhiteSpace(stand)) return value.Trim();
        var withoutBracket = Regex.Replace(value, @"\s*\([^)]+\)\s*$", string.Empty).Trim();
        return Regex.Replace(withoutBracket, @"\b(?:stall|stand|unit|units)\s*#?\s*" + Regex.Escape(stand) + @"\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
    }

    private static string? RoutePart(string route, int index)
    {
        var parts = route.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        return index < 0 ? parts[^1] : index < parts.Length ? parts[index] : null;
    }

    private static string Canonical(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static bool SheetIs(string sheet, string expected) => Canonical(sheet) == Canonical(expected);

    private sealed record MarketWorkbookEntry(int RowNumber, string Market, string Name, string? StandOrLocation, string? Salesman, string? Sender, string? ReadOnlyMapPdfUrl);
}

public sealed record Workbook(Dictionary<string, List<WorkbookRow>> Sheets);

public sealed record WorkbookImportResult(string Mode)
{
    public List<WorkbookRowResult> Rows { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public object Summary => Rows.GroupBy(row => row.Section).ToDictionary(group => group.Key, group => new
    {
        total = group.Count(),
        matched = group.Count(row => row.Status is "matched" or "updated"),
        imported = group.Count(row => row.Status is "imported" or "updated"),
        ready = group.Count(row => row.Status == "ready"),
        review = group.Count(row => row.Status is "review" or "conflict"),
        skipped = group.Count(row => row.Status == "skipped"),
        newRows = group.Count(row => row.Status == "new")
    });
}

public sealed record WorkbookRowResult(string Section, int RowNumber, string Key, string Status, string Reason, int Confidence)
{
    public string? ActionTaken { get; set; }
    public List<string> RelatedRecords { get; init; } = [];
}

public sealed record WorkbookRow(string SheetName, int RowNumber, Dictionary<string, string?> Values)
{
    public string? Text(params string[] names)
    {
        foreach (var name in names)
            if (Values.TryGetValue(new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()), out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return null;
    }

    public bool? Bool(params string[] names)
    {
        var value = Text(names);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value.Trim().ToLowerInvariant() switch
        {
            "yes" or "y" or "1" or "active" or "ok" => true,
            "no" or "n" or "0" or "inactive" => false,
            _ => null
        };
    }

    public string? Time(params string[] names)
    {
        var value = Text(names);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var time)) return time.ToString("HH:mm:ss");
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var serial) && serial >= 0 && serial < 1)
        {
            var span = TimeSpan.FromDays((double)serial);
            return TimeOnly.FromTimeSpan(span).ToString("HH:mm:ss");
        }
        return value;
    }

    public DateOnly? Date(params string[] names)
    {
        var value = Text(names);
        if (DateOnly.TryParse(value, out var date)) return date;
        if (double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var serial) && serial > 25000)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        return null;
    }

    public decimal? Decimal(params string[] names)
    {
        var value = Text(names);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
