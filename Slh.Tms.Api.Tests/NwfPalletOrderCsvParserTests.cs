using System.Text;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class NwfPalletOrderCsvParserTests
{
    private readonly NwfPalletOrderCsvParser parser = new();

    [Fact]
    public void NwfCsv_StagesOnlyPositivePalletRows_AndUsesPoAsTmsReference()
    {
        const string csv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,2,PO00499461
Stuart Lyons,19/08/2026,Drayton,Tesco,ONE01,One Stop Tamworth,B78 1ST,SO000367751,8000053488,IPP STD,0,PO00499461
Stuart Lyons,19/08/2026,Selsey,Morrisons,MOR06,Morrisons FRUITBRIDGWATER 718,TA6 4FG,SO000367789,12345,IPP STD,9,PO00499461
""";
        var request = Request(csv, "message-19");

        var result = parser.TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(2, result.Orders.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("zero-pallet", StringComparison.OrdinalIgnoreCase));

        var first = result.Orders[0].Payload;
        Assert.Equal("NWF", first.GetProperty("customerCode").GetString());
        Assert.Equal("2026-08-19", first.GetProperty("collectionDate").GetString());
        Assert.Equal("Drayton", first.GetProperty("sellerName").GetString());
        Assert.Equal("Aldi SAWLEY Distribution Centre", first.GetProperty("stallNumber").GetString());
        Assert.Equal("DE72 2HP", first.GetProperty("deliveryAddress").GetString());
        Assert.Equal("SO000367762", first.GetProperty("salesOrderId").GetString());
        Assert.Equal("6511786146", first.GetProperty("customerRef").GetString());
        Assert.Equal("IPP Euro", first.GetProperty("palletName").GetString());
        Assert.Equal("PO00499461", first.GetProperty("poRef").GetString());
        Assert.Equal("PO00499461", first.GetProperty("customerPo").GetString());
        Assert.StartsWith("PO00499461/SO000367762/Drayton/ALD20", first.GetProperty("poNumber").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, first.GetProperty("pallets").GetInt32());
        Assert.Equal("NWF Pallet Order CSV", first.GetProperty("intakeParser").GetString());
        Assert.Contains(first.GetProperty("intakeMatchKeys").EnumerateArray().Select(item => item.GetString()),
            key => key is not null && key.Contains("PO:PO00499461", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(first.GetProperty("intakeMatchKeys").EnumerateArray().Select(item => item.GetString()),
            key => key is not null && key.Contains("SALES:SO000367762", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdatedCsv_KeepsStableNaturalKey_WhenQuantityChanges()
    {
        const string firstCsv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,2,PO00499461
""";
        const string updatedCsv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,4,PO00499461
""";

        var first = Assert.Single(parser.TryParse(Request(firstCsv, "message-a"))!.Orders);
        var updated = Assert.Single(parser.TryParse(Request(updatedCsv, "message-b"))!.Orders);

        Assert.Equal(first.NaturalKey, updated.NaturalKey);
        Assert.Equal(2, first.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal(4, updated.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void OutlookContentBytesAttachment_IsParsed()
    {
        const string csv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,2,PO00499461
""";

        var request = new MailboxEmailIntakeRequest(
            "message-content-bytes",
            null,
            "info@lyonshaulage.com",
            "ShiftLogisticalPlanner@nwfltd.co.uk",
            "Shift Logistical Planner",
            "NWAY Stuart Lyons Transport Pallet Order Report 19/08/2026",
            DateTimeOffset.Parse("2026-08-18T18:00:00Z"),
            "Please see attached pallet order report.",
            null,
            null,
            [new MailboxAttachmentRequest(
                "NWAY PALLET ORDER REPORT SLH.csv",
                "text/csv",
                null,
                false,
                ContentBytes: Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)))]);

        var order = Assert.Single(parser.TryParse(request)!.Orders);

        Assert.Equal("SO000367762", order.Payload.GetProperty("salesOrderId").GetString());
        Assert.Equal(2, order.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void NwfEmailBodyPipeTable_IsParsed_WhenAttachmentContentIsUnavailable()
    {
        const string body = """
Hello,

Please see below and attached.

Haulier Name| Requested Ship Date| 04. Collection Site| Customer Name| DepotID| Depot Description| Delivery Address| Sales Order ID| CustomerRef| Pallet Name| PalletQty| PO REF
---|---|---|---|---|---|---|---|---|---|---|---
Stuart Lyons| 25/08/2026| Drayton| Aldi| ALD20| Aldi SAWLEY Distribution Centre| DE72 2HP| SO000368395| 6511967794| IPP Euro| 2| PO00500547
Stuart Lyons| 25/08/2026| Selsey| Aldi| ALD26| Aldi ATHERSTONE Distribution Centre| CV9 2SQ| SO000368427| 6511972761| IPP Euro| 1| PO00500547
Stuart Lyons| 25/08/2026| Selsey| Tesco| ONE01| One Stop Tamworth| B78 1ST| SO000368432| 8000053695| IPP STD| 0| PO00500547
""";
        var request = new MailboxEmailIntakeRequest(
            "message-body-table",
            null,
            "info@lyonshaulage.com",
            "ShiftLogisticalPlanner@nwfltd.co.uk",
            "Shift Logistical Planner",
            "NWAY Stuart Lyons Transport Pallet Order Report 25/08/2026",
            DateTimeOffset.Parse("2026-08-24T13:26:16Z"),
            body,
            body,
            null,
            [new MailboxAttachmentRequest("NWAY Stuart Lyons Transport Pallet Order Report 25-08-2026.csv", "text/csv", null, false, Size: 4096)]);

        var result = parser.TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(2, result.Orders.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("zero-pallet", StringComparison.OrdinalIgnoreCase));
        var first = result.Orders[0].Payload;
        Assert.Equal("PO00500547/SO000368395/Drayton/ALD20", first.GetProperty("poNumber").GetString());
        Assert.Equal("NWF pallet order email body table.csv", first.GetProperty("sourceAttachmentName").GetString());
        Assert.Equal("Aldi SAWLEY Distribution Centre", first.GetProperty("stallNumber").GetString());
        Assert.Equal(2, first.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void Sep19MorrisonsBodySnapshot_ProducesAll22MovementsAnd106Pallets()
    {
        const string body = """
Hello,
Please see below and attached.
Haulier Name| Requested Ship Date| 04. Collection Site| Customer Name| DepotID| Depot Description| Delivery Address| Sales Order ID| CustomerRef| Pallet Name| PalletQty| PO REF
---|---|---|---|---|---|---|---|---|---|---|---
Stuart Lyons| 19/09/2026| Merston| Morrisons| MOR06| Morrisons FRUITBRIDGWATER 718| TA6 4FG| SO000370882| 91329115| IPP STD| 2| PO00504426
Stuart Lyons| 19/09/2026| Merston| Morrisons| MOR07| Morrisons FRUITGADBROOK 971| CW9 7WA| SO000370878| 91321935| IPP STD| 3| PO00504426
Stuart Lyons| 19/09/2026| Merston| Morrisons| MOR08| Morrisons FRUITLATIMER 952| NN15 5YT| SO000370880| 91324453| IPP STD| 3| PO00504426
Stuart Lyons| 19/09/2026| Merston| Morrisons| MOR09| Morrisons FRUITSITTINGBOURNE 763| ME10 2FD| SO000370885| 91329634| IPP STD| 3| PO00504426
Stuart Lyons| 19/09/2026| Merston| Morrisons| MOR11| Morrisons FRUITWAKEFIELD 990| WF2 0XF| SO000370884| 91329422| IPP STD| 3| PO00504426
Stuart Lyons| 19/09/2026| Merston| Morrisons| MOR18| Morrisons STOCKTON 994| TS18 2SZ| SO000370879| 91322045| IPP STD| 2| PO00504426
Stuart Lyons| 19/09/2026| Merston| Morrisons| MOR25| Morrisons DORDON 814| B78 1SE| SO000370902| 52333848| IPP STD| 1| PO00504426
Stuart Lyons| 19/09/2026| Runcton| Morrisons| MOR06| Morrisons FRUITBRIDGWATER 718| TA6 4FG| SO000370882| 91329115| IPP STD| 6| PO00504426
Stuart Lyons| 19/09/2026| Runcton| Morrisons| MOR07| Morrisons FRUITGADBROOK 971| CW9 7WA| SO000370878| 91321935| IPP STD| 7| PO00504426
Stuart Lyons| 19/09/2026| Runcton| Morrisons| MOR08| Morrisons FRUITLATIMER 952| NN15 5YT| SO000370880| 91324453| IPP STD| 9| PO00504426
Stuart Lyons| 19/09/2026| Runcton| Morrisons| MOR09| Morrisons FRUITSITTINGBOURNE 763| ME10 2FD| SO000370885| 91329634| IPP STD| 7| PO00504426
Stuart Lyons| 19/09/2026| Runcton| Morrisons| MOR10| Morrisons FRUITSTOCKTON 993| TS18 2SZ| SO000370881| 91325050| IPP STD| 1| PO00504426
Stuart Lyons| 19/09/2026| Runcton| Morrisons| MOR11| Morrisons FRUITWAKEFIELD 990| WF2 0XF| SO000370884| 91329422| IPP STD| 8| PO00504426
Stuart Lyons| 19/09/2026| Runcton| Morrisons| MOR18| Morrisons STOCKTON 994| TS18 2SZ| SO000370879| 91322045| IPP STD| 3| PO00504426
Stuart Lyons| 19/09/2026| Runcton| Morrisons| MOR25| Morrisons DORDON 814| B78 1SE| SO000370902| 52333848| IPP STD| 1| PO00504426
Stuart Lyons| 19/09/2026| Selsey| Morrisons| MOR06| Morrisons FRUITBRIDGWATER 718| TA6 4FG| SO000370882| 91329115| IPP STD| 7| PO00504426
Stuart Lyons| 19/09/2026| Selsey| Morrisons| MOR07| Morrisons FRUITGADBROOK 971| CW9 7WA| SO000370878| 91321935| IPP STD| 7| PO00504426
Stuart Lyons| 19/09/2026| Selsey| Morrisons| MOR08| Morrisons FRUITLATIMER 952| NN15 5YT| SO000370880| 91324453| IPP STD| 10| PO00504426
Stuart Lyons| 19/09/2026| Selsey| Morrisons| MOR09| Morrisons FRUITSITTINGBOURNE 763| ME10 2FD| SO000370885| 91329634| IPP STD| 10| PO00504426
Stuart Lyons| 19/09/2026| Selsey| Morrisons| MOR11| Morrisons FRUITWAKEFIELD 990| WF2 0XF| SO000370884| 91329422| IPP STD| 7| PO00504426
Stuart Lyons| 19/09/2026| Selsey| Morrisons| MOR18| Morrisons STOCKTON 994| TS18 2SZ| SO000370879| 91322045| IPP STD| 3| PO00504426
Stuart Lyons| 19/09/2026| Selsey| Morrisons| MOR25| Morrisons DORDON 814| B78 1SE| SO000370902| 52333848| IPP STD| 3| PO00504426
""";

        var request = new MailboxEmailIntakeRequest(
            "nwf-19-sep-live-shape",
            null,
            "info@lyonshaulage.com",
            "ShiftLogisticalPlanner@nwfltd.co.uk",
            "Shift Logistical Planner",
            "NWAY Stuart Lyons Transport Pallet Order Report 19/09/2026",
            DateTimeOffset.Parse("2026-09-18T05:37:05Z"),
            body,
            body,
            null,
            [new MailboxAttachmentRequest("NWAY Stuart Lyons Transport Pallet Order Report 19-09-2026.csv", "text/csv", null, false, Size: 8192)]);

        var result = parser.TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(22, result.Orders.Count);
        Assert.Equal(106, result.Orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.All(result.Orders, order => Assert.Equal("2026-09-19", order.Payload.GetProperty("collectionDate").GetString()));
        Assert.Equal(7, result.Orders.Count(order => order.Payload.GetProperty("sellerName").GetString() == "Merston"));
        Assert.Equal(8, result.Orders.Count(order => order.Payload.GetProperty("sellerName").GetString() == "Runcton"));
        Assert.Equal(7, result.Orders.Count(order => order.Payload.GetProperty("sellerName").GetString() == "Selsey"));
    }

    [Fact]
    public void Sep19OutlookWrappedBody_ReassemblesLogicalRows()
    {
        const string body = """
Hello,

Please see below and attached.

Haulier Name| Requested Ship Date| 04\\. Collection Site| Customer Name|
DepotID| Depot Description| Delivery Address| Sales Order ID| CustomerRef|
Pallet Name| PalletQty| PO REF
---|---|---|---|---|---|---|---|---|---|---|---
Stuart Lyons| 19/09/2026| Merston| Morrisons| MOR06| Morrisons FRUITBRIDGWATER
718| TA6 4FG| SO000370882| 91329115| IPP STD| 2| PO00504426
Stuart Lyons| 19/09/2026| Runcton| Morrisons| MOR09| Morrisons
FRUITSITTINGBOURNE 763| ME10 2FD| SO000370885| 91329634| IPP STD| 7|
PO00504426
Stuart Lyons| 19/09/2026| Runcton| Tesco| ONE01| One Stop Tamworth| B78 1ST|
SO000371055| 8000054552| IPP STD| 15| PO00504426
""";

        var request = new MailboxEmailIntakeRequest(
            "nwf-19-sep-outlook-wrapped",
            null,
            "info@lyonshaulage.com",
            "ShiftLogisticalPlanner@nwfltd.co.uk",
            "Shift Logistical Planner",
            "NWAY Stuart Lyons Transport Pallet Order Report 19/09/2026",
            DateTimeOffset.Parse("2026-09-18T10:09:13Z"),
            body,
            null,
            null,
            [new MailboxAttachmentRequest("NWAY report.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null, false, Size: 8192)]);

        var result = parser.TryParse(request);

        Assert.NotNull(result);
        Assert.Null(result!.IgnoredReason);
        Assert.Equal(3, result.Orders.Count);
        Assert.Equal(24, result.Orders.Sum(order => order.Payload.GetProperty("pallets").GetInt32()));
        Assert.Contains(result.Orders, order =>
            order.Payload.GetProperty("depotId").GetString() == "MOR06" &&
            order.Payload.GetProperty("depotDescription").GetString() == "Morrisons FRUITBRIDGWATER 718" &&
            order.Payload.GetProperty("pallets").GetInt32() == 2);
        Assert.Contains(result.Orders, order =>
            order.Payload.GetProperty("depotId").GetString() == "MOR09" &&
            order.Payload.GetProperty("depotDescription").GetString() == "Morrisons FRUITSITTINGBOURNE 763" &&
            order.Payload.GetProperty("pallets").GetInt32() == 7);
        Assert.Contains(result.Orders, order =>
            order.Payload.GetProperty("customerName").GetString() == "Tesco" &&
            order.Payload.GetProperty("pallets").GetInt32() == 15);
    }

    [Fact]
    public void ForwardedNwfBodyTable_IsAcceptedOnlyBecauseVerifiedTableSignatureIsPresent()
    {
        const string body = """
Forwarded message
NWAY Stuart Lyons Transport Pallet Order Report 19/09/2026
Haulier Name| Requested Ship Date| 04. Collection Site| Customer Name| DepotID| Depot Description| Delivery Address| Sales Order ID| CustomerRef| Pallet Name| PalletQty| PO REF
---|---|---|---|---|---|---|---|---|---|---|---
Stuart Lyons| 19/09/2026| Selsey| Tesco| ONE01| One Stop Tamworth| B78 1ST| SO000371055| 8000054552| IPP STD| 15| PO00504426
""";
        var request = new MailboxEmailIntakeRequest(
            "forwarded-nwf-table",
            null,
            "info@lyonshaulage.com",
            "gerone@lyonshaulage.com",
            "Gerone",
            "FW: NWAY Stuart Lyons Transport Pallet Order Report 19/09/2026",
            DateTimeOffset.Parse("2026-09-18T12:03:00Z"),
            body,
            null,
            null,
            null);

        var result = parser.TryParse(request);

        var order = Assert.Single(result!.Orders);
        Assert.Equal("Tesco", order.Payload.GetProperty("customerName").GetString());
        Assert.Equal(15, order.Payload.GetProperty("pallets").GetInt32());
    }

    [Fact]
    public void MissingPo_UsesSalesOrderOnlyAsFallbackAndFlagsReview()
    {
        const string csv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,19/08/2026,Drayton,Aldi,ALD20,Aldi SAWLEY Distribution Centre,DE72 2HP,SO000367762,6511786146,IPP Euro,2,
""";

        var row = Assert.Single(parser.TryParse(Request(csv, "message-no-po"))!.Orders);
        Assert.StartsWith("SO000367762/Drayton/ALD20", row.Payload.GetProperty("poNumber").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Medium", row.Payload.GetProperty("intakeConfidence").GetString());
        Assert.Contains(row.Payload.GetProperty("intakeWarnings").EnumerateArray().Select(item => item.GetString()),
            warning => warning is not null && warning.Contains("PO REF is missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NonNwfCustomerCsv_IsNotClassifiedAsNwf()
    {
        const string csv = """
Haulier Name,Requested Ship Date,04. Collection Site,Customer Name,DepotID,Depot Description,Delivery Address,Sales Order ID,CustomerRef,Pallet Name,PalletQty,PO REF
Stuart Lyons,14/09/2026,Sefter,NISA,NISA01,NISA depot,UK,SO123,REF1,IPP STD,1,PO123
""";

        var request = new MailboxEmailIntakeRequest(
            "message-barfoots-nisa", null, "info@lyonshaulage.com",
            "Gosia.Mydlak@barfoots.co.uk", "Gosia Mydlak",
            "NISA pallet booking for depot 14/09/26.",
            DateTimeOffset.Parse("2026-09-12T12:25:19Z"),
            "Please be advised that a total of 1 pallet space will be required to accommodate all three destinations.",
            null, null,
            [new MailboxAttachmentRequest(
                "NISA pallet booking.csv",
                "text/csv",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)),
                false)]);

        Assert.Null(parser.TryParse(request));
    }

    private static MailboxEmailIntakeRequest Request(string csv, string messageId) =>
        new(
            messageId,
            null,
            "info@lyonshaulage.com",
            "ShiftLogisticalPlanner@nwfltd.co.uk",
            "Shift Logistical Planner",
            "NWAY Stuart Lyons Transport Pallet Order Report 19/08/2026",
            DateTimeOffset.Parse("2026-08-18T18:00:00Z"),
            "Please see attached pallet order report.",
            null,
            null,
            [new MailboxAttachmentRequest(
                "NWAY PALLET ORDER REPORT SLH.csv",
                "text/csv",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)),
                false)]);
}
