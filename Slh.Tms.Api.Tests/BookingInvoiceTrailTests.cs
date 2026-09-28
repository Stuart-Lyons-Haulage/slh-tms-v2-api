using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BookingInvoiceTrailTests
{
    [Fact]
    public async Task Nwf_snapshot_creates_reservation_then_records_amendment_and_cancellation()
    {
        await using var db = CreateDb();
        var staged = new StagedImport { EntityType = "order", IdempotencyKey = "nwf-1", PayloadJson = "{}" };
        using var first = JsonDocument.Parse("""
            {"customerCode":"NWF","jobType":"NWF crate dump","collectionDate":"2026-09-29","deliveryDate":"2026-09-30","collectionReference":"CRATE-100","nwfCratePoForGrower":"CRATE-PO-1","palletQty":12,"intakeNaturalKey":"NWF|2026-09-29|CRATEREF:CRATE-100","sourceMessageId":"message-1","sourceAttachmentName":"crate-dump.xlsm"}
            """);

        var id = await NwfBookingReservationSync.UpsertAsync(db, staged, first.RootElement, "planner", CancellationToken.None);
        await db.SaveChangesAsync();

        var reservation = await db.BookingReservations.SingleAsync();
        Assert.Equal(id, reservation.Id);
        Assert.Equal(BookingReservationStatus.PreOrder, reservation.Status);
        Assert.Equal(12, reservation.ReservedUnits);
        Assert.Single(await db.BookingReservationRevisions.ToListAsync());

        using var amendment = JsonDocument.Parse("""
            {"customerCode":"NWF","jobType":"NWF crate dump","collectionDate":"2026-09-30","collectionReference":"CRATE-100-AMENDED","palletQty":10,"intakeNaturalKey":"NWF|2026-09-29|CRATEREF:CRATE-100","sourceMessageId":"message-2","status":"amended"}
            """);
        await NwfBookingReservationSync.UpsertAsync(db, staged, amendment.RootElement, "planner", CancellationToken.None);
        await db.SaveChangesAsync();

        reservation = await db.BookingReservations.SingleAsync();
        Assert.Equal(BookingReservationStatus.Amended, reservation.Status);
        Assert.Equal(10, reservation.ReservedUnits);
        Assert.Equal("CRATE-100-AMENDED", reservation.CollectionReference);
        Assert.Equal(new DateOnly(2026, 9, 30), reservation.CollectionDate);
        Assert.Equal(2, reservation.CurrentRevisionNumber);

        using var cancellation = JsonDocument.Parse("""
            {"customerCode":"NWF","collectionDate":"2026-09-30","collectionReference":"CRATE-100-AMENDED","palletQty":10,"intakeNaturalKey":"NWF|2026-09-29|CRATEREF:CRATE-100","sourceMessageId":"message-3","deleted":true}
            """);
        await NwfBookingReservationSync.UpsertAsync(db, staged, cancellation.RootElement, "planner", CancellationToken.None);
        await db.SaveChangesAsync();

        reservation = await db.BookingReservations.SingleAsync();
        Assert.Equal(BookingReservationStatus.Cancelled, reservation.Status);
        Assert.Equal(3, reservation.CurrentRevisionNumber);
        Assert.Equal(3, await db.BookingReservationRevisions.CountAsync());
        Assert.Equal(["ReceivedFromNwfIntake", "AmendedFromNwfIntake", "CancelledFromNwfIntake"], await db.OperationalHistoryEvents.OrderBy(item => item.OccurredAtUtc).Select(item => item.EventType).ToListAsync());
    }

    [Fact]
    public async Task Nwf_source_refresh_reuses_business_identity_without_downgrading_confirmed_booking()
    {
        await using var db = CreateDb();
        var staged = new StagedImport { EntityType = "order", IdempotencyKey = "nwf-source-refresh", PayloadJson = "{}" };
        using var first = JsonDocument.Parse("""
            {"customerCode":"NWF","jobType":"NWF crate dump","collectionDate":"2026-09-29","collectionReference":"CRATE-REFRESH-1","palletQty":12,"sourceRow":"row-42","sourceMessageId":"message-old","plannerReady":true,"intakeNaturalKey":"old-natural-key"}
            """);
        await NwfBookingReservationSync.UpsertAsync(db, staged, first.RootElement, "planner", CancellationToken.None);
        await db.SaveChangesAsync();

        using var refreshed = JsonDocument.Parse("""
            {"customerCode":"NWF","jobType":"NWF crate dump","collectionDate":"2026-09-29","collectionReference":"CRATE-REFRESH-1","palletQty":12,"sourceRow":"row-42","sourceMessageId":"message-new","intakeNaturalKey":"new-natural-key"}
            """);
        await NwfBookingReservationSync.UpsertAsync(db, staged, refreshed.RootElement, "planner", CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Single(await db.BookingReservations.ToListAsync());
        var reservation = await db.BookingReservations.SingleAsync();
        Assert.Equal(BookingReservationStatus.Confirmed, reservation.Status);
        Assert.Equal(2, reservation.CurrentRevisionNumber);
        Assert.Equal(["Received", "New NWF source snapshot retained without business-field change"], await db.BookingReservationRevisions.OrderBy(item => item.RevisionNumber).Select(item => item.ChangeNote).ToListAsync());
    }

    [Fact]
    public async Task Planner_cancellation_creates_a_revision_and_operational_event()
    {
        await using var db = CreateDb();
        var reservation = new BookingReservation
        {
            CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|2026-09-29|CANCEL-1",
            CollectionDate = new DateOnly(2026, 9, 29), ReservedUnits = 8, CompositionJson = "{}"
        };
        db.BookingReservations.Add(reservation);
        await db.SaveChangesAsync();

        var controller = new BookingReservationsController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "planner") })) } };
        var result = await controller.Cancel(reservation.Id, new CancelBookingReservationRequest("NWF deleted the pre-order"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.BookingReservations.SingleAsync();
        Assert.Equal(BookingReservationStatus.Cancelled, saved.Status);
        Assert.Equal(2, saved.CurrentRevisionNumber);
        Assert.Equal("NWF deleted the pre-order", (await db.BookingReservationRevisions.SingleAsync()).ChangeNote);
        Assert.Equal("Cancelled", Assert.Single(await db.OperationalHistoryEvents.Select(item => item.EventType).ToListAsync()));
    }

    [Fact]
    public async Task Draft_order_cannot_be_matched_as_a_confirmed_movement()
    {
        await using var db = CreateDb();
        var reservation = new BookingReservation
        {
            CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|2026-09-29|DRAFT-1",
            CollectionDate = new DateOnly(2026, 9, 29), ReservedUnits = 8, CompositionJson = "{}"
        };
        var order = new TransportOrder { Reference = "DRAFT-ORDER-1", CustomerCode = "NWF", CollectionDate = reservation.CollectionDate, Pallets = 8, Status = OrderStatus.Draft };
        db.BookingReservations.Add(reservation);
        db.TransportOrders.Add(order);
        await db.SaveChangesAsync();
        var controller = new BookingReservationsController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "planner") })) } };

        Assert.IsType<ConflictObjectResult>(await controller.MatchOrder(reservation.Id, new MatchBookingOrderRequest(order.Id), CancellationToken.None));
        Assert.Empty(await db.BookingReservationAllocations.ToListAsync());
    }

    [Fact]
    public async Task Unmatch_retains_allocation_evidence_but_releases_capacity()
    {
        await using var db = CreateDb();
        var reservation = new BookingReservation
        {
            CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|2026-09-29|UNMATCH-1",
            CollectionDate = new DateOnly(2026, 9, 29), ReservedUnits = 8, CompositionJson = "{}", Status = BookingReservationStatus.Assigned
        };
        var order = new TransportOrder { Reference = "NWF-UNMATCH-1", CustomerCode = "NWF", CollectionDate = reservation.CollectionDate, Pallets = 8, Status = OrderStatus.ReadyToPlan };
        var allocation = new BookingReservationAllocation { BookingReservationId = reservation.Id, TransportOrderId = order.Id, Units = 8 };
        db.AddRange(reservation, order, allocation);
        await db.SaveChangesAsync();
        var controller = new BookingReservationsController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "planner") })) } };

        Assert.IsType<OkObjectResult>(await controller.Unmatch(reservation.Id, new UnmatchBookingAllocationRequest(allocation.Id, "Wrong collection reference"), CancellationToken.None));
        var savedAllocation = await db.BookingReservationAllocations.SingleAsync();
        Assert.False(savedAllocation.IsActive);
        Assert.Equal(BookingReservationStatus.PreOrder, (await db.BookingReservations.SingleAsync()).Status);
        Assert.Contains("UnmatchedFromTransportOrder", await db.OperationalHistoryEvents.Select(item => item.EventType).ToListAsync());
    }

    [Fact]
    public async Task Planner_cannot_reduce_capacity_below_active_matches()
    {
        await using var db = CreateDb();
        var reservation = new BookingReservation
        {
            CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|2026-09-29|CAPACITY-1",
            CollectionDate = new DateOnly(2026, 9, 29), ReservedUnits = 8, CompositionJson = "{}", Status = BookingReservationStatus.Assigned
        };
        var order = new TransportOrder { Reference = "NWF-CAPACITY-1", CustomerCode = "NWF", CollectionDate = reservation.CollectionDate, Pallets = 8, Status = OrderStatus.Planned };
        db.AddRange(reservation, order, new BookingReservationAllocation { BookingReservationId = reservation.Id, TransportOrderId = order.Id, Units = 8 });
        await db.SaveChangesAsync();
        var controller = new BookingReservationsController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "planner") })) } };

        var result = await controller.Revise(reservation.Id, new ReviseBookingReservationRequest(ReservedUnits: 4, ChangeNote: "NWF reduced capacity"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(8, (await db.BookingReservations.SingleAsync()).ReservedUnits);
    }

    [Fact]
    public async Task Candidate_matching_surfaces_retained_source_reference_first()
    {
        await using var db = CreateDb();
        var reservation = new BookingReservation
        {
            CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|2026-09-29|SOURCE-1",
            CollectionDate = new DateOnly(2026, 9, 29), CollectionReference = "CRATE-REF-1", ReservedUnits = 8, CompositionJson = "{}"
        };
        var movement = new OrderMovement { CustomerCode = "NWF", StableMovementKey = "NWF|SOURCE-1" };
        var revision = new OrderRevision { MovementId = movement.Id, StagedImportId = Guid.NewGuid(), RevisionNumber = 1, PayloadJson = "{}" };
        movement.CurrentRevisionId = revision.Id;
        var sourceLine = new OrderSourceLine { RevisionId = revision.Id, SourceRowKey = "row-1", LoadReference = "CRATE-REF-1", PayloadJson = "{}" };
        var order = new TransportOrder { Reference = "NWF-CONFIRMED-1", CustomerCode = "NWF", CollectionDate = reservation.CollectionDate, Pallets = 8, Status = OrderStatus.ReadyToPlan, SourceMovementId = movement.Id };
        db.AddRange(reservation, movement, revision, sourceLine, order);
        await db.SaveChangesAsync();
        var controller = new BookingReservationsController(db);
        var result = await controller.CandidateOrders(reservation.Id, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal("CRATE-REF-1", json[0].GetProperty("collectionReference").GetString());
    }

    [Fact]
    public async Task Reservation_source_evidence_returns_the_retained_mailbox_payload()
    {
        await using var db = CreateDb();
        var evidence = new StagedImport { EntityType = "email-evidence", IdempotencyKey = "source-evidence:message-1", PayloadJson = "{\"body\":\"original workbook request\"}", Source = "Retained mailbox evidence" };
        var staged = new StagedImport { EntityType = "order", IdempotencyKey = "email:message-1:row-1", PayloadJson = "{\"sourceEvidenceKey\":\"source-evidence:message-1\",\"sourceMessageId\":\"message-1\"}", Source = "Info mailbox" };
        var reservation = new BookingReservation { CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|SOURCE-EVIDENCE-1", CollectionDate = new DateOnly(2026, 9, 29), ReservedUnits = 8, CompositionJson = "{}", SourceStagedImportId = staged.Id };
        db.AddRange(evidence, staged, reservation);
        await db.SaveChangesAsync();

        var result = await new BookingReservationsController(db).SourceEvidence(reservation.Id, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Contains("original workbook request", json.GetProperty("sourceEvidencePayloadJson").GetString());
        Assert.Equal("message-1", json.GetProperty("sourceMessageId").GetString());
    }

    [Fact]
    public async Task Completed_load_creates_one_invoice_record_with_operational_order_lines()
    {
        await using var db = CreateDb();
        var order = new TransportOrder { Reference = "NWF-ORDER-1", CustomerCode = "NWF", CollectionDate = new DateOnly(2026, 9, 29), Pallets = 10, Status = OrderStatus.Delivered };
        var reservation = new BookingReservation { CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|2026-09-29|CRATE-1", CollectionDate = order.CollectionDate, ReservedUnits = 10, CompositionJson = "{}", Status = BookingReservationStatus.Assigned };
        var load = new Load { Reference = "AM-1", PlanningDate = new DateOnly(2026, 9, 29), Status = LoadStatus.Completed };
        load.Stops.Add(new LoadStop { LoadId = load.Id, OrderId = order.Id, Sequence = 1, Name = "NWF depot" });
        db.TransportOrders.Add(order);
        db.BookingReservations.Add(reservation);
        db.BookingReservationAllocations.Add(new BookingReservationAllocation { BookingReservationId = reservation.Id, TransportOrderId = order.Id, Units = 10 });
        db.Loads.Add(load);
        db.DriverStatusLogs.Add(new DriverStatusLog { LoadId = load.Id, Status = RunCompletionPersistenceGuard.CompletionEvidenceStatus, CapturedBy = "test" });
        await db.SaveChangesAsync();

        await InvoiceRecordService.EnsureDraftForCompletedLoadAsync(db, load, "planner", CancellationToken.None);
        await db.SaveChangesAsync();
        await InvoiceRecordService.EnsureDraftForCompletedLoadAsync(db, load, "planner", CancellationToken.None);
        await db.SaveChangesAsync();

        var record = Assert.Single(await db.InvoiceRecords.ToListAsync());
        Assert.Equal(InvoiceRecordStatus.Ready, record.Status);
        Assert.Equal(load.Id, record.LoadId);
        Assert.Equal(reservation.Id, record.BookingReservationId);
        var line = Assert.Single(await db.InvoiceRecordLines.ToListAsync());
        Assert.Equal(load.Id, line.LoadId);
        Assert.Equal(order.Id, line.TransportOrderId);
        Assert.Equal(reservation.Id, line.BookingReservationId);
        Assert.Contains(order.Reference, line.OperationalReferenceSnapshot);
        Assert.Equal("PreparedFromCompletedLoad", Assert.Single(await db.OperationalHistoryEvents.Select(item => item.EventType).ToListAsync()));
    }

    [Fact]
    public async Task Matching_is_capacity_checked_and_idempotent_for_the_same_transport_order()
    {
        await using var db = CreateDb();
        var reservation = new BookingReservation
        {
            CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|2026-09-29|CRATE-200",
            CollectionDate = new DateOnly(2026, 9, 29), ReservedUnits = 10, CompositionJson = "{}", Status = BookingReservationStatus.Confirmed
        };
        var order = new TransportOrder { Reference = "NWF-ORDER-200", CustomerCode = "NWF", CollectionDate = new DateOnly(2026, 9, 29), Pallets = 10, Status = OrderStatus.ReadyToPlan };
        db.BookingReservations.Add(reservation);
        db.TransportOrders.Add(order);
        await db.SaveChangesAsync();

        var controller = new BookingReservationsController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "planner") })) } };
        var first = await controller.MatchOrder(reservation.Id, new MatchBookingOrderRequest(order.Id), CancellationToken.None);
        Assert.IsType<OkObjectResult>(first);

        var second = await controller.MatchOrder(reservation.Id, new MatchBookingOrderRequest(order.Id), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(second);
        Assert.Single(await db.BookingReservationAllocations.ToListAsync());
        Assert.Equal(BookingReservationStatus.Assigned, (await db.BookingReservations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Planner_amendment_updates_capacity_and_keeps_revision_history()
    {
        await using var db = CreateDb();
        var reservation = new BookingReservation
        {
            CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|2026-09-29|AMEND-1",
            CollectionDate = new DateOnly(2026, 9, 29), CollectionReference = "CRATE-1", ReservedUnits = 12,
            CompositionJson = "{}", Status = BookingReservationStatus.PreOrder
        };
        db.BookingReservations.Add(reservation);
        await db.SaveChangesAsync();

        var controller = new BookingReservationsController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "planner") })) } };
        var result = await controller.Revise(reservation.Id, new ReviseBookingReservationRequest(
            CollectionDate: new DateOnly(2026, 9, 30), ReservedUnits: 10, CollectionReference: "CRATE-1-AMENDED",
            ChangeNote: "NWF amended the crate dump quantity and collection reference", SourceMessageId: "message-amend-1"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.BookingReservations.SingleAsync();
        Assert.Equal(new DateOnly(2026, 9, 30), saved.CollectionDate);
        Assert.Equal(10, saved.ReservedUnits);
        Assert.Equal("CRATE-1-AMENDED", saved.CollectionReference);
        Assert.Equal(BookingReservationStatus.Amended, saved.Status);
        Assert.Equal(2, saved.CurrentRevisionNumber);
        Assert.Contains("Amended", await db.OperationalHistoryEvents.Select(item => item.EventType).ToListAsync());
        Assert.Equal("message-amend-1", (await db.BookingReservationRevisions.OrderBy(item => item.RevisionNumber).LastAsync()).SourceMessageId);
    }

    [Fact]
    public async Task Load_history_chain_combines_dispatch_order_reservation_invoice_and_driver_events()
    {
        await using var db = CreateDb();
        var order = new TransportOrder { Reference = "NWF-CHAIN-1", CustomerCode = "NWF", CollectionDate = new DateOnly(2026, 9, 29), Status = OrderStatus.Planned };
        var reservation = new BookingReservation { CustomerCode = "NWF", BookingType = "NWF crate dump", StableBookingKey = "NWF|2026-09-29|CHAIN-1", CollectionDate = order.CollectionDate, ReservedUnits = 10, CompositionJson = "{}", Status = BookingReservationStatus.Assigned };
        var load = new Load { Reference = "SB-CHAIN-1", PlanningDate = order.CollectionDate, Status = LoadStatus.Dispatched };
        load.Stops.Add(new LoadStop { LoadId = load.Id, OrderId = order.Id, Sequence = 1, Name = "North collection" });
        var invoice = new InvoiceRecord { CustomerCode = "NWF", LoadId = load.Id, InvoiceDate = order.CollectionDate, Status = InvoiceRecordStatus.Ready, PayloadJson = "{}" };
        db.TransportOrders.Add(order);
        db.BookingReservations.Add(reservation);
        db.BookingReservationAllocations.Add(new BookingReservationAllocation { BookingReservationId = reservation.Id, TransportOrderId = order.Id, Units = 10 });
        db.Loads.Add(load);
        db.InvoiceRecords.Add(invoice);
        db.OperationalHistoryEvents.AddRange(
            new OperationalHistoryEvent { EntityType = "Load", EntityId = load.Id, EventType = "DispatchLocked", PayloadJson = "{}" },
            new OperationalHistoryEvent { EntityType = "TransportOrder", EntityId = order.Id, EventType = "AssignedToSouthboundReturnLoad", PayloadJson = "{}" },
            new OperationalHistoryEvent { EntityType = "BookingReservation", EntityId = reservation.Id, EventType = "MatchedToTransportOrder", PayloadJson = "{}" },
            new OperationalHistoryEvent { EntityType = "InvoiceRecord", EntityId = invoice.Id, EventType = "PreparedFromCompletedLoad", PayloadJson = "{}" });
        db.DriverStatusLogs.Add(new DriverStatusLog { LoadId = load.Id, Status = "Driver dispatched", Notes = "SMS sent", CapturedBy = "planner" });
        await db.SaveChangesAsync();

        var result = await new OperationalHistoryController(db).LoadChain(load.Id, CancellationToken.None);
        var response = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(response.Value);
        Assert.Contains("DispatchLocked", json);
        Assert.Contains("AssignedToSouthboundReturnLoad", json);
        Assert.Contains("MatchedToTransportOrder", json);
        Assert.Contains("PreparedFromCompletedLoad", json);
        Assert.Contains("Driver dispatched", json);
    }

    private static TmsDbContext CreateDb() => new(new DbContextOptionsBuilder<TmsDbContext>()
        .UseInMemoryDatabase($"booking-invoice-trail-{Guid.NewGuid():N}").Options);
}
