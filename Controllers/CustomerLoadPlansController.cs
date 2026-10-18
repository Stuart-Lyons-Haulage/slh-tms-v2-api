using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Builds the daily customer-facing load plan from the same live runs used by Driver Dispatch.
/// Dispatch queues a reviewed snapshot; Power Automate sends that immutable snapshot from the
/// Info shared mailbox and calls the sent endpoint only after Outlook accepts the message.
/// </summary>
[ApiController, Route("api/v1/customer-load-plans"), Authorize]
public sealed class CustomerLoadPlansController(TmsDbContext db, ILogger<CustomerLoadPlansController> logger) : ControllerBase
{
    private const string EntityType = "outbound-load-plan";
    private const string Mailbox = "info@lyonshaulage.com";
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpGet("preview"), Authorize(Policy = "TmsWrite")]
    public async Task<ActionResult<CustomerLoadPlanPreviewResponse>> Preview([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        return Ok(await BuildPreview(planningDate, ct));
    }

    [HttpGet("pdf"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Pdf([FromQuery] DateOnly date, [FromQuery] string customerCode, CancellationToken ct)
    {
        var preview = await BuildPreview(date, ct);
        var plan = preview.Plans.FirstOrDefault(item => string.Equals(item.CustomerCode, customerCode, StringComparison.OrdinalIgnoreCase));
        if (plan is null) return NotFound(new { message = $"No planned movements were found for {customerCode} on {date:dd/MM/yyyy}." });
        return File(CustomerLoadPlanPdf.Build(plan), "application/pdf", plan.AttachmentName);
    }

    [HttpPost("queue"), Authorize(Policy = "TmsWrite")]
    public Task<IActionResult> Queue(QueueCustomerLoadPlanRequest request, CancellationToken ct) =>
        QueueLoadPlanCore(request, null, User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "Dispatch", ct);

    private async Task<IActionResult> QueueLoadPlanCore(QueueCustomerLoadPlanRequest request, string? idempotencyKey, string actor, CancellationToken ct)
    {
        var preview = await BuildPreview(request.PlanningDate, ct);
        var plan = preview.Plans.FirstOrDefault(item => string.Equals(item.CustomerCode, request.CustomerCode, StringComparison.OrdinalIgnoreCase));
        if (plan is null) return NotFound(new { message = $"No live Dispatch plan exists for {request.CustomerCode} on {request.PlanningDate:dd/MM/yyyy}." });

        var to = CleanRecipients(request.To ?? plan.To);
        var cc = CleanRecipients(request.Cc ?? plan.Cc);
        if (to.Count == 0)
            return BadRequest(new { message = "Add at least one customer email address before sending this load plan." });

        var queued = new QueuedLoadPlan(
            plan.PlanningDate,
            plan.CustomerCode,
            plan.CustomerName,
            to,
            cc,
            string.IsNullOrWhiteSpace(request.Subject) ? plan.Subject : request.Subject.Trim(),
            string.IsNullOrWhiteSpace(request.Body) ? plan.Body : request.Body.Trim(),
            plan.AttachmentName,
            plan.Rows,
            plan.TotalPallets);

        var now = DateTimeOffset.UtcNow;
        var item = new StagedImport
        {
            EntityType = EntityType,
            IdempotencyKey = idempotencyKey ?? $"load-plan:{request.PlanningDate:yyyyMMdd}:{SafeKey(plan.CustomerCode)}:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(queued, Json),
            Status = StagingStatus.Approved,
            Source = $"Dispatch load plan / {plan.CustomerCode} / {request.PlanningDate:yyyy-MM-dd}",
            ReceivedAtUtc = now,
            ReviewedAtUtc = now,
            ReviewedBy = actor,
            ReviewNote = $"Queued from Driver Dispatch for delivery from {Mailbox}."
        };
        db.StagedImports.Add(item);
        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = item.Id,
            EventType = "Queued",
            NewStatus = StagingStatus.Approved,
            PayloadJson = JsonSerializer.Serialize(new { plan.CustomerCode, plan.CustomerName, request.PlanningDate, to, cc, plan.AttachmentName }, Json),
            Note = item.ReviewNote,
            Actor = actor,
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);

        return Accepted(new
        {
            id = item.Id,
            status = "Queued",
            mailbox = Mailbox,
            customerCode = plan.CustomerCode,
            customerName = plan.CustomerName,
            attachmentName = plan.AttachmentName,
            recipients = to,
            message = $"{plan.CustomerName} load plan queued to send from {Mailbox}."
        });
    }

    /// <summary>
    /// Per-customer delivery times for a date: each planned delivery with its planned
    /// time and the latest live ETA from tracking. Replaces the hand-built "Daily Times"
    /// and "Aldi Times" emails.
    /// </summary>
    [HttpGet("delivery-times"), Authorize(Policy = "TmsWrite")]
    public async Task<ActionResult<CustomerDeliveryTimesResponse>> DeliveryTimes([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var preview = await BuildPreview(planningDate, ct);
        var etas = await LatestDeliveryEtas(planningDate, ct);
        var lastSends = await LastSends(planningDate, DeliveryTimesKind, ct);
        var customers = preview.Plans.Select(plan =>
        {
            lastSends.TryGetValue(plan.CustomerCode, out var last);
            return new CustomerDeliveryTimesPreview(
                plan.CustomerCode,
                plan.CustomerName,
                planningDate,
                DeliveryTimesSubject(plan.CustomerName, planningDate),
                plan.To,
                DeliveryTimeRows(plan, etas),
                LastStatus(last),
                last?.Status == StagingStatus.Promoted ? last.ReviewedAtUtc : null);
        }).ToList();
        return Ok(new CustomerDeliveryTimesResponse(planningDate, DateTimeOffset.UtcNow, customers, preview.Warnings));
    }

    /// <summary>Queues a customer's delivery times (HTML table in the body, load plan PDF
    /// attached) on the same outbox the Info-mailbox flow already sends.</summary>
    [HttpPost("delivery-times/queue"), Authorize(Policy = "TmsWrite")]
    public Task<IActionResult> QueueDeliveryTimes(QueueCustomerLoadPlanRequest request, CancellationToken ct) =>
        QueueDeliveryTimesCore(request, null, User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "Dispatch", ct);

    private async Task<IActionResult> QueueDeliveryTimesCore(QueueCustomerLoadPlanRequest request, string? idempotencyKey, string actor, CancellationToken ct)
    {
        var preview = await BuildPreview(request.PlanningDate, ct);
        var plan = preview.Plans.FirstOrDefault(item => string.Equals(item.CustomerCode, request.CustomerCode, StringComparison.OrdinalIgnoreCase));
        if (plan is null) return NotFound(new { message = $"No live Dispatch plan exists for {request.CustomerCode} on {request.PlanningDate:dd/MM/yyyy}." });

        var to = CleanRecipients(request.To ?? plan.To);
        var cc = CleanRecipients(request.Cc ?? plan.Cc);
        if (to.Count == 0)
            return BadRequest(new { message = "Add at least one customer email address before sending delivery times." });

        var rows = DeliveryTimeRows(plan, await LatestDeliveryEtas(request.PlanningDate, ct));
        var queued = new QueuedLoadPlan(
            plan.PlanningDate,
            plan.CustomerCode,
            plan.CustomerName,
            to,
            cc,
            string.IsNullOrWhiteSpace(request.Subject) ? DeliveryTimesSubject(plan.CustomerName, plan.PlanningDate) : request.Subject.Trim(),
            string.IsNullOrWhiteSpace(request.Body) ? DeliveryTimesBody(plan.PlanningDate, rows) : request.Body.Trim(),
            DeliveryTimesAttachment(plan.CustomerName, plan.PlanningDate),
            plan.Rows,
            plan.TotalPallets,
            DeliveryTimesKind);

        var now = DateTimeOffset.UtcNow;
        var item = new StagedImport
        {
            EntityType = EntityType,
            IdempotencyKey = idempotencyKey ?? $"delivery-times:{request.PlanningDate:yyyyMMdd}:{SafeKey(plan.CustomerCode)}:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(queued, Json),
            Status = StagingStatus.Approved,
            Source = $"Dispatch delivery times / {plan.CustomerCode} / {request.PlanningDate:yyyy-MM-dd}",
            ReceivedAtUtc = now,
            ReviewedAtUtc = now,
            ReviewedBy = actor,
            ReviewNote = $"Delivery times queued from Driver Dispatch for delivery from {Mailbox}."
        };
        db.StagedImports.Add(item);
        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = item.Id,
            EventType = "Queued",
            NewStatus = StagingStatus.Approved,
            PayloadJson = JsonSerializer.Serialize(new { plan.CustomerCode, plan.CustomerName, request.PlanningDate, to, cc, kind = DeliveryTimesKind }, Json),
            Note = item.ReviewNote,
            Actor = actor,
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);

        return Accepted(new
        {
            id = item.Id,
            status = "Queued",
            mailbox = Mailbox,
            customerCode = plan.CustomerCode,
            customerName = plan.CustomerName,
            recipients = to,
            message = $"{plan.CustomerName} delivery times queued to send from {Mailbox}."
        });
    }

    /// <summary>The automatic-send schedule, with today's state per customer.</summary>
    [HttpGet("schedule"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Schedule(CancellationToken ct)
    {
        var settings = await CustomerCommsSchedule.LoadAsync(db, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var tomorrow = today.AddDays(1);
        var plans = await BuildPreview(tomorrow, ct);
        var timesPreview = await BuildPreview(today, ct);
        var planSends = await LastSends(tomorrow, null, ct);
        var timeSends = await LastSends(today, DeliveryTimesKind, ct);
        var stuck = await db.StagedImports.AsNoTracking()
            .CountAsync(item => item.EntityType == EntityType && item.Status == StagingStatus.Approved && item.ReceivedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-10), ct);
        var customers = plans.Plans.Select(plan => (plan.CustomerCode, plan.CustomerName, plan.To))
            .Concat(timesPreview.Plans.Select(plan => (plan.CustomerCode, plan.CustomerName, plan.To)))
            .Concat(settings.Customers.Keys.Select(code => (code, code, (IReadOnlyList<string>)[])))
            .GroupBy(item => item.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var code = group.Key;
                var name = group.Select(item => item.Item2).FirstOrDefault(value => !string.Equals(value, code, StringComparison.OrdinalIgnoreCase)) ?? code;
                var choice = settings.Customers.GetValueOrDefault(code) ?? new CustomerCommsSchedule.Choice(false, false);
                planSends.TryGetValue(code, out var planSend);
                timeSends.TryGetValue(code, out var timeSend);
                return new
                {
                    customerCode = code,
                    customerName = name,
                    recipients = group.SelectMany(item => item.Item3).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    choice.LoadPlan,
                    choice.DeliveryTimes,
                    loadPlanStatus = LastStatus(planSend),
                    loadPlanHold = CustomerCommsSchedule.HoldFor(code, tomorrow),
                    deliveryTimesStatus = LastStatus(timeSend),
                    hasPlanTomorrow = plans.Plans.Any(item => string.Equals(item.CustomerCode, code, StringComparison.OrdinalIgnoreCase)),
                    hasDeliveriesToday = timesPreview.Plans.Any(item => string.Equals(item.CustomerCode, code, StringComparison.OrdinalIgnoreCase))
                };
            })
            .OrderBy(item => item.customerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(new
        {
            settings.LoadPlanTime,
            settings.DeliveryTimesTime,
            settings.LoadPlanLatest,
            today,
            tomorrow,
            emailsWaitingOverTenMinutes = stuck,
            customers
        });
    }

    [HttpPut("schedule"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SaveSchedule([FromBody] CustomerCommsSchedule.Settings request, CancellationToken ct)
    {
        if (!CustomerCommsSchedule.IsTime(request.LoadPlanTime) || !CustomerCommsSchedule.IsTime(request.DeliveryTimesTime) || !CustomerCommsSchedule.IsTime(request.LoadPlanLatest))
            return BadRequest(new { message = "Times must be HH:mm." });
        await CustomerCommsSchedule.SaveAsync(db, request, User.Identity?.Name ?? "Customer Comms", ct);
        return Ok(request);
    }

    /// <summary>
    /// Used by the schedule: queue one customer's load plan (tomorrow) or delivery times
    /// (today) exactly as the Customer Comms buttons do, once per customer per day.
    /// A load plan is held while any of that customer's orders are not on a run yet.
    /// </summary>
    internal async Task<string> QueueAutomaticAsync(DateOnly date, string customerCode, bool deliveryTimes, CancellationToken ct)
    {
        var kind = deliveryTimes ? DeliveryTimesKind : null;
        var sends = await LastSends(date, kind, ct);
        if (sends.TryGetValue(customerCode, out var existing) && existing.Status is StagingStatus.Approved or StagingStatus.Promoted)
            return "Already sent";
        var key = $"auto:{(deliveryTimes ? "times" : "plan")}:{date:yyyyMMdd}:{SafeKey(customerCode)}";
        if (await db.StagedImports.AsNoTracking().AnyAsync(item => item.IdempotencyKey == key, ct)) return "Already sent";

        if (!deliveryTimes)
        {
            var unplanned = await UnplannedOrdersAsync(date, customerCode, ct);
            if (unplanned > 0) return $"Held: {unplanned} order{(unplanned == 1 ? "" : "s")} not on a run yet";
        }

        var request = new QueueCustomerLoadPlanRequest(date, customerCode, null, null, null, null);
        var result = deliveryTimes
            ? await QueueDeliveryTimesCore(request, key, "Scheduled send", ct)
            : await QueueLoadPlanCore(request, key, "Scheduled send", ct);
        return result switch
        {
            AcceptedResult => "Queued",
            NotFoundObjectResult => "Nothing planned",
            BadRequestObjectResult => "Held: no email address for this customer in Master data",
            _ => "Not sent"
        };
    }

    private async Task<int> UnplannedOrdersAsync(DateOnly date, string customerCode, CancellationToken ct)
    {
        var orders = await db.TransportOrders.AsNoTracking()
            .Where(order => order.CollectionDate == date && order.CustomerCode == customerCode && order.Status != OrderStatus.Cancelled)
            .Select(order => order.Id)
            .ToListAsync(ct);
        if (orders.Count == 0) return 0;
        var onRuns = new HashSet<Guid>();
        foreach (var day in new[] { date.AddDays(-1), date, date.AddDays(1) })
            foreach (var load in await PlanningResilience.ReadLoadsAsync(db, day, ct))
                if (load.Status != LoadStatus.Cancelled)
                    foreach (var stop in load.Stops)
                        if (stop.OrderId is Guid id) onRuns.Add(id);
        return orders.Count(id => !onRuns.Contains(id));
    }

    [HttpGet("outbox"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Outbox([FromQuery] int take = 20, CancellationToken ct = default)
    {
        var queued = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == EntityType && item.Status == StagingStatus.Approved)
            .OrderBy(item => item.ReceivedAtUtc)
            .Take(Math.Clamp(take, 1, 50))
            .ToListAsync(ct);

        var items = new List<CustomerLoadPlanOutboxItem>();
        foreach (var item in queued)
        {
            try
            {
                var plan = JsonSerializer.Deserialize<QueuedLoadPlan>(item.PayloadJson, Json);
                if (plan is null || plan.To.Count == 0) continue;
                var printable = ToPreview(plan);
                items.Add(new CustomerLoadPlanOutboxItem(
                    item.Id,
                    Mailbox,
                    plan.To,
                    plan.Cc,
                    plan.Subject,
                    plan.Body,
                    [new CustomerLoadPlanOutboxAttachment(plan.AttachmentName, "application/pdf", Convert.ToBase64String(CustomerLoadPlanPdf.Build(printable)))],
                    plan.PlanningDate,
                    plan.CustomerCode,
                    plan.CustomerName));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                logger.LogWarning(exception, "Skipping malformed customer load-plan outbox item {OutboxId}.", item.Id);
            }
        }
        return Ok(new { count = items.Count, items });
    }

    [HttpPost("{id:guid}/sent"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> MarkSent(Guid id, MarkCustomerLoadPlanSentRequest request, CancellationToken ct)
    {
        var item = await db.StagedImports.SingleOrDefaultAsync(row => row.Id == id && row.EntityType == EntityType, ct);
        if (item is null) return NotFound();
        if (item.Status == StagingStatus.Promoted) return Ok(new { id, alreadySent = true });
        if (item.Status != StagingStatus.Approved) return Conflict(new { message = $"Load-plan outbox item is {item.Status}, not queued." });

        var previous = item.Status;
        var now = DateTimeOffset.UtcNow;
        item.Status = StagingStatus.Promoted;
        item.ReviewedAtUtc = now;
        item.ReviewedBy = "Info mailbox outbound flow";
        item.ReviewNote = string.Join(" | ", new[]
        {
            item.ReviewNote,
            $"Sent from {Mailbox} at {now:O}",
            string.IsNullOrWhiteSpace(request.ProviderMessageId) ? null : $"Outlook message: {request.ProviderMessageId}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = item.Id,
            EventType = "Sent",
            PreviousStatus = previous,
            NewStatus = item.Status,
            PayloadJson = JsonSerializer.Serialize(new { mailbox = Mailbox, request.ProviderMessageId, sentAtUtc = now }, Json),
            Note = item.ReviewNote,
            Actor = item.ReviewedBy,
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { id, sent = true, sentAtUtc = now, mailbox = Mailbox });
    }

    [HttpPost("{id:guid}/failed"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> MarkFailed(Guid id, [FromBody] LoadPlanFailure request, CancellationToken ct)
    {
        var item = await db.StagedImports.SingleOrDefaultAsync(row => row.Id == id && row.EntityType == EntityType, ct);
        if (item is null) return NotFound();
        if (item.Status == StagingStatus.Promoted) return Conflict(new { message = "This load plan is already marked sent." });
        var previous = item.Status;
        item.Status = StagingStatus.Failed;
        item.ReviewedAtUtc = DateTimeOffset.UtcNow;
        item.ReviewedBy = "Info mailbox outbound flow";
        item.ReviewNote = string.Join(" | ", new[] { item.ReviewNote, $"Send failed: {request.Error}" }.Where(value => !string.IsNullOrWhiteSpace(value)));
        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = item.Id,
            EventType = "Failed",
            PreviousStatus = previous,
            NewStatus = item.Status,
            PayloadJson = JsonSerializer.Serialize(new { request.Error }, Json),
            Note = item.ReviewNote,
            Actor = item.ReviewedBy
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { id, failed = true });
    }

    private async Task<CustomerLoadPlanPreviewResponse> BuildPreview(DateOnly planningDate, CancellationToken ct)
    {
        var warnings = new List<string>();
        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToList();

        var orderBindings = loads
            .SelectMany(load => load.Stops.Where(stop => stop.OrderId is not null).Select(stop => new { Load = load, Stop = stop, OrderId = stop.OrderId!.Value }))
            .GroupBy(binding => binding.OrderId)
            .Select(group => group.OrderBy(binding => binding.Stop.Sequence).Last())
            .ToList();
        var orderIds = orderBindings.Select(binding => binding.OrderId).Distinct().ToList();
        if (orderIds.Count == 0)
            return new CustomerLoadPlanPreviewResponse(planningDate, DateTimeOffset.UtcNow, [], ["No customer-linked orders are allocated to runs for this date yet."]);

        List<TransportOrder> orders;
        try
        {
            orders = await db.TransportOrders.AsNoTracking()
                .Where(order => orderIds.Contains(order.Id) && order.Status != OrderStatus.Cancelled)
                .ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Customer load-plan preview could not read TransportOrders for {PlanningDate}.", planningDate);
            return new CustomerLoadPlanPreviewResponse(planningDate, DateTimeOffset.UtcNow, [], ["Live order details are temporarily unavailable, so customer load plans cannot be generated safely."]);
        }

        var customerCodes = orders.Select(order => order.CustomerCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var customers = await db.Customers.AsNoTracking().Where(customer => customerCodes.Contains(customer.Code)).ToListAsync(ct);
        var contacts = await db.CustomerContacts.AsNoTracking().Where(contact => contact.Active && customerCodes.Contains(contact.CustomerCode) && contact.Email != null).ToListAsync(ct);
        var driverIds = loads.Where(load => load.DriverId is not null).Select(load => load.DriverId!.Value).Distinct().ToList();
        var vehicleIds = loads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var trailerIds = loads.Where(load => load.TrailerId is not null).Select(load => load.TrailerId!.Value).Distinct().ToList();
        List<Driver> drivers = driverIds.Count == 0 ? [] : await db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id)).ToListAsync(ct);
        List<Vehicle> vehicles = vehicleIds.Count == 0 ? [] : await db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)).ToListAsync(ct);
        List<Trailer> trailers = trailerIds.Count == 0 ? [] : await db.Trailers.AsNoTracking().Where(trailer => trailerIds.Contains(trailer.Id)).ToListAsync(ct);

        var orderById = orders.ToDictionary(order => order.Id);
        var customerNameByCode = customers.ToDictionary(customer => customer.Code, customer => customer.Name, StringComparer.OrdinalIgnoreCase);
        var driverById = drivers.ToDictionary(driver => driver.Id);
        var vehicleById = vehicles.ToDictionary(vehicle => vehicle.Id);
        var trailerById = trailers.ToDictionary(trailer => trailer.Id);

        var palletTypes = await PalletTypesAsync(orders, ct);
        var rows = new List<CustomerLoadPlanRow>();
        foreach (var binding in orderBindings)
        {
            if (!orderById.TryGetValue(binding.OrderId, out var order)) continue;
            var load = binding.Load;
            var collectionStop = FindCollectionStop(load, order, binding.Stop.Sequence);
            var delivery = !string.IsNullOrWhiteSpace(order.MarketName)
                ? string.Join(" - ", new[] { order.MarketName, order.StallNumber }.Where(value => !string.IsNullOrWhiteSpace(value)))
                : order.StallNumber ?? CleanStopName(binding.Stop.Name);
            rows.Add(new CustomerLoadPlanRow(
                load.Id,
                load.Reference,
                order.Id,
                order.Reference,
                order.CustomerCode,
                order.SellerName ?? CleanStopName(collectionStop?.Name) ?? "Collection site not set",
                delivery ?? "Delivery site not set",
                order.Pallets,
                LocalTime(collectionStop?.PlannedArrivalUtc),
                LocalTime(order.DeliveryWindowEndUtc ?? order.DeliveryWindowStartUtc),
                load.DriverId is Guid driverId && driverById.TryGetValue(driverId, out var driver) ? driver.DisplayName : null,
                load.VehicleId is Guid vehicleId && vehicleById.TryGetValue(vehicleId, out var vehicle) ? vehicle.Registration : null,
                load.TrailerId is Guid trailerId && trailerById.TryGetValue(trailerId, out var trailer) ? trailer.TrailerNumber : null,
                load.Status.ToString(),
                order.DriverInstructions,
                palletTypes.GetValueOrDefault(order.Id),
                binding.Stop.Sequence));
        }

        var previous = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == EntityType)
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(500)
            .ToListAsync(ct);
        var previousByKey = new Dictionary<string, StagedImport>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in previous)
        {
            try
            {
                var queued = JsonSerializer.Deserialize<QueuedLoadPlan>(item.PayloadJson, Json);
                if (queued is null || queued.PlanningDate != planningDate || queued.Kind is not null) continue;
                var key = queued.CustomerCode;
                if (!previousByKey.ContainsKey(key)) previousByKey[key] = item;
            }
            catch (JsonException) { }
        }

        var plans = rows.GroupBy(row => row.CustomerCode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => customerNameByCode.GetValueOrDefault(group.Key, group.Key))
            .Select(group =>
            {
                var customerName = customerNameByCode.GetValueOrDefault(group.Key, group.Key);
                var planRows = group.OrderBy(row => row.LoadReference).ThenBy(row => row.CollectionSite).ThenBy(row => row.DeliverySite).ToList();
                var preferredContacts = contacts.Where(contact => string.Equals(contact.CustomerCode, group.Key, StringComparison.OrdinalIgnoreCase) && contact.ReceivesEtaUpdates).Select(contact => contact.Email!);
                var fallbackContacts = contacts.Where(contact => string.Equals(contact.CustomerCode, group.Key, StringComparison.OrdinalIgnoreCase)).Select(contact => contact.Email!);
                var recipients = CleanRecipients(preferredContacts.Any() ? preferredContacts : fallbackContacts);
                previousByKey.TryGetValue(group.Key, out var last);
                return new CustomerLoadPlanPreview(
                    group.Key,
                    customerName,
                    planningDate,
                    SubjectFor(group.Key, customerName, planningDate),
                    BodyFor(planningDate, planRows),
                    AttachmentFor(group.Key, customerName, planningDate),
                    recipients,
                    [],
                    planRows,
                    planRows.Sum(row => row.Pallets ?? 0),
                    LastStatus(last),
                    last?.Status == StagingStatus.Promoted ? last.ReviewedAtUtc : null);
            }).ToList();

        var orphaned = orderIds.Count - rows.Count;
        if (orphaned > 0) warnings.Add($"{orphaned} run-linked order(s) could not be resolved into a customer load plan.");
        if (plans.Any(plan => plan.To.Count == 0)) warnings.Add("Some customers have no active email contact in Master Data. Add recipients in the preview before sending.");
        return new CustomerLoadPlanPreviewResponse(planningDate, DateTimeOffset.UtcNow, plans, warnings);
    }

    /// <summary>Euro / standard (etc.) for each order, from the booking it was approved from.</summary>
    private async Task<Dictionary<Guid, string>> PalletTypesAsync(IReadOnlyCollection<TransportOrder> orders, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        var bySource = orders.Where(order => order.SourceStagedImportId != null)
            .GroupBy(order => order.SourceStagedImportId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(order => order.Id).ToList());
        if (bySource.Count == 0) return result;
        try
        {
            foreach (var chunk in bySource.Keys.Chunk(400))
            {
                var payloads = await db.StagedImports.AsNoTracking()
                    .Where(item => chunk.Contains(item.Id))
                    .Select(item => new { item.Id, item.PayloadJson })
                    .ToListAsync(ct);
                foreach (var row in payloads)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(row.PayloadJson);
                        string? type = null;
                        foreach (var name in new[] { "palletType", "loadUnitType", "unitType", "handlingUnitType", "palletFormat", "capacityType" })
                            if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                            { type = value.GetString(); break; }
                        if (type is null) continue;
                        foreach (var orderId in bySource[row.Id]) result[orderId] = type;
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Pallet types for the customer load plan could not be read; loading guides will be skipped.");
            db.ChangeTracker.Clear();
        }
        return result;
    }

    private static CustomerLoadPlanPreview ToPreview(QueuedLoadPlan plan) => new(
        plan.CustomerCode, plan.CustomerName, plan.PlanningDate, plan.Subject, plan.Body, plan.AttachmentName,
        plan.To, plan.Cc, plan.Rows, plan.TotalPallets, "Queued", null);

    private static LoadStop? FindCollectionStop(Load load, TransportOrder order, int deliverySequence)
    {
        var candidates = load.Stops.Where(stop => stop.Sequence <= deliverySequence).OrderBy(stop => stop.Sequence).ToList();
        if (!string.IsNullOrWhiteSpace(order.SellerName))
        {
            var named = candidates.LastOrDefault(stop => CleanStopName(stop.Name)?.Contains(order.SellerName, StringComparison.OrdinalIgnoreCase) == true);
            if (named is not null) return named;
        }
        return candidates.LastOrDefault(stop => stop.Sequence < deliverySequence && stop.Name.StartsWith("Collect", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    private static string? CleanStopName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim();
        foreach (var prefix in new[] { "Collect · ", "Deliver · ", "Collect - ", "Deliver - ", "Collect: ", "Deliver: " })
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return clean[prefix.Length..].Trim();
        return clean;
    }

    private static string? LocalTime(DateTimeOffset? value) => value is null
        ? null
        : TimeZoneInfo.ConvertTime(value.Value, London).ToString("HH:mm");

    private static string SubjectFor(string customerCode, string customerName, DateOnly date)
    {
        var compact = date.ToString("ddMMyyyy");
        if (customerCode.Equals("NWF", StringComparison.OrdinalIgnoreCase)) return $"Lyons Collections-{compact}";
        if (customerCode.Contains("BARFOOT", StringComparison.OrdinalIgnoreCase)) return $"Barfoots Load Plan-{compact}";
        if (customerCode.Contains("LANGMEAD", StringComparison.OrdinalIgnoreCase)) return $"Langmead Herbs Lyons Collections-{compact}";
        return $"{customerName} Load Plan-{compact}";
    }

    private static string AttachmentFor(string customerCode, string customerName, DateOnly date)
    {
        var compact = date.ToString("ddMMyyyy");
        if (customerCode.Equals("NWF", StringComparison.OrdinalIgnoreCase)) return $"Lyons Collections-{compact}.pdf";
        if (customerCode.Contains("BARFOOT", StringComparison.OrdinalIgnoreCase)) return $"Barfoots Load Plan-{compact}.pdf";
        var safe = new string(customerName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray()).Trim();
        return $"{safe} Load Plan-{compact}.pdf";
    }

    private static string BodyFor(DateOnly date, IReadOnlyList<CustomerLoadPlanRow>? rows = null)
    {
        var guides = rows is null ? [] : LoadingGuide.ForPlan(rows);
        var guideLines = guides.Count == 0
            ? string.Empty
            : "\n\nLoading guides (mixed euro and standard pallets, at the back of the attachment):\n" +
              string.Join("\n", guides.Select(guide => $"{guide.LoadReference} loading guide: {guide.Code}"));
        return $"Good afternoon,\n\nPlease find attached the Stuart Lyons load plan for {date:dd/MM/yyyy}.{guideLines}\n\nIf anything needs amending, please reply to this email and the planning team will review it.\n\nKind Regards,\nStuart Lyons Haulage\nT: 01243 555536\nE: {Mailbox}";
    }

    private static List<string> CleanRecipients(IEnumerable<string> values) => values
        .Select(value => value?.Trim() ?? string.Empty)
        .Where(value => value.Length > 3 && value.Contains('@'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string SafeKey(string value)
    {
        var safe = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return safe.Length == 0 ? "CUSTOMER" : safe[..Math.Min(safe.Length, 40)];
    }

    private static string? LastStatus(StagedImport? item) => item?.Status switch
    {
        StagingStatus.Approved => "Queued",
        StagingStatus.Promoted => "Sent",
        StagingStatus.Failed => "Failed",
        _ => null
    };

    private sealed record QueuedLoadPlan(
        DateOnly PlanningDate,
        string CustomerCode,
        string CustomerName,
        IReadOnlyList<string> To,
        IReadOnlyList<string> Cc,
        string Subject,
        string Body,
        string AttachmentName,
        IReadOnlyList<CustomerLoadPlanRow> Rows,
        int TotalPallets,
        string? Kind = null);

    private const string DeliveryTimesKind = "DeliveryTimes";

    /// <summary>Latest live ETA for each (run, order) delivery stop on the date, from tracking.</summary>
    private async Task<Dictionary<(Guid LoadId, Guid OrderId), EtaSnapshot>> LatestDeliveryEtas(DateOnly planningDate, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, Guid), EtaSnapshot>();
        var loads = await PlanningResilience.ReadLoadsAsync(db, planningDate, ct);
        var deliveryStops = loads
            .Where(load => load.Status != LoadStatus.Cancelled)
            .SelectMany(load => load.Stops.Where(stop => stop.OrderId is not null).Select(stop => (load.Id, OrderId: stop.OrderId!.Value, stop.Id, stop.Sequence)))
            .GroupBy(stop => (stop.Item1, stop.OrderId))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(stop => stop.Sequence).First().Item3);
        if (deliveryStops.Count == 0) return result;

        var stopIds = deliveryStops.Values.Distinct().ToList();
        var dayStart = new DateTimeOffset(planningDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        try
        {
            var snapshots = await db.EtaSnapshots.AsNoTracking()
                .Where(snapshot => stopIds.Contains(snapshot.StopId) && snapshot.EtaUtc != null && snapshot.CapturedAtUtc >= dayStart)
                .ToListAsync(ct);
            var latestByStop = snapshots
                .GroupBy(snapshot => snapshot.StopId)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.CapturedAtUtc).First());
            foreach (var (key, stopId) in deliveryStops)
                if (latestByStop.TryGetValue(stopId, out var snapshot)) result[key] = snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Live ETAs could not be read for delivery times on {PlanningDate}; planned times only.", planningDate);
        }
        return result;
    }

    private static IReadOnlyList<CustomerDeliveryTimeRow> DeliveryTimeRows(CustomerLoadPlanPreview plan, IReadOnlyDictionary<(Guid LoadId, Guid OrderId), EtaSnapshot> etas) =>
        plan.Rows
            .OrderBy(row => row.DeliveryDeadline ?? "99:99")
            .ThenBy(row => row.DeliverySite)
            .Select(row =>
            {
                etas.TryGetValue((row.LoadId, row.OrderId), out var eta);
                return new CustomerDeliveryTimeRow(
                    row.OrderReference,
                    row.DeliverySite,
                    row.Pallets,
                    row.VehicleRegistration,
                    row.DeliveryDeadline,
                    LocalTime(eta?.EtaUtc),
                    eta?.Risk,
                    LocalTime(eta?.CapturedAtUtc),
                    row.Status);
            })
            .ToList();

    private static string DeliveryTimesSubject(string customerName, DateOnly date) => $"{customerName} Delivery Times-{date:ddMMyyyy}";

    private static string DeliveryTimesAttachment(string customerName, DateOnly date)
    {
        var safe = new string(customerName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray()).Trim();
        return $"{safe} Delivery Times-{date:ddMMyyyy}.pdf";
    }

    // Single-line HTML: the Info-mailbox flow turns newlines into <br>.
    private static string DeliveryTimesBody(DateOnly date, IReadOnlyList<CustomerDeliveryTimeRow> rows)
    {
        static string Cell(string? value) => $"<td style=\"padding:4px 8px;border:1px solid #ccc\">{System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(value) ? "—" : value)}</td>";
        var body = new System.Text.StringBuilder();
        body.Append($"Good afternoon,<br><br>Please see your delivery times for {date:dddd dd/MM/yyyy}. Live ETAs come from our vehicle tracking and are correct at the time shown.<br><br>");
        body.Append("<table style=\"border-collapse:collapse;font-family:Arial,sans-serif;font-size:10pt\"><tr style=\"background:#f0f0f0\">");
        foreach (var heading in new[] { "Delivery", "Order ref", "Pallets", "Vehicle", "Planned", "Live ETA", "ETA as at" })
            body.Append($"<th style=\"padding:4px 8px;border:1px solid #ccc;text-align:left\">{heading}</th>");
        body.Append("</tr>");
        foreach (var row in rows)
            body.Append("<tr>")
                .Append(Cell(row.DeliverySite)).Append(Cell(row.OrderReference)).Append(Cell(row.Pallets?.ToString()))
                .Append(Cell(row.VehicleRegistration)).Append(Cell(row.PlannedDelivery)).Append(Cell(row.LiveEta)).Append(Cell(row.EtaAsAt))
                .Append("</tr>");
        body.Append($"</table><br>The load plan is attached. If anything needs amending, please reply to this email.<br><br>Kind Regards,<br>Stuart Lyons Haulage<br>T: 01243 555536<br>E: {Mailbox}");
        return body.ToString();
    }

    private async Task<Dictionary<string, StagedImport>> LastSends(DateOnly planningDate, string? kind, CancellationToken ct)
    {
        var previous = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == EntityType)
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(500)
            .ToListAsync(ct);
        var result = new Dictionary<string, StagedImport>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in previous)
        {
            try
            {
                var queued = JsonSerializer.Deserialize<QueuedLoadPlan>(item.PayloadJson, Json);
                if (queued is null || queued.PlanningDate != planningDate || !string.Equals(queued.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
                result.TryAdd(queued.CustomerCode, item);
            }
            catch (JsonException) { }
        }
        return result;
    }

    public sealed record LoadPlanFailure(string? Error);
}
