using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MailboxFullBodyTests
{
    [Fact]
    public void TangmereMarketBody_ParsesSevenDropsAndAllFortyEightPallets()
    {
        var request = Email("Tangmere markets 06/09", "Please collecty 48pt from tangmere today", "<p>Please collecty 48pt from tangmere today</p>" +
            "<p>15pt Fresh import spit</p><p>15pt sunfresh spit</p><p>3pt m&amp;m spit</p><p>5pt quality spit</p>" +
            "<p>5pt waldon spit</p><p>2pt m&amp;a western</p><p>3pt universal western</p>")
            with { SenderAddress = "planner@pmtransport.co.uk" };
        var result = new SpecialistMailboxOrderParser().TryParse(request);
        Assert.NotNull(result);
        Assert.Equal(7, result!.Orders.Count);
        Assert.Equal(48, result.Orders.Sum(o => o.Payload.GetProperty("pallets").GetInt32()));
        Assert.Equal(5, result.Orders.Count(o => o.Payload.GetProperty("marketName").GetString() == "Spit"));
        Assert.Equal(2, result.Orders.Count(o => o.Payload.GetProperty("marketName").GetString() == "Western"));
        Assert.All(result.Orders, o => Assert.False(o.Payload.GetProperty("plannerReady").GetBoolean()));
        Assert.Equal(7, result.Orders.Select(o => o.NaturalKey).Distinct().Count());
    }

    [Fact]
    public void TangmereMarketBody_MissingDropDoesNotSilentlyCreatePartialOrder()
    {
        var request = Email("Tangmere markets 06/09", null!, "<p>Please collecty 48pt from tangmere today</p><p>15pt Fresh import spit</p>")
            with { SenderAddress = "planner@pmtransport.co.uk" };
        var result = new SpecialistMailboxOrderParser().TryParse(request);
        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
        Assert.NotNull(result.IgnoredReason);
    }

    [Fact]
    public void CoventGarden_FullHtmlIncludesDropsBeyondPreview()
    {
        var request = Email("Covent Garden deliveries 07/09/2026", "Please see tomorrow's deliveries.",
            "<div>Collection: 07/09/2026 from 16:00:</div><div>APS Produce</div>" +
            "<p>I A Harris - 1 pallet</p><p>Kale &amp; Damson - 2 pallets</p><p>Primeur - 3 pallets</p>");
        var result = new SpecialistMailboxOrderParser().TryParse(request);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Orders.Count);
        Assert.Equal(new[] { 1, 2, 3 }, result.Orders.Select(o => o.Payload.GetProperty("pallets").GetInt32()));
        Assert.Equal("Kale & Damson", result.Orders[1].Payload.GetProperty("stallNumber").GetString());
    }

    [Fact]
    public void Spitalfields_FullHtmlIncludesBothDropsBeyondPreview()
    {
        var request = Email("C & J Hayward", "Morning, please collect today.",
            "<p>Please pick up the following pallets today for delivery tonight:</p>" +
            "<p>5 to Kemsley - Spitalfields</p><p>1 to Jenni International - Spitalfields</p><p>Many thanks</p>");
        var result = new EmailOrderIntakeService().Parse(request);
        Assert.Equal(2, result.Orders.Count);
        Assert.All(result.Orders, o => Assert.Equal("Spitalfields", o.Payload.GetProperty("stallNumber").GetString()));
        Assert.Equal(new[] { 5, 1 }, result.Orders.Select(o => o.Payload.GetProperty("pallets").GetInt32()));
    }

    [Fact]
    public void HtmlTables_PreserveRowsAndCellBoundariesWithoutDuplicatingPreview()
    {
        var body = MailboxBodyNormalizer.Normalize("Depot | PO | Pallets", "<style>ignored</style>" +
            "<table><tr><td>Depot</td><td>PO</td><td>Pallets</td></tr>" +
            "<tr><td>Bracknell</td><td>A12345</td><td>4</td></tr></table>");
        Assert.Equal("Depot | PO | Pallets\nBracknell | A12345 | 4", body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body> </body></html>")]
    public void MissingOrEmptyHtml_FallsBackToText(string? html)
        => Assert.Equal("Full plain text body", MailboxBodyNormalizer.Normalize("Full plain text body", html));

    private static MailboxEmailIntakeRequest Email(string subject, string preview, string html) => new(
        "full-body-regression", null, "info@lyonshaulage.com", "planner@example.com", "Planner",
        subject, DateTimeOffset.Parse("2026-09-06T10:00:00Z"), preview, html, null, null);
}
