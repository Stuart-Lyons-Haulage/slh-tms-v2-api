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

    private static MailboxEmailIntakeRequest Request(string subject, string body) => new(
        "source-gate-test", null, "info@lyonshaulage.com", "planner@example.test", "Planner",
        subject, DateTimeOffset.Parse("2026-10-05T07:00:00Z"), body, null, null, null);
}
