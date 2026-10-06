using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalWorkflowHttpTests(CustomWebFactory factory) : IClassFixture<CustomWebFactory>
{
    [Fact]
    public async Task Authenticated_booking_match_history_and_invoice_routes_share_one_operational_chain()
    {
        var load = new Load { Reference = $"HTTP-CHAIN-{Guid.NewGuid():N}"[..20], PlanningDate = new DateOnly(2026, 9, 29), Status = LoadStatus.Dispatched };
        var movement = new OrderMovement { CustomerCode = "ALDI", StableMovementKey = $"ALDI:HTTP-CRATE-{Guid.NewGuid():N}" };
        var revision = new OrderRevision { MovementId = movement.Id, StagedImportId = Guid.NewGuid(), RevisionNumber = 1, PayloadJson = "{\"jobType\":\"IFCO tray collection\",\"customerPo\":\"HTTP-CRATE-1\"}" };
        movement.CurrentRevisionId = revision.Id;
        var order = new TransportOrder { Reference = $"HTTP-ORDER-{Guid.NewGuid():N}"[..24], CustomerCode = "ALDI", CollectionDate = load.PlanningDate, Pallets = 8, Status = OrderStatus.ReadyToPlan, SourceMovementId = movement.Id };
        load.Stops.Add(new LoadStop { LoadId = load.Id, OrderId = order.Id, Sequence = 1, Name = "NWF collection" });
        var invoice = new InvoiceRecord { CustomerCode = "NWF", LoadId = load.Id, InvoiceDate = load.PlanningDate, Status = InvoiceRecordStatus.Ready, PayloadJson = "{}" };

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Loads.Add(load);
            db.AddRange(movement, revision, order);
            db.InvoiceRecords.Add(invoice);
            db.OperationalHistoryEvents.Add(new OperationalHistoryEvent { EntityType = "Load", EntityId = load.Id, EventType = "DispatchLocked", PayloadJson = "{}" });
            db.OperationalHistoryEvents.Add(new OperationalHistoryEvent { EntityType = "InvoiceRecord", EntityId = invoice.Id, EventType = "PreparedFromCompletedLoad", PayloadJson = "{}" });
            db.DriverStatusLogs.Add(new DriverStatusLog { LoadId = load.Id, Status = "Driver dispatched", Notes = "HTTP test message", CapturedBy = "planner" });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var create = await client.PostAsJsonAsync("/api/v1/booking-reservations", new
        {
            customerCode = "NWF", bookingType = "NWF crate dump", collectionDate = load.PlanningDate,
            reservedUnits = 8, stableBookingKey = $"HTTP|{load.Id:N}", collectionReference = "HTTP-CRATE-1"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var reservationId = created.GetProperty("id").GetGuid();

        var match = await client.PostAsJsonAsync($"/api/v1/booking-reservations/{reservationId}/match-order", new { transportOrderId = order.Id });
        Assert.Equal(HttpStatusCode.OK, match.StatusCode);

        var chain = await client.GetFromJsonAsync<JsonElement>($"/api/v1/operational-history/load/{load.Id}");
        var chainText = chain.GetRawText();
        Assert.Contains("MatchedToTransportOrder", chainText);
        Assert.Contains("DispatchLocked", chainText);
        Assert.Contains("Driver dispatched", chainText);
        Assert.Contains("PreparedFromCompletedLoad", chainText);

        var invoiceHistory = await client.GetFromJsonAsync<JsonElement>($"/api/v1/invoice-history?loadId={load.Id}");
        Assert.Contains("PreparedFromCompletedLoad", invoiceHistory.GetRawText());
    }
}
