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
    public async Task<IActionResult> Queue(QueueCustomerLoadPlanRequest request, CancellationToken ct)
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
        var actor = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "Dispatch";
        var item = new StagedImport
        {
            EntityType = EntityType,
            IdempotencyKey = $"load-plan:{request.PlanningDate:yyyyMMdd}:{SafeKey(plan.CustomerCode)}:{Guid.NewGuid():N}",
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
                order.DriverInstructions));
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
                if (queued is null || queued.PlanningDate != planningDate) continue;
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
                    BodyFor(planningDate),
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

    private static string BodyFor(DateOnly date) =>
        $"Good afternoon,\n\nPlease find attached the Stuart Lyons load plan for {date:dd/MM/yyyy}.\n\nIf anything needs amending, please reply to this email and the planning team will review it.\n\nKind Regards,\nStuart Lyons Haulage\nT: 01243 555536\nE: {Mailbox}";

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
        int TotalPallets);

    public sealed record LoadPlanFailure(string? Error);
}
