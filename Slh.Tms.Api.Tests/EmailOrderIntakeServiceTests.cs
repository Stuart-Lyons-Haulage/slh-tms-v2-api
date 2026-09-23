using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmailOrderIntakeServiceTests
{
    private readonly EmailOrderIntakeService service = new();

    [Fact]
    public void InternalLyonsPlannerEmail_IsIgnored()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-1", null, "info@lyonshaulage.com", "joe@lyonshaulage.com", "Joe",
            "Lyons Collections-18082026", DateTimeOffset.Parse("2026-08-17T15:00:00Z"),
            "Please find attached Load plan for tomorrow.", null, null, null));

        Assert.Empty(result.Orders);
        Assert.NotNull(result.IgnoredReason);
    }

    [Fact]
    public void BarfootsReadyForCollectionStatus_DoesNotCreateAnotherOrder()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-waitrose-ready", null, "info@lyonshaulage.com", "Goods.INNV@barfoots.co.uk", "Goods IN NV",
            "Waitrose", DateTimeOffset.Parse("2026-09-08T11:59:40Z"),
            "Waitrose is ready to be collected from Leythorne.", null, "https://outlook.example/status", null));

        Assert.Empty(result.Orders);
        Assert.Contains("Operational request", result.IgnoredReason);
    }

    [Fact]
    public void TrayCollectionBody_CreatesPendingOrderShape()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-tray", null, "info@lyonshaulage.com", "Ioana-Andreea.Pascalau@summerberry.co.uk", "Ioana",
            "Tray collection Northampton 18/08", DateTimeOffset.Parse("2026-08-17T07:20:05Z"),
            "Could you please organise collection for the below\nNorthampton\n18/08\nTHE359/348310", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("TSBC", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-18", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("Northampton", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("THE359/348310/NORTHAMPTON", order.Payload.GetProperty("poNumber").GetString());
        Assert.Equal("Tray collection", order.Payload.GetProperty("jobType").GetString());
    }

    [Fact]
    public void NisaBodyCollectionPoint_OverridesAttachmentOrTemplateFallback()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-nisa-body-precedence", null, "info@lyonshaulage.com",
            "orders@barfoots.co.uk", "Barfoots",
            "NISA pallet booking 12/09/2026", DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
            "Collection point: Leythorne\nPlease book 4 pallets to Aylesford for delivery on 12/09/2026.",
            null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("BARFOOTS LEYTHORNE", order.Payload.GetProperty("sellerName").GetString()?.ToUpperInvariant());
        Assert.Equal(4, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Contains("body.explicit", order.Payload.GetProperty("intakeFieldSources").GetProperty("collectionSite").GetString());
    }

    [Fact]
    public void SummerBerryCoopBody_ExtractsPalletsTimeTemperatureAndDate()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-coop", null, "info@lyonshaulage.com", "Ioana-Andreea.Pascalau@summerberry.co.uk", "Ioana",
            "TSBC COOP - 18.08.2026", DateTimeOffset.Parse("2026-08-17T09:53:57Z"),
            "Please find attached pallet requirements. Total Pallets : 2 Collection time: 17:00 Transport at +3 degrees Collect from: TSBC, Chichester", null, null, null),
            ["TSBC CO-OP"]);

        var order = Assert.Single(result.Orders);
        Assert.Equal("COOP", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal(2, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("Summer Berry", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("TSBC CO-OP", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Contains("Requested time: 17:00", order.Payload.GetProperty("driverInstructions").GetString());
        Assert.Contains("Temperature: +3°C", order.Payload.GetProperty("driverInstructions").GetString());
    }

    [Fact]
    public void SummerBerryLabelledPalletBody_IsStaged()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-summerberry-spinneys", null, "info@lyonshaulage.com", "Ioana-Andreea.Pascalau@summerberry.co.uk", "Ioana",
            "27.08.2026 Spinneys/JHF delivery", DateTimeOffset.Parse("2026-08-26T07:10:15Z"),
            """
            Customer : Spinneys/JHF order
            Depot Date: 27.08.2026
            Collection : 26.08.2026- 17:00
            Pallets : 15
            Adress of delivery : K&N facility
            Building 1235, Eastern Perimeter Road,
            London Heathrow Airport,
            Hounslow,
            TW6 2SQ
            To be there not later than 8AM on the Depot Date
            """, null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("SPINNEYS/JHF", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Spinneys/JHF", order.Payload.GetProperty("marketName").GetString());
        Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-08-27", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("Summer Berry", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("K&N facility", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(15, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("17:00", order.Payload.GetProperty("requestedTime").GetString());
        Assert.Equal("08:00", order.Payload.GetProperty("deliveryRequestedTime").GetString());
        Assert.Equal("Not later than", order.Payload.GetProperty("deliveryTimeConstraint").GetString());
        Assert.Contains("TW6 2SQ", order.Payload.GetProperty("deliveryAddress").GetString());
        Assert.Equal("High", order.Payload.GetProperty("intakeConfidence").GetString());
        Assert.Contains(order.Warnings, warning => warning.Contains("inferred as Summer Berry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HillBrothersHamsHallTrolleyBody_IsStaged()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-hams-hall", null, "info@lyonshaulage.com", "eva@hillsplants.com", "Eva Jaskulska",
            "Hams Hall - depot THU", DateTimeOffset.Parse("2026-08-25T06:54:52Z"),
            "Good morning,\nHams Hall order for depot Thursday 27th Aug is 19 trolleys.\nHills collection.\nKind regards,\nEva", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("HILLBROTHERS", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-27", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("Hams Hall", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("Hill Brothers", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal(19, order.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void LangmeadsDeliveryToBody_IsStagedWithCollectionTime()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-langmeads-zenith", null, "info@lyonshaulage.com", "EmiliaMargas@langmeadherbs.co.uk", "Emilia Margas",
            "Delivery to Zenith Nurseries  26.08.2026", DateTimeOffset.Parse("2026-08-25T07:59:51Z"),
            "Good morning\nCould you please collect below for delivery to Zenith Nurseries - Station Road, Evesham, WR11 8LW.\n* Wednesday (26.08.2026) collection, the same day delivery - 16 pallets\nCollection 8 am.\nLangmead Herbs Limited\nHam Farm, Main Road, Bosham", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("LANGMEADS", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("Ham Farm", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Zenith Nurseries", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(16, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Contains("Requested time: 08:00", order.Payload.GetProperty("driverInstructions").GetString());
    }

    [Fact]
    public void LangmeadHamFarmBooking_IsStagedAsLangmeadsOrder()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "langmead-ham-09092026", null, "info@lyonshaulage.com", "AndrejsLupins@langmeadherbs.co.uk", "Andrejs Lupins",
            "Ham Farm to NISA Transport WED 09/09/2026 depot", DateTimeOffset.Parse("2026-09-08T13:02:00Z"),
            "Good afternoon, Please find attached Ham Farm Langmead Herbs booking form for NISA transport WED 09/09/2026 depot. Product will be ready for collection at 16:30. 2 pallets. Collection from Ham Farm.", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("LANGMEADS", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Ham Farm", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal(2, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-09-09", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Contains("16:30", order.Payload.GetProperty("driverInstructions").GetString());
    }

    [Fact]
    public void WaltonFarmAldiBooking_IsStagedAsLangmeadsOrder()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "walton-aldi-09092026", null, "info@lyonshaulage.com", "ewa@langmeadherbs.co.uk", "Ewa Kuszczak",
            "Aldi Order 09/09/2026", DateTimeOffset.Parse("2026-09-08T12:41:00Z"),
            "Please find attached the Walton Farm booking forms for Aldi transport 09/09/2026 depot day. 2 pallets from Walton Farm.", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("LANGMEADS", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Walton Farm", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Aldi", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(2, order.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void HallHunterDirectDepotDelivery_UsesSeparateCollectionAndDeliveryDates()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-hhp-waitrose", null, "info@lyonshaulage.com", "chris.benning@primafruit.co.uk", "Chris Benning",
            "HHP WAITROSE DIRECT DEPOT DELIVERY Wednesday 26/08/26", DateTimeOffset.Parse("2026-08-25T08:36:23Z"),
            "Please collect 4 pallets from Hall Hunter today Tuesday 25/08/2026.\n* Leyland 4 pallets\nFor Delivery date Wednesday 26/08/2026.\nPO number: A59997. 174 cases of Berries.", null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("WAITROSE", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-25", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-08-26", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("Hall Hunter", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Leyland", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(4, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("A59997", order.Payload.GetProperty("customerPo").GetString());
    }

    [Fact]
    public void WaitroseDepotTable_StagesEachDepotRow()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-waitrose-table", null, "info@lyonshaulage.com", "Norbert.Horvath@fowlerwelch.co.uk", "Norbert",
            "Waitrose 26/08/26", DateTimeOffset.Parse("2026-08-25T05:09:06Z"),
            "DELIVERY DATE | 26/08/2026\nDEPOT | PO NUMBER | PALLET COUNT\nAYLESFORD | X58797 | 3\nBRACKNELL | J58987 | 2\nBRINKLOW | R58889 | 2\nLEYLAND | Z57709 | 1", null, null, null));

        Assert.Equal(4, result.Orders.Count);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Aylesford" && order.Payload.GetProperty("pallets").GetInt32() == 3);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Leyland" && order.Payload.GetProperty("pallets").GetInt32() == 1);
        Assert.All(result.Orders, order => Assert.Equal("2026-08-26", order.Payload.GetProperty("deliveryDate").GetString()));
    }

    [Fact]
    public void DoubleHWaitroseColumnTable_StagesEachDepotColumn()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-doubleh-waitrose", null, "info@lyonshaulage.com", "Ramas@doubleh.co.uk", "Ramas",
            "Waitrose order for dd 27.08 from Double H", DateTimeOffset.Parse("2026-08-26T07:07:37Z"),
            """
            Please see below Waitrose order for depot date 27.08 (collection today 26.08)

            Depot date| 27.08| | | |
             | Aylesford| Bracknell| Brinklow| Leyland|
            Order Ref| X58520| J58713| R58625| Z57428| TOTAL
            Trolleys| 21| 40| 39| 23| 123
            """, null, null, null));

        Assert.Equal(4, result.Orders.Count);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Aylesford" && order.Payload.GetProperty("pallets").GetInt32() == 21);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("stallNumber").GetString() == "Leyland" && order.Payload.GetProperty("customerPo").GetString() == "Z57428");
        Assert.All(result.Orders, order =>
        {
            Assert.Equal("WAITROSE", order.Payload.GetProperty("customerCode").GetString());
            Assert.Equal("Double H", order.Payload.GetProperty("sellerName").GetString());
            Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
            Assert.Equal("2026-08-27", order.Payload.GetProperty("deliveryDate").GetString());
        });
    }

    [Fact]
    public void BarfootsWaitroseChainedWaves_StageEachWaveWithInheritedCollectionSite()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-barfoots-waitrose-0909", null, "info@lyonshaulage.com", "Agnieszka.Zawislan@barfoots.co.uk", "Agnieszka Zawislan",
            "Waitrose from Sefter & Leythorne for depot 09/09/26", DateTimeOffset.Parse("2026-09-08T09:49:54Z"),
            """
            Please see attached Waitrose confirmed pallet booking:
            Aylesford WAVE 1 from Sefter 2 pallets PO O78442 & Aylesford Wave 3 6 pallets PO O78431.
            Leyland WAVE 1 from Sefter 1 pallet PO B78737 & Leyland Wave 3 2 pallets PO B78749.
            """, null, "https://outlook.example/waitrose", null),
            ["Sefter", "Leythorne", "Aylesford", "Leyland"]);

        Assert.Equal(4, result.Orders.Count);
        Assert.Equal(new[] { "B78737", "B78749", "O78431", "O78442" },
            result.Orders.Select(order => order.Payload.GetProperty("customerPo").GetString()).Order().ToArray());
        Assert.All(result.Orders, order =>
        {
            Assert.Equal("WAITROSE", order.Payload.GetProperty("customerCode").GetString());
            Assert.Equal("Sefter", order.Payload.GetProperty("sellerName").GetString());
            Assert.Equal("2026-09-09", order.Payload.GetProperty("deliveryDate").GetString());
            Assert.Equal("message-barfoots-waitrose-0909", order.Payload.GetProperty("sourceMessageId").GetString());
        });

        var waveOne = result.Orders.Where(order => order.Payload.GetProperty("wave").GetInt32() == 1).ToList();
        var waveThree = result.Orders.Where(order => order.Payload.GetProperty("wave").GetInt32() == 3).ToList();
        Assert.Equal(2, waveOne.Count);
        Assert.Equal(2, waveThree.Count);
        Assert.All(waveOne, order =>
        {
            Assert.Equal("2026-09-08", order.Payload.GetProperty("collectionDate").GetString());
            Assert.False(order.Payload.GetProperty("overnightRoute").GetBoolean());
            Assert.Equal("SameDay", order.Payload.GetProperty("routeTiming").GetString());
        });
        Assert.All(waveThree, order =>
        {
            Assert.Equal("2026-09-08", order.Payload.GetProperty("collectionDate").GetString());
            Assert.True(order.Payload.GetProperty("overnightRoute").GetBoolean());
            Assert.Equal("17:00", order.Payload.GetProperty("requestedTime").GetString());
            Assert.Equal("Overnight", order.Payload.GetProperty("routeTiming").GetString());
        });
    }

    [Fact]
    public void BarfootsWaitroseAmendment_KeepsStableIdentityWhenPalletsChange()
    {
        EmailIntakeParseResult Parse(string messageId, int pallets) => service.Parse(new MailboxEmailIntakeRequest(
            messageId, null, "info@lyonshaulage.com", "Agnieszka.Zawislan@barfoots.co.uk", "Agnieszka Zawislan",
            "Waitrose from Sefter for depot 09/09/26", DateTimeOffset.Parse("2026-09-08T09:49:54Z"),
            $"Aylesford WAVE 1 from Sefter {pallets} pallets PO O78442.", null, null, null),
            ["Sefter", "Aylesford"]);

        var original = Assert.Single(Parse("message-original", 2).Orders);
        var amended = Assert.Single(Parse("message-amended", 3).Orders);

        Assert.Equal("WAITROSE", amended.Payload.GetProperty("customerCode").GetString());
        Assert.Equal(original.NaturalKey, amended.NaturalKey);
        Assert.NotEqual(original.Payload.GetProperty("pallets").GetInt32(), amended.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void BarfootsWholesaleWorkbook_KeepsPhysicalMarketSeparateFromMarketCustomer()
    {
        var request = new MailboxEmailIntakeRequest(
            "message-wholesale-0909", null, "info@lyonshaulage.com", "Mariela.Popova@barfoots.co.uk", "Mariela Popova",
            "Wholesale Market Pallet Bookings for delivery on 09/09/26", DateTimeOffset.Parse("2026-09-08T10:28:35Z"),
            "Please find attached the Wholesale Market pallet bookings for delivery 09/09/26.", null,
            "https://outlook.example/markets", null);
        var rows = new List<object?[]>
        {
            new object?[] { "COLLECTION SEFTER", null, null, null, null, null, null, null },
            new object?[] { "Market", "Customer", "Delivery addess", "Delivery Date", "Delivery Time", "Temp.", "No. of Pallets", "SO" },
            new object?[] { "NEW COVENT GARDEN", "Premier Foods Exotics Dept. (PFW01)", "Units 417-418 New Covent Garden Market SW8 5EQ", new DateTime(2026, 9, 9), "by 1-3am.", "+3", 2d, 3550871d },
            new object?[] { null, "Premier Foods Veg & Salad Dept. (PFW02)", "Units 301-313 New Covent Garden Market SW8 5EQ", null, "by 1-3am.", "+3", 8d, "3549110/ 3550872" },
            new object?[] { "NEW SPITALFIELDS MATKET", "Canim Fruit & Veg Ltd(CANIM)", "Stand 64-65 New Spitalfields Market E10 5SH", null, "by 1-3am.", "+3", 3d, null },
            new object?[] { "COLLECTION LEYTHORNE", null, null, null, null, null, null, null },
            new object?[] { "NEW SPITALFIELDS MATKET", "Canim Fruit & Veg Ltd(CANIM)", "Stand 64-65 New Spitalfields Market E10 5SH", new DateTime(2026, 9, 9), "by 1-3am.", "+3", 3d, null }
        };

        var orders = EmailOrderIntakeService.ParseBarfootsWholesaleMarketRows(request, "LYONS - Wholesale Booking Spreadsheet 09.09.xlsx", "Sheet1", rows);

        Assert.Equal(4, orders.Count);
        var covent = Assert.Single(orders, order => order.Payload.GetProperty("customerPo").GetString() == "3550871");
        Assert.Equal("BARFOOTS", covent.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Sefter", covent.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("New Covent Garden", covent.Payload.GetProperty("marketName").GetString());
        Assert.Equal("Premier Foods Exotics Dept. (PFW01)", covent.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("Units 417-418 New Covent Garden Market SW8 5EQ", covent.Payload.GetProperty("deliveryAddress").GetString());
        Assert.Equal("Sheet1", covent.Payload.GetProperty("sourceSheet").GetString());
        Assert.Equal(3, covent.Payload.GetProperty("sourceRow").GetInt32());
        Assert.Equal(2, orders.Count(order => order.Payload.GetProperty("sellerName").GetString() == "Sefter" && order.Payload.GetProperty("marketName").GetString() == "New Covent Garden"));
        Assert.Single(orders, order => order.Payload.GetProperty("sellerName").GetString() == "Leythorne");
    }

    [Fact]
    public void InternalMorrisonsBridgwaterEmail_StagesEachCollectionLine()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-internal-bridgwater", null, "info@lyonshaulage.com", "michael@lyonshaulage.com", "Michael Lyons",
            "Bridgewater load tomorrow", DateTimeOffset.Parse("2026-08-25T16:39:38Z"),
            """
            Load Number 20.

            Tomorrow, please be at your first collection site for 06:30. Please plan your start time accordingly.

            Collections list:
            Merston  8p  Morrisons-Bridgwater
            Runcton  8p  Morrisons-Bridgwater
            Selsey  9p  Morrisons-Bridgwater

            Once loaded please deliver to Morrisons-Bridgwater.

            Morrisons-Bridgwater booking ref: X01392278
            """, null, null, null));

        Assert.Equal(3, result.Orders.Count);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("sellerName").GetString() == "Merston" && order.Payload.GetProperty("pallets").GetInt32() == 8);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("sellerName").GetString() == "Selsey" && order.Payload.GetProperty("pallets").GetInt32() == 9);
        Assert.All(result.Orders, order =>
        {
            Assert.Equal("MORRISONS", order.Payload.GetProperty("customerCode").GetString());
            Assert.Equal("Morrisons-Bridgwater", order.Payload.GetProperty("stallNumber").GetString());
            Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
            Assert.Equal("2026-08-26", order.Payload.GetProperty("deliveryDate").GetString());
            Assert.Equal("06:30", order.Payload.GetProperty("requestedTime").GetString());
            Assert.Equal("X01392278", order.Payload.GetProperty("customerPo").GetString());
        });
    }

    [Fact]
    public void VitacressWaitroseLeylandReply_StagesOnwardDepotDelivery()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-vitacress-leyland", null, "info@lyonshaulage.com", "kay@lyonshaulage.com", "Kay Ryan",
            "RE: Leyland", DateTimeOffset.Parse("2026-08-26T09:32:30Z"),
            """
            Morning Maciej

            Booked in for you

            From: Maciej Rybitwa <Maciej.Rybitwa@vitacress.com>
            To: Waitrose Primary Transport; info <info@lyonshaulage.com>
            Subject: Leyland

            Morning
            We will drop 7 plts into Bracknell tomorrow 27/08/26 around 07.30 for your onward delivery to Leyland.
            """, null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("WAITROSE", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-27", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-08-27", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("Bracknell", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Leyland", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(7, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("07:30", order.Payload.GetProperty("requestedTime").GetString());
    }

    [Fact]
    public void PmTransportAdditionalMarketEmail_StagesShortMarketAmendmentForReview()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-additional-market", null, "info@lyonshaulage.com", "Keiran@PMTransport.co.uk", "Keiran",
            "Additional market", DateTimeOffset.Parse("2026-08-26T09:33:04Z"),
            """
            Another 3pt sunstar spit please

            All pallets will be ready about 6pm,

            They may get 11pt ready for about 5 if needed
            """, null, null, null));

        var order = Assert.Single(result.Orders);
        Assert.Equal("PMTRANSPORT", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("Sunstar", order.Payload.GetProperty("sellerName").GetString());
        Assert.Equal("Spitalfields", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(3, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("18:00", order.Payload.GetProperty("requestedTime").GetString());
        Assert.Contains(order.Warnings, warning => warning.Contains("11 pallets may be ready", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SimpleTodayTonightSplitBody_StagesEachLine()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-cj-hayward", null, "info@lyonshaulage.com", "debhayward@yahoo.com", "Debbie Hayward",
            "C & J Hayward", DateTimeOffset.Parse("2026-08-25T07:54:56Z"),
            "Morning,\nPlease can you pick up the following pallets today for delivery tonight:\n5 to Kemsley - Spitalfields\n1 to Jenni International - Spitalfields\nMany thanks", null, null, null));

        Assert.Equal(2, result.Orders.Count);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("sellerName").GetString() == "Kemsley" && order.Payload.GetProperty("pallets").GetInt32() == 5);
        Assert.Contains(result.Orders, order => order.Payload.GetProperty("sellerName").GetString() == "Jenni International" && order.Payload.GetProperty("pallets").GetInt32() == 1);
        Assert.All(result.Orders, order =>
        {
            Assert.Equal("2026-08-25", order.Payload.GetProperty("collectionDate").GetString());
            Assert.Equal("2026-08-25", order.Payload.GetProperty("deliveryDate").GetString());
            Assert.Equal("Spitalfields", order.Payload.GetProperty("stallNumber").GetString());
        });
    }

    [Theory]
    [InlineData("Langmeads Aldi booking 26/08", "Please book delivery to Aldi Atherstone. Collection 8 am.", "LANGMEADS")]
    [InlineData("Barfoots Morrisons delivery 26/08", "Please find attached correct pallet booking.", "BARFOOTS")]
    [InlineData("Natures Way Waitrose 26/08", "Waitrose booking confirmed for tomorrow.", "WAITROSE")]
    public void RecognisedSupplierOrSupermarket_WithDateAndNoQuantity_IsNotStagedAsZeroPalletOrder(
        string subject,
        string body,
        string expectedCustomer)
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            $"message-low-detail-{expectedCustomer}", null, "info@lyonshaulage.com", "loads@example.com", "Loads",
            subject, DateTimeOffset.Parse("2026-08-25T07:00:00Z"),
            body, null, null, null));

        Assert.Empty(result.Orders);
        Assert.Contains("No transport order", result.IgnoredReason);
        Assert.Contains(result.Warnings, warning => warning.Contains("not enough order detail", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DatedBodyWithoutOrderFields_IsNotStagedAsZeroPalletFallback()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-vague", null, "info@lyonshaulage.com", "loads@example.com", "Loads",
            "Available work 26/08", DateTimeOffset.Parse("2026-08-24T09:00:00Z"),
            "Can you look at these from tomorrow. Pallets may be exchanged.", null, null, null));

        Assert.Empty(result.Orders);
        Assert.Contains("No transport order", result.IgnoredReason);
        Assert.Contains(result.Warnings, warning => warning.Contains("not enough order detail", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Sainsburys order 26/08", "Please arrange 12 pallets.", null, "SAINSBURY", "Sainsbury")]
    [InlineData("Collection request 26/08", "Waitrose Bracknell needs 8 pallets.", null, "WAITROSE", "Waitrose")]
    [InlineData("Collection request 26/08", "Please arrange 10 pallets.", "Natures Way collections.xlsx", "NWF", "Natures Way")]
    [InlineData("Collection request 26/08", "Barfoots Sefter 6 pallets.", null, "BARFOOTS", "Barfoots")]
    [InlineData("Weightrose order 26/08", "Please arrange 4 pallets.", null, "WAITROSE", "Waitrose")]
    public void RecognisedCustomerOrSite_WithDateAndPallets_IsStagedForReview(
        string subject,
        string body,
        string? attachmentName,
        string expectedCustomer,
        string expectedSite)
    {
        var attachments = attachmentName is null
            ? null
            : new List<MailboxAttachmentRequest> { new(attachmentName, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null, false) };

        var result = service.Parse(new MailboxEmailIntakeRequest(
            $"message-{expectedCustomer}-{expectedSite}", null, "info@lyonshaulage.com", "loads@example.com", "Loads",
            subject, DateTimeOffset.Parse("2026-08-24T09:00:00Z"),
            body, null, null, attachments));

        var order = Assert.Single(result.Orders);
        Assert.Equal(expectedCustomer, order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal(expectedSite, order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("2026-08-26", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Contains(order.Warnings, warning => warning.Contains("Collection site was not explicit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MasterDataSiteName_WithDateAndPallets_IsStagedForReview()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-master-site", null, "info@lyonshaulage.com", "loads@example.com", "Loads",
            "Collection request 26/08", DateTimeOffset.Parse("2026-08-24T09:00:00Z"),
            "Sainsbury Waltham Point has 16 pallets for collection.", null, null, null),
            ["Sainsbury Waltham Point"]);

        var order = Assert.Single(result.Orders);
        Assert.Equal("SAINSBURY", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal("Sainsbury Waltham Point", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal(16, order.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void NightShunting_IsNotMisclassifiedAsOrder()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-shunt", null, "info@lyonshaulage.com", "DanielLawes@nwfltd.co.uk", "Daniel Lawes",
            "NWF Night Shunting", DateTimeOffset.Parse("2026-08-17T12:07:47Z"),
            "Please confirm if you can cover the night shift again this evening.", null, null, null));

        Assert.Empty(result.Orders);
        Assert.Contains("Operational request", result.IgnoredReason);
    }

    [Fact]
    public void AvailableLoadsMailshot_IsIgnoredAsOperationalNoise()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-monarch-loads", null, "info@lyonshaulage.com", "mailshot@monarchtransport.co.uk", "Monarch Transport",
            "URGENT - Monarch Transport Available Loads", DateTimeOffset.Parse("2026-08-25T06:16:39Z"),
            "Monarch Available Loads. Can you cover the below loads? You are receiving this email because you opted in via our site.",
            null, null, null));

        Assert.Empty(result.Orders);
        Assert.Contains("Operational request", result.IgnoredReason);
    }

    [Theory]
    [InlineData("BARTRUMS AVAILABLE LOADS", "We currently have the following full load work available. If you are interested and able to assist, please contact us.")]
    [InlineData("Inbound ETA's - 25-08-2026", "Good morning, Please find attached ETA's.")]
    [InlineData("LOADS AVAILABLE  - MUST BE OWN VEHICLE", "Please see below load available - please let us know if you can assist.")]
    public void OperationalNoiseSubjects_AreIgnored(string subject, string body)
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            $"message-noise-{subject}", null, "info@lyonshaulage.com", "loads@example.com", "Loads",
            subject, DateTimeOffset.Parse("2026-08-25T08:00:00Z"),
            body, null, null, null));

        Assert.Empty(result.Orders);
        Assert.Contains("Operational request", result.IgnoredReason);
    }

    [Fact]
    public void AndoverAndAvonmouthAttachmentStyle_IsSplitIntoTwoOrders()
    {
        var result = service.Parse(new MailboxEmailIntakeRequest(
            "message-depot-split", null, "info@lyonshaulage.com", "loads@example.com", "Leythorne",
            "Waitrose depot collections 09/09/26", DateTimeOffset.Parse("2026-09-08T09:00:00Z"),
            "Please arrange 1 pallet to Andover and 2 pallets to Avonmouth for 09/09/26.",
            null, null, null));

        Assert.Equal(2, result.Orders.Count);
        Assert.Equal(new[] { "Andover", "Avonmouth" }, result.Orders.Select(x => x.Payload.GetProperty("stallNumber").GetString()).OrderBy(x => x));
        Assert.Equal(new[] { 1, 2 }, result.Orders.Select(x => x.Payload.GetProperty("pallets").GetInt32()).OrderBy(x => x));
    }

    [Fact]
    public void WholesaleMarketWithoutCollectionDate_StagesOvernightDepartureOnPreviousDay()
    {
        var rows = new List<object?[]>
        {
            new[] { "COLLECTION Sefter", "", "", "", "" },
            new[] { "Market", "Customer", "Delivery addess", "Pallets", "Delivery Date" },
            new[] { "New Covent Garden", "Premier Foods", "PFW01", "2", "10/09/2026" }
        };
        var request = new MailboxEmailIntakeRequest(
            "message-market-overnight", null, "info@lyonshaulage.com", "mariela.popova@barfoots.co.uk", "Mariela Popova",
            "Wholesale Market Pallet Bookings for delivery on 10/09/26", DateTimeOffset.Parse("2026-09-09T10:00:00Z"),
            "Wholesale Market Pallet Bookings", null, null, null);

        var order = Assert.Single(EmailOrderIntakeService.ParseBarfootsWholesaleMarketRows(request, "Wholesale Market Pallet Bookings.xlsx", "Sheet1", rows));
        Assert.Equal("2026-09-09", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-09-10", order.Payload.GetProperty("deliveryDate").GetString());
    }
}
