using System.Text.Json;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeSourceEvidenceTests
{
    [Fact]
    public void EnrichSourceEvidence_RetainsReviewableEmailPreviewWithoutAttachmentBytes()
    {
        // Production defect caught: a staged order cannot be traced back to the
        // exact message when the flow envelope is accepted but the email body is discarded.
        var payload = JsonSerializer.SerializeToElement(new { customerCode = "COOP", pallets = 26 });
        using var toDocument = JsonDocument.Parse("[{\"address\":\"info@lyonshaulage.com\"}]");
        using var ccDocument = JsonDocument.Parse("[{\"address\":\"planner@example.com\"}]");
        var request = new MailboxEmailIntakeRequest(
            "outlook-id", "<internet-id@example>", "info@lyonshaulage.com", "orders@example.com", "Orders Team",
            "PO 123", DateTimeOffset.Parse("2026-08-22T08:00:00Z"), "Collect 26 pallets from Sefter for Waitrose.", "<p>Collect 26 pallets from Sefter for Waitrose.</p>",
            "https://outlook.example/message", [new("order.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "SECRET-BYTES", false, "cid-1", 2048)],
            "conversation-1", toDocument.RootElement.Clone(), ccDocument.RootElement.Clone(), "html", "high", "correlation-1");

        var enriched = OrderIntakeController.EnrichSourceEvidence(payload, request);

        Assert.Equal("conversation-1", enriched.GetProperty("sourceConversationId").GetString());
        Assert.Equal("outlook-id", enriched.GetProperty("sourceMessageId").GetString());
        Assert.Equal("PO 123", enriched.GetProperty("sourceSubject").GetString());
        Assert.Equal("https://outlook.example/message", enriched.GetProperty("sourceWebLink").GetString());
        Assert.Equal("correlation-1", enriched.GetProperty("importCorrelationId").GetString());
        Assert.Equal("Pending Review", enriched.GetProperty("reviewStatus").GetString());
        Assert.Contains("Collect 26 pallets from Sefter", enriched.GetProperty("sourceBodyText").GetString());
        Assert.False(enriched.GetProperty("sourceBodyPreviewTruncated").GetBoolean());
        Assert.StartsWith("email-evidence:", enriched.GetProperty("sourceEvidenceKey").GetString());
        var attachment = Assert.Single(enriched.GetProperty("sourceAttachments").EnumerateArray());
        Assert.Equal("order.xlsx", attachment.GetProperty("name").GetString());
        Assert.Equal(2048, attachment.GetProperty("size").GetInt64());
        Assert.DoesNotContain("SECRET-BYTES", enriched.GetRawText());
    }

    [Fact]
    public void EnrichSourceEvidence_ConvertsHtmlToPlainPreviewWhenBodyTextIsMissing()
    {
        var payload = JsonSerializer.SerializeToElement(new { customerCode = "WAITROSE" });
        var request = new MailboxEmailIntakeRequest(
            "message-html", null, "info@lyonshaulage.com", "customer@example.com", "Customer",
            "Collection instruction", DateTimeOffset.Parse("2026-09-08T09:00:00Z"), null,
            "<p>Please collect <strong>2 pallets</strong> from Sefter.</p>", null, []);

        var enriched = OrderIntakeController.EnrichSourceEvidence(payload, request);

        Assert.Contains("Please collect 2 pallets from Sefter.", enriched.GetProperty("sourceBodyText").GetString());
    }
}
