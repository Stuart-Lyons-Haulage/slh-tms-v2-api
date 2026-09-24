using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class InfoMailboxGraphPollingServiceTests
{
    [Fact]
    public void GraphMessage_IsMappedToCanonicalMailboxRequest()
    {
        using var document = JsonDocument.Parse("""
        {
          "id": "AAMk-test-message",
          "internetMessageId": "<order@example.test>",
          "conversationId": "conversation-1",
          "subject": "Aldi Confirmed Booking for delivery Thursday 24.09.2026",
          "receivedDateTime": "2026-09-23T12:41:43Z",
          "body": { "contentType": "html", "content": "<p>Please see attached.</p>" },
          "bodyPreview": "Please see attached.",
          "from": { "emailAddress": { "name": "Agnieszka Zawislan", "address": "Agnieszka.Zawislan@barfoots.co.uk" } },
          "toRecipients": [
            { "emailAddress": { "name": "info", "address": "info@lyonshaulage.com" } }
          ],
          "ccRecipients": [],
          "importance": "normal",
          "webLink": "https://outlook.office.com/mail/test",
          "hasAttachments": true
        }
        """);

        var message = InfoMailboxGraphPollingService.ParseMessage(document.RootElement);
        Assert.NotNull(message);
        Assert.Equal("AAMk-test-message", message!.Id);
        Assert.Equal("Agnieszka.Zawislan@barfoots.co.uk", message.SenderAddress);
        Assert.True(message.HasAttachments);

        var attachments = new List<MailboxAttachmentRequest>
        {
            new("Thursday 24.09.2026.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                null,
                false,
                null,
                12000,
                "BASE64-CONTENT")
        };

        var request = InfoMailboxGraphPollingService.ToIntakeRequest(
            message,
            attachments,
            "info@lyonshaulage.com");

        Assert.Equal(message.Id, request.MessageId);
        Assert.Equal("info@lyonshaulage.com", request.Mailbox);
        Assert.Equal(message.SenderAddress, request.SenderAddress);
        Assert.Equal(message.Subject, request.Subject);
        Assert.Equal(message.BodyPreview, request.BodyText);
        Assert.Equal(message.BodyHtml, request.BodyHtml);
        Assert.Equal("html", request.BodyFormat);
        Assert.Single(request.Attachments!);
        Assert.Equal("BASE64-CONTENT", request.Attachments![0].EffectiveContentBase64);
        Assert.StartsWith("graph:", request.CorrelationId);
        Assert.NotNull(request.ToRecipients);
    }

    [Fact]
    public void GraphOptions_RequireAllApplicationCredentials_WhenEnabled()
    {
        var options = new InfoMailboxGraphOptions
        {
            Enabled = true,
            TenantId = "tenant",
            ClientId = "client",
            ClientSecret = "secret",
            Mailbox = "info@lyonshaulage.com"
        };

        Assert.True(options.IsConfigured);

        options.ClientSecret = "";
        Assert.False(options.IsConfigured);
    }
}
