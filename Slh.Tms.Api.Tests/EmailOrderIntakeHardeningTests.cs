using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmailOrderIntakeHardeningTests
{
    private readonly EmailOrderIntakeService service = new();

    [Theory]
    [InlineData("planner@lyonshaulage.com", "Lyons Collections-12102026", "orders.xlsx", true)]
    [InlineData("planner@lyonshaulage.com", "SB for 12.10.2026", "SB - Morrisons & Aldi Bookings.xlsm", true)]
    [InlineData("planner@lyonshaulage.com", "NWF transfer", "transfer.xlsx", false)]
    [InlineData("orders@customer.example", "Lyons Collections-12102026", "orders.xlsx", false)]
    public void InternalPlannerSnapshots_AreClassifiedAsComparisonEvidence(string sender, string subject, string attachment, bool expected)
    {
        var request = new MailboxEmailIntakeRequest(
            "internal-plan", null, "info@lyonshaulage.com", sender, "Planner", subject,
            DateTimeOffset.UtcNow, null, null, null,
            [new MailboxAttachmentRequest(attachment, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null, false)]);

        Assert.Equal(expected, Slh.Tms.Api.Controllers.OrderIntakeController.IsInternalPlannerSnapshot(request));
    }

    [Fact]
    public void ForwardedCustomerInstruction_IsInboundAndUsesExternalSender()
    {
        var request = new MailboxEmailIntakeRequest(
            "forward-1", null, "info@lyonshaulage.com", "planner@lyonshaulage.com", "Planner", "FW: Aldi collection",
            DateTimeOffset.UtcNow,
            "From: bookings@barfoots.co.uk\nSent: Tuesday, 20 October 2026\nSubject: Aldi collection\n\nPlease collect 18 pallets from Sefter for Aldi Atherstone.",
            null, null, null);

        var result = OrderSourceDirectionClassifier.Classify(request);

        Assert.Equal(OrderSourceDirection.ForwardedInbound, result.Direction);
        Assert.Equal("bookings@barfoots.co.uk", result.EffectiveSender);
        Assert.True(result.IsOrderCandidate);
        Assert.StartsWith("Please collect", OrderSourceDirectionClassifier.ExtractForwardedInstruction(request.BodyText));
    }

    [Fact]
    public void CustomerReplyWithQuotedLyonsPlan_RemainsInbound()
    {
        var request = new MailboxEmailIntakeRequest(
            "reply-1", null, "info@lyonshaulage.com", "bookings@barfoots.co.uk", "Bookings", "RE: Lyons Collections",
            DateTimeOffset.UtcNow,
            "Please add 4 pallets for Aldi Bolton.\n\nFrom: planner@lyonshaulage.com\nSent: yesterday\nSubject: Lyons Collections\n[quoted plan]",
            null, null, null);

        var result = OrderSourceDirectionClassifier.Classify(request);

        Assert.Equal(OrderSourceDirection.Inbound, result.Direction);
        Assert.True(result.IsOrderCandidate);
    }

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
