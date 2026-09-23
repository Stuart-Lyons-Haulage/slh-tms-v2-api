using System.Text.Json;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class InfoMailboxEvidenceUpgradeTests
{
    [Fact]
    public void UpgradeEvidencePayload_ReplacesHtmlBodyTextAndAddsMissingAttachmentCopy()
    {
        var existing = JsonSerializer.Serialize(new
        {
            messageId = "msg-1",
            senderAddress = "orders@example.com",
            senderName = "orders@example.com",
            bodyText = "<html><body><p>Collect 4 pallets</p></body></html>",
            bodyHtml = "<html><body><p>Collect 4 pallets</p></body></html>",
            bodyFormat = "true",
            attachments = new[]
            {
                new
                {
                    name = "order.xlsx",
                    contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    size = 2048,
                    isInline = false,
                    contentId = (string?)null,
                    contentBase64 = ""
                }
            },
            evidenceAvailable = true
        });

        var request = new MailboxEmailIntakeRequest(
            "msg-1", "<msg-1@example>", "info@lyonshaulage.com", "orders@example.com", "Orders Team",
            "PO 123", DateTimeOffset.Parse("2026-09-17T09:00:00Z"), "Collect 4 pallets", "<p>Collect 4 pallets</p>",
            "https://outlook.example/msg-1",
            [new("order.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "BASE64-COPY", false, null, 2048)],
            "conversation-1", null, null, "html", "normal", "corr-1");

        var result = InfoMailboxIntakeUpgradeController.UpgradeEvidencePayload(existing, request);
        using var document = JsonDocument.Parse(result.PayloadJson);
        var root = document.RootElement;

        Assert.True(result.Changed);
        Assert.True(result.CleanBodyApplied);
        Assert.Equal(1, result.AttachmentCopiesAdded);
        Assert.Equal("Collect 4 pallets", root.GetProperty("bodyText").GetString());
        Assert.Equal("html", root.GetProperty("bodyFormat").GetString());
        Assert.Equal("Orders Team", root.GetProperty("senderName").GetString());
        var attachment = Assert.Single(root.GetProperty("attachments").EnumerateArray());
        Assert.Equal("BASE64-COPY", attachment.GetProperty("contentBase64").GetString());
    }

    [Fact]
    public void UpgradeEvidencePayload_DoesNotDowngradeExistingAttachmentCopy()
    {
        var existing = JsonSerializer.Serialize(new
        {
            messageId = "msg-2",
            bodyText = "Readable existing body",
            bodyHtml = "<p>Readable existing body</p>",
            bodyFormat = "html",
            attachments = new[]
            {
                new
                {
                    name = "order.pdf",
                    contentType = "application/pdf",
                    size = 1000,
                    isInline = false,
                    contentId = (string?)null,
                    contentBase64 = "GOOD-COPY"
                }
            },
            evidenceAvailable = true
        });

        var request = new MailboxEmailIntakeRequest(
            "msg-2", null, "info@lyonshaulage.com", "orders@example.com", "Orders Team",
            "Order", DateTimeOffset.Parse("2026-09-17T09:00:00Z"), "Readable existing body", "<p>Readable existing body</p>",
            null, [new("order.pdf", "application/pdf", null, false, null, 1000)]);

        var result = InfoMailboxIntakeUpgradeController.UpgradeEvidencePayload(existing, request);
        using var document = JsonDocument.Parse(result.PayloadJson);
        var attachment = Assert.Single(document.RootElement.GetProperty("attachments").EnumerateArray());

        Assert.Equal("GOOD-COPY", attachment.GetProperty("contentBase64").GetString());
        Assert.Equal(0, result.AttachmentCopiesAdded);
    }

    [Fact]
    public void UpgradeEvidencePayload_PreservesExistingGoodBodyWhenReplayBodyIsWorse()
    {
        var existing = JsonSerializer.Serialize(new
        {
            messageId = "msg-3",
            bodyText = "Collect 10 pallets from Sefter for delivery tomorrow.",
            bodyHtml = "<p>Collect 10 pallets from Sefter for delivery tomorrow.</p>",
            bodyFormat = "html",
            attachments = Array.Empty<object>(),
            evidenceAvailable = true
        });

        var request = new MailboxEmailIntakeRequest(
            "msg-3", null, "info@lyonshaulage.com", "orders@example.com", "Orders Team",
            "Order", DateTimeOffset.Parse("2026-09-17T09:00:00Z"), "<div>Collect 10</div>", "<div>Collect 10</div>",
            null, []);

        var result = InfoMailboxIntakeUpgradeController.UpgradeEvidencePayload(existing, request);
        using var document = JsonDocument.Parse(result.PayloadJson);

        Assert.Equal("Collect 10 pallets from Sefter for delivery tomorrow.", document.RootElement.GetProperty("bodyText").GetString());
    }
}
