using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SpecialistMailboxOrderParserTests
{
    private readonly SpecialistMailboxOrderParser parser = new();

    [Fact]
    public void Vitacress_waitrose_collection_rows_preserve_dates_time_amendment_and_total()
    {
        var rows = new List<object?[]>
        {
            new object?[] { null, "COLLECTION DATE", new DateTime(2026, 9, 8), "Waitrose PO number", "DELIVERY DATE", new DateTime(2026, 9, 9) },
            new object?[] { null, "Aylesford", 4d, "O78436", new DateTime(1899, 12, 30, 16, 0, 0), null },
            new object?[] { null, "Bracknell", 5d, "K78676", new DateTime(1899, 12, 30, 16, 0, 0), null },
            new object?[] { null, "Brinklow", 5d, "T78405", new DateTime(1899, 12, 30, 16, 0, 0), null },
            new object?[] { null, "Leyland", 3d, "B78729", new DateTime(1899, 12, 30, 16, 0, 0), null },
            new object?[] { null, "TOTAL", 17d, null, null, "AMENDED" }
        };

        var result = WaitroseLegacyWorkbookParser.ParseVitacress(rows);

        Assert.Equal(4, result.Count);
        Assert.Equal(17, result.Sum(x => x.Pallets));
        Assert.All(result, x => Assert.Equal(new DateOnly(2026, 9, 8), x.CollectionDate));
        Assert.All(result, x => Assert.Equal(new DateOnly(2026, 9, 9), x.DeliveryDate));
        Assert.All(result, x => Assert.Equal(new TimeOnly(16, 0), x.ReadyTime));
        Assert.All(result, x => Assert.True(x.IsAmendment));
    }

    [Fact]
    public void Aps_weekly_waitrose_matrix_creates_one_order_per_nonzero_depot()
    {
        var rows = new List<object?[]>
        {
            new object?[] { "Week Commencing SUNDAY", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 10d, 9d, 2026d },
            new object?[] { "Depot day", "SUNDAY", null, null, null, "MONDAY", null, null, null, "TUESDAY", null, null, null, "WEDNESDAY" },
            new object?[] { null, "Bracknell", "Brinklow", "Aylesford", "Leyland", "Bracknell", "Brinklow", "Aylesford", "Leyland", "Bracknell", "Brinklow", "Aylesford", "Leyland" },
            new object?[] { "Number of Pallets", "N/A", "N/A", "N/A", "N/A", 11d, 7d, 6d, 4d, 11d, 6d, 5d, 4d },
            new object?[] { "Time Ready @ WSL", "19;00", "19;00", "19;00", "19;00", "19;00", "19;00", "19;00", "19;00", "19;00", "19;00", "19;00", "19;00" },
            new object?[] { "PO Number", "N/A", "N/A", "N/A", "N/A", "K78631", "T78343", "O78368", "B78667", "K78659", "T78357", "O78396", "B78713" }
        };

        var result = WaitroseLegacyWorkbookParser.ParseApsWeekly(rows, new DateOnly(2026, 9, 8));

        Assert.Equal(8, result.Count);
        Assert.Equal(54, result.Sum(x => x.Pallets));
        Assert.DoesNotContain(result, x => x.Po == "N/A");
        Assert.All(result, x => Assert.Equal(new TimeOnly(19, 0), x.ReadyTime));
        Assert.Equal(new DateOnly(2026, 9, 7), result.First(x => x.Depot == "Bracknell" && x.Po == "K78631").DeliveryDate);
    }

    [Fact]
    public void Cancellation_DoesNotCreateNewOrder()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "cancel-1", null, "info@lyonshaulage.com", "MalwinaFrasek@nwfltd.co.uk", "Malwina Frasek",
            "IFCO Glasshoughton 285349425 - cancelled 18.08", DateTimeOffset.Parse("2026-08-17T12:00:00Z"),
            "Due to tray wash shortage load ref Glasshoughton 285349425 has been cancelled 18.08 IFCO PO00500400", null, null, null));

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
        Assert.NotNull(result.IgnoredReason);
    }

    [Fact]
    public void AmazonBody_UsesSeparateCollectionAndDeliveryDates()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "amazon-1", null, "info@lyonshaulage.com", "Kevin.Nicholls@iowtomatoes.co.uk", "Kevin Nicholls",
            "Amazon Delivery Wednesday 19th August", DateTimeOffset.Parse("2026-08-17T12:44:20Z"),
            "Please see below Amazon for delivery 19/08/2026.\nBooking Ref: 22246489013\nCollection: Tuesday 18th August from 16:00:\nAPS Produce\nChichester Food Park\nDelivery Wednesday 19th August:\nAmazon ALT2 - MILTON KEYNES\nALT2\n2 pallets TOTAL WEIGHT 973kgs", null, null, null));

        var order = Assert.Single(result!.Orders);
        Assert.Equal("AMAZON", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-18", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-08-19", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("22246489013", order.Payload.GetProperty("customerPo").GetString());
        Assert.Equal(2, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("APS Produce", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Amazon ALT2 - MILTON KEYNES", order.Payload.GetProperty("stallNumber").GetString());
    }

    [Fact]
    public void CoventGardenBody_CreatesOneOrderPerDrop()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "covent-1", null, "info@lyonshaulage.com", "Kevin.Nicholls@iowtomatoes.co.uk", "Kevin Nicholls",
            "Covent Garden Deliveries Tues 18th August/Wed 19th August", DateTimeOffset.Parse("2026-08-17T12:40:55Z"),
            "Collection: Tuesday 18th August from 16:00:\nAPS Produce\nDelivery Tuesday evening/Wednesday morning 18th August/19th August:\nI A Harris - 1 pallet, 186kg\nKale & Damson - 1 pallet, 203kg\nPrimeur - 2 pallets, 575kg", null, null, null));

        Assert.Equal(3, result!.Orders.Count);
        Assert.Equal(new[] { 1, 1, 2 }, result.Orders.Select(order => order.Payload.GetProperty("pallets").GetInt32()).ToArray());
        Assert.All(result.Orders, order => Assert.Equal("2026-08-18", order.Payload.GetProperty("collectionDate").GetString()));
        Assert.All(result.Orders, order => Assert.Equal("2026-08-19", order.Payload.GetProperty("deliveryDate").GetString()));
    }

    [Fact]
    public void RouteSubject_ExtractsFromToReferenceAndGenericPalletCount()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "transfer-1", null, "info@lyonshaulage.com", "Kamila.Biohn@barfoots.co.uk", "Kamila Biohn",
            "Collection from Milton Keynes to Sefter 18.08, 285351670", DateTimeOffset.Parse("2026-08-17T11:00:00Z"),
            "38 pallets Please deliver on the same day IFCO SYSTEMS", null, null, null));

        var order = Assert.Single(result!.Orders);
        Assert.Equal("IFCO", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Milton Keynes", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Sefter", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(38, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-08-18", order.Payload.GetProperty("collectionDate").GetString());
    }

    [Fact]
    public void RouteTransferSubject_ExtractsMerstonDraytonCollectionRoute()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "transfer-2", null, "info@lyonshaulage.com", "planner@example.com", "Planner",
            "Merston to Drayton transfers for collections - 25-08-2026", DateTimeOffset.Parse("2026-08-24T14:00:00Z"),
            "Test email for today's transfer collection import.", null, null, null));

        var order = Assert.Single(result!.Orders);
        Assert.Equal("NWF", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Merston", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Drayton", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("2026-08-25", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-08-25", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("Collection transfer", order.Payload.GetProperty("jobType").GetString());
        Assert.True(order.Payload.GetProperty("pallets").ValueKind is System.Text.Json.JsonValueKind.Null);
        Assert.Contains("Pallet quantity was not identified.", order.Warnings);
    }

    [Fact]
    public void NwfTransferSubject_ExtractsBarnhamDraytonReferenceAndPlts()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "transfer-nwf-1", null, "info@lyonshaulage.com", "PackagingPlanner@nwfltd.co.uk", "Packaging Planner",
            "NWF transfer - Barnham to Drayton SUN 20/09", DateTimeOffset.Parse("2026-09-18T07:04:36Z"),
            "@D_Drayton Logistics - please receive on arrival = INTO000173010\n20/09/2026 | 25FPPCOLTOV2 | V1 | 35,840 | 2plts = All stock", null, null, null));

        var order = Assert.Single(result!.Orders);
        Assert.Equal("NWF", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Barnham", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Drayton", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("2026-09-20", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal(2, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Contains("INTO000173010", order.Payload.GetProperty("poNumber").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NwfConfirmedAldiCollection_SplitsCollectionSitesExactly()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "nwf-aldi-confirmed-1", null, "info@lyonshaulage.com", "MariuszUrbanski@nwfltd.co.uk", "Mariusz Urbanski",
            "Aldi Bedford - confirmation for 19/09", DateTimeOffset.Parse("2026-09-17T13:23:55Z"),
            """
            Please see confirmed ALDI trays collection for 19/09
            ALDI| PO00505088 | £195.87| 19/09/2026| 20/09/2026| 26| Bedford| 228419235| PO00503669 | Merston / Runcton| 33| Merston 18 plt / Runcton 15/ plt
            ALDI| PO00505089 | £195.87| 19/09/2026| 20/09/2026| 26| Bedford| 228419486| PO00503677 | Selsey| 33|
            """, null, null, null));

        Assert.NotNull(result);
        Assert.Equal(3, result!.Orders.Count);
        Assert.Equal(66, result.Orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("sellerName").GetString() == "Merston" && order.Payload.GetProperty("pallets").GetInt32() == 18);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("sellerName").GetString() == "Runcton" && order.Payload.GetProperty("pallets").GetInt32() == 15);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("sellerName").GetString() == "Selsey" && order.Payload.GetProperty("pallets").GetInt32() == 33);
        Assert.All(result.Orders, order => Assert.Equal("2026-09-20", order.Payload.GetProperty("deliveryDate").GetString()));
    }

    [Fact]
    public void IfcoConfirmedCollectionsBody_CreatesCrateTrayCollectionRows()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "ifco-1", null, "info@lyonshaulage.com", "MalwinaFrasek@nwfltd.co.uk", "Malwina Frasek",
            "IFCO confirmed collections for 26/08", DateTimeOffset.Parse("2026-08-24T12:02:53Z"),
            """
            Good afternoon,

            Please find attached IFCO collection for 26/8

            IFCO| PO00501528 | £551.75| 26/08/2026| 27/08/2026| 22| IFCO Glasshoughton| 285362822| PO00501064 | Runcton| 26| Please use new reference - Order moved from Covenry to Glasshoughton
            ---|---|---|---|---|---|---|---|---|---|---|---
            IFCO| TBC| £0.00| 26/08/2026| 27/08/2026| 22| TBC|  | PO00501076 | Selsey| 17|
            IFCO| PO00501686 | £551.75| 26/08/2026| same day | 22| IFCO Glasshoughton| 285362754| PO00501074 | Selsey| 26|
            """, null, null, null));

        Assert.NotNull(result);
        Assert.Equal(3, result!.Orders.Count);

        var first = result.Orders[0].Payload;
        Assert.Equal("IFCO", first.GetProperty("customerCode").GetString());
        Assert.Equal("IFCO crate/tray collection", first.GetProperty("jobType").GetString());
        Assert.Equal("2026-08-26", first.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-08-27", first.GetProperty("deliveryDate").GetString());
        Assert.Equal("IFCO Glasshoughton", first.GetProperty("sellerName").GetString());
        Assert.Equal("Runcton", first.GetProperty("stallNumber").GetString());
        Assert.Equal("PO00501528", first.GetProperty("transportPo").GetString());
        Assert.Equal("PO00501064", first.GetProperty("cratePo").GetString());
        Assert.Equal("285362822", first.GetProperty("collectionReference").GetString());
        Assert.Equal(26, first.GetProperty("pallets").GetInt32());
        Assert.True(first.GetProperty("plannerReady").GetBoolean());

        var pending = result.Orders[1].Payload;
        Assert.False(pending.GetProperty("plannerReady").GetBoolean());
        Assert.Equal("PendingReview", pending.GetProperty("intakeStatus").GetString());
        Assert.Contains("Transport PO is TBC.", pending.GetProperty("intakeWarnings").EnumerateArray().Select(item => item.GetString()));

        var sameDay = result.Orders[2].Payload;
        Assert.Equal("2026-08-26", sameDay.GetProperty("deliveryDate").GetString());
        Assert.Equal("Selsey", sameDay.GetProperty("stallNumber").GetString());
    }

    [Fact]
    public void WaitroseHallHunterBody_StagesSingleCollectionAndDeliveryMovement()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "waitrose-hhp-1", null, "info@lyonshaulage.com", "chris.benning@primafruit.co.uk", "Chris Benning",
            "HHP WAITROSE DIRECT DEPOT DELIVERY Wednesday 16/09/26", DateTimeOffset.Parse("2026-09-15T07:58:00Z"),
            """
            Good morning,

            Please collect 3 pallets from Hall Hunter today Tuesday 15/09/2026.

            *   Leyland 3 pallets

            For Delivery date Wednesday 16/09/2026.

            PO number: A65026. 119 cases of Berries.
            """, null, null, null));

        var order = Assert.Single(result!.Orders);
        Assert.Equal("WAITROSE", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("A65026", order.Payload.GetProperty("customerPo").GetString());
        Assert.Equal("2026-09-15", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-09-16", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("Hall Hunter", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Leyland", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(3, order.Payload.GetProperty("pallets").GetInt32());
        Assert.True(order.Payload.GetProperty("plannerReady").GetBoolean());
    }

    [Fact]
    public void ApsDoleSubwayBody_UsesDdAsDeliveryDateAndDestinationAddress()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "aps-dole-1", null, "info@lyonshaulage.com", "Katarzyna.Jalowiec@apsgroup.uk.com", "Kasia Jalowiec",
            "Oliver Kay D.D. 16.09.2026", DateTimeOffset.Parse("2026-09-15T06:43:00Z"),
            """
            Good morning,

            Please see address for Dole Subway:

            Oliver Kay Hoddesdon
            Bingley Road
            Unit A
            HODDESDON
            EN11 0NX
            UNITED KINGDOM

            For D.D. 16.09.2026 will be 4 pallets.
            """, null, null, null));

        var order = Assert.Single(result!.Orders);
        Assert.Equal("APS", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("APS Produce", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Oliver Kay Hoddesdon", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("2026-09-16", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal(4, order.Payload.GetProperty("pallets").GetInt32());
        Assert.True(order.Payload.GetProperty("plannerReady").GetBoolean());
    }

    [Fact]
    public void CoopAttachmentOnlyEmail_DoesNotStageZeroPalletPlaceholder()
    {
        var result = parser.TryParse(new MailboxEmailIntakeRequest(
            "coop-attachment-1", null, "info@lyonshaulage.com", "Mariela.Popova@barfoots.co.uk", "Mariela Popova",
            "Confirmed COOP pallet booking", DateTimeOffset.Parse("2026-09-15T06:54:00Z"),
            "Good morning,\n\nPlease find attached the confirmed COOP pallet booking for delivery tomorrow.",
            null, null,
            [new MailboxAttachmentRequest(null, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null, false)]));

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
        Assert.NotNull(result.IgnoredReason);
        Assert.Contains("attachment", result.IgnoredReason, StringComparison.OrdinalIgnoreCase);
    }
}
