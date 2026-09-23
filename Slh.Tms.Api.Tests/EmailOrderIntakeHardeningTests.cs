using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmailOrderIntakeHardeningTests
{
    private readonly EmailOrderIntakeService service = new();

    [Fact]
    public void LangmeadSubdomainSender_MatchesVerifiedRootDomain()
    {
        Assert.True(EmailOrderIntakeService.SenderAddressMatchesDomain(
            "planner@ops.langmeadherbs.co.uk",
            "langmeadherbs.co.uk"));
    }

    [Fact]
    public void LookalikeDomain_DoesNotMatchLangmeadRootDomain()
    {
        Assert.False(EmailOrderIntakeService.SenderAddressMatchesDomain(
            "planner@langmeadherbs.co.uk.evil.example",
            "langmeadherbs.co.uk"));
    }

    [Fact]
    public void MondayWholesaleDelivery_FlagsInferredSundayCollectionForPlannerReview()
    {
        var rows = new List<object?[]>
        {
            new object?[] { "COLLECTION Sefter", "", "", "", "" },
            new object?[] { "Market", "Customer", "Delivery addess", "Pallets", "Delivery Date" },
            new object?[] { "New Covent Garden", "Premier Foods", "PFW01", "2", "14/09/2026" }
        };
        var request = new MailboxEmailIntakeRequest(
            "market-monday", null, "info@lyonshaulage.com", "mariela.popova@barfoots.co.uk", "Mariela Popova",
            "Wholesale Market Pallet Bookings for delivery on 14/09/26", DateTimeOffset.Parse("2026-09-11T10:00:00Z"),
            "Wholesale Market Pallet Bookings", null, null, null);

        var order = Assert.Single(EmailOrderIntakeService.ParseBarfootsWholesaleMarketRows(
            request, "Wholesale Market Pallet Bookings.xlsx", "Sheet1", rows));

        Assert.Equal("2026-09-13", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-09-14", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Contains(order.Warnings, warning => warning.Contains("Sunday", StringComparison.OrdinalIgnoreCase));
    }
}
