using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeAutomaticSourceGateTests
{
    [Fact]
    public void MarketMessageIsOutsideAutomaticParserLane()
    {
        var request = Request("Wholesale Market Pallet Bookings", "Barfoots market workbook for Covent Garden.");

        Assert.False(Slh.Tms.Api.Controllers.OrderIntakeController.IsApprovedAutomaticSource(request));
    }

    [Theory]
    [InlineData("Waitrose confirmed booking", "Waitrose pallet order")]
    [InlineData("NWF collections", "Natures Way crate/tray collection")]
    [InlineData("IFCO booking", "IFCO crate collection")]
    [InlineData("Summer Berry load plan", "Summer Berry Southbound")]
    [InlineData("Barfoots Waitrose booking", "Sefter North to Waitrose")]
    public void ApprovedSourcesRemainInAutomaticParserLane(string subject, string body)
    {
        Assert.True(Slh.Tms.Api.Controllers.OrderIntakeController.IsApprovedAutomaticSource(Request(subject, body)));
    }

    [Fact]
    public void UnknownAdHocMessageIsOutsideAutomaticParserLane()
    {
        Assert.False(Slh.Tms.Api.Controllers.OrderIntakeController.IsApprovedAutomaticSource(
            Request("Small customer booking", "Please collect 4 pallets on 05/10/2026.")));
    }

    [Fact]
    public void LangmeadCoopBookingWithProformaAttachmentIsApproved()
    {
        var request = new MailboxEmailIntakeRequest(
            "langmead-coop-test", null, "info@lyonshaulage.com", "Alex@langmeadherbs.co.uk", "Alex",
            "Ham Farm to CO-OP Transport MON 12/10/2026 depot.", DateTimeOffset.Parse("2026-10-11T08:31:00Z"),
            "Please find attached Ham Farm Langmead Herbs CO-OP booking form for Monday 12/10/2026.", null, null,
            [new MailboxAttachmentRequest("CO-OP ORDER PROFORMA MON 12.10.2026 depot.xls", "application/vnd.ms-excel", "AA==")]);

        Assert.True(Slh.Tms.Api.Controllers.OrderIntakeController.IsApprovedAutomaticSource(request));
    }

    [Fact]
    public void SainsburyHaulierPlanSenderAndSubjectAreApproved()
    {
        var request = new MailboxEmailIntakeRequest(
            "sainsbury-plan-test", null, "info@lyonshaulage.com", "Backhaul.Requests@sainsburys.co.uk", "Backhaul",
            "Transport plan for STUART LYONS for 12/10/2026 - Version 1", DateTimeOffset.Parse("2026-10-11T13:09:00Z"),
            "Please find attached the transport plan.", null, null,
            [new MailboxAttachmentRequest("Haulier Plan.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "AA==")]);

        Assert.True(Slh.Tms.Api.Controllers.OrderIntakeController.IsApprovedAutomaticSource(request));
    }

    [Theory]
    [InlineData("stuart@lyonshaulage.com")]
    [InlineData("MicrosoftExchange329e71ec88ae4615bbc36ab6ce41109e@lyonshaulage.com")]
    [InlineData("planner@stuartlyonshaulage.co.uk")]
    public void InternalOutboundSendersAreRecognised(string sender)
    {
        Assert.True(Slh.Tms.Api.Controllers.OrderIntakeController.IsInternalSender(sender));
    }

    [Theory]
    [InlineData("sender@external.example")]
    [InlineData("sender@notlyonshaulage.com")]
    public void ExternalSendersAreNotRecognisedAsInternal(string sender)
    {
        Assert.False(Slh.Tms.Api.Controllers.OrderIntakeController.IsInternalSender(sender));
    }

    [Theory]
    [InlineData("{\"customerCode\":\"COOP\"}")]
    [InlineData("{\"customerCode\":\"BARFOOTS\",\"stallNumber\":\"Nisa Retail Ltd (Aylesford)\"}")]
    [InlineData("{\"customerCode\":\"BARFOOTS\",\"destination\":\"Covent Garden Market\"}")]
    [InlineData("{\"customerCode\":\"INDEPENDENT\"}")]
    public void Manual_only_customer_rows_are_excluded_from_automation(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        Assert.True(Slh.Tms.Api.Controllers.OrderIntakeController.IsManualInputOrder(document.RootElement));
    }

    [Fact]
    public void Approved_retailer_row_is_not_classified_as_manual_only()
    {
        using var document = JsonDocument.Parse("{\"customerCode\":\"WAITROSE\",\"stallNumber\":\"Leyland\"}");
        Assert.False(Slh.Tms.Api.Controllers.OrderIntakeController.IsManualInputOrder(document.RootElement));
    }

    private static MailboxEmailIntakeRequest Request(string subject, string body) => new(
        "source-gate-test", null, "info@lyonshaulage.com", "planner@example.test", "Planner",
        subject, DateTimeOffset.Parse("2026-10-05T07:00:00Z"), body, null, null, null);
}
