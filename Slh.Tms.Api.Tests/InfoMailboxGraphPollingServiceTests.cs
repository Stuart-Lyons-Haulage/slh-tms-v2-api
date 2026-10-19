using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class InfoMailboxGraphPollingServiceTests
{
    [Theory]
    [InlineData("orders@doubleh.co.uk", true)]
    [InlineData("planner@sub.nwfltd.co.uk", true)]
    [InlineData("orders@not-a-customer.example", false)]
    [InlineData(null, false)]
    public void Customer_domain_allowlist_matches_root_and_subdomains(string? sender, bool expected)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "doubleh.co.uk", "nwfltd.co.uk" };

        Assert.Equal(expected, InfoMailboxGraphPollingService.IsCustomerDomain(sender, domains));
    }

    [Theory]
    [InlineData("Agnieszka.Zawislan@barfoots.co.uk", true)]
    [InlineData("planner@sub.barfoots.co.uk", true)]
    [InlineData("orders@barfoots.co.uk.evil.example", false)]
    [InlineData("orders@unknown.example", false)]
    public void Verified_order_source_fallback_keeps_barfoots_visible_to_graph(string? sender, bool expected)
    {
        Assert.Equal(expected, InfoMailboxGraphPollingService.IsVerifiedOrderSourceDomain(sender));
    }

    [Theory]
    [InlineData("joe@lyonshaulage.com", true)]
    [InlineData("joe@ops.lyonshaulage.com", true)]
    [InlineData("joe@lyonshaulage.com.evil.example", false)]
    [InlineData("orders@customer.example", false)]
    [InlineData(null, false)]
    public void InternalSender_IsExcludedByMailboxDomain(string? sender, bool expected)
    {
        Assert.Equal(expected, InfoMailboxGraphPollingService.IsInternalSender(
            sender,
            "info@lyonshaulage.com"));
    }

    [Fact]
    public void ManualMailboxDayWindow_UsesLondonMidnightAndHandlesDst()
    {
        var (startUtc, endUtc) = InfoMailboxGraphPollingService.MailboxDayWindow(new DateOnly(2026, 10, 25));

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.Zero), startUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), endUtc);
        Assert.Equal(TimeSpan.FromHours(25), endUtc - startUtc);
    }

    [Fact]
    public void GraphHealthState_TracksActiveAndFailedPollStages()
    {
        var health = new InfoMailboxGraphHealthState();

        health.BeginAttempt();
        Assert.True(health.InProgress);
        Assert.NotNull(health.LastAttemptUtc);

        health.Failure(new InvalidOperationException("Graph timed out"), "Graph mailbox scan");

        Assert.False(health.InProgress);
        Assert.Equal("Graph mailbox scan", health.LastFailureStage);
        Assert.Equal("Graph timed out", health.LastError);
    }

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
        Assert.Equal("Please see attached.", request.BodyText);
        Assert.Equal(message.BodyHtml, request.BodyHtml);
        Assert.Equal("html", request.BodyFormat);
        Assert.Single(request.Attachments!);
        Assert.Equal("BASE64-CONTENT", request.Attachments![0].EffectiveContentBase64);
        Assert.StartsWith("graph:", request.CorrelationId);
        Assert.NotNull(request.ToRecipients);
    }

    [Theory]
    // Evidence written by EnsureSourceEmailEvidence: PascalCase fields, camelCase contentBase64.
    [InlineData("""{"attachments":[{"Name":"Sunday 18.10.2026.xlsx","IsInline":false,"RetrievalError":null,"contentBase64":null}]}""", true)]
    [InlineData("""{"attachments":[{"Name":"Sunday 18.10.2026.xlsx","IsInline":false,"RetrievalError":"Graph attachment retrieval failed: timeout","contentBase64":null}]}""", true)]
    [InlineData("""{"attachments":[{"Name":"outlook-attachment-unavailable","IsInline":false,"RetrievalError":"timeout","contentBase64":null}]}""", true)]
    [InlineData("""{"attachments":[{"Name":"Sunday 18.10.2026.xlsx","IsInline":false,"contentBase64":"UEsDBA=="}]}""", false)]
    [InlineData("""{"attachments":[{"Name":"outlook-attachment-unavailable","contentBase64":null},{"Name":"GHS Aldi Bookings 181026.xlsm","IsInline":false,"contentBase64":"UEsDBA=="}]}""", false)]
    [InlineData("""{"attachments":[{"Name":"image476d70.PNG","IsInline":true,"contentBase64":null}]}""", false)]
    [InlineData("""{"attachments":[{"Name":"big.pdf","IsInline":false,"RetrievalError":"Attachment is 30,000,000 bytes and exceeds the configured Graph intake limit.","contentBase64":null}]}""", false)]
    public void Retained_message_is_fetched_again_until_its_order_file_is_held(string evidence, bool retry)
    {
        Assert.Equal(retry, InfoMailboxGraphPollingService.EvidenceNeedsAttachmentRetry(evidence, messageHasAttachments: true));
        Assert.False(InfoMailboxGraphPollingService.EvidenceNeedsAttachmentRetry(evidence, messageHasAttachments: false));
    }

    [Fact]
    public void Evidence_is_only_skipped_after_intake_finished_for_it()
    {
        Assert.False(InfoMailboxGraphPollingService.EvidenceIntakeCompleted("""{"messageId":"m1","attachments":[]}"""));
        Assert.True(InfoMailboxGraphPollingService.EvidenceIntakeCompleted("""{"messageId":"m1","attachments":[],"intakeCompletedAtUtc":"2026-10-17T12:43:30Z"}"""));
    }

    [Fact]
    public void Intake_receives_the_whole_body_not_the_255_character_preview()
    {
        var rows = string.Join("", Enumerable.Range(1, 30).Select(index => $"<tr><td>DEPOT {index}</td><td>X610{index:00}</td><td>{index}</td></tr>"));
        var html = $"<p>These are the Waitrose pallet counts for delivery tomorrow.</p><table>{rows}</table>";
        var message = new GraphMailboxMessage("id-1", "<id-1@example.test>", "conv-1", "Waitrose 18/10/26",
            DateTimeOffset.Parse("2026-10-17T13:24:44Z"), html, "These are the Waitrose pallet counts", "Tom.Whiting@fowlerwelch.co.uk",
            "Tom Whiting", "normal", null, false, null, null);

        var request = InfoMailboxGraphPollingService.ToIntakeRequest(message, [], "info@lyonshaulage.com");

        Assert.Contains("DEPOT 30 | X61030 | 30", request.BodyText);
        Assert.Equal(html, request.BodyHtml);
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

    [Fact]
    public void GraphBody_ExtractsSupportedSharePointWorkbookLinksOnly()
    {
        var links = InfoMailboxGraphPollingService.ExtractSupportedDocumentLinks(
            "<p>See <a href=\"https://stuartlyonshaulage-my.sharepoint.com/:x:/g/personal/joe_lyonshaulage_com/file/Lyons%20collections%20260926%20REAL.xlsm?e=abc\">workbook</a></p>",
            "See https://example.com/not-an-order.txt");

        var link = Assert.Single(links);
        Assert.Contains("Lyons%20collections%20260926%20REAL.xlsm", link);
        Assert.StartsWith("u!", InfoMailboxGraphPollingService.ToGraphShareId(link));
    }

    [Fact]
    public void GraphBody_RecognisesExcelShareLinksWithoutAFileExtension()
    {
        var links = InfoMailboxGraphPollingService.ExtractSupportedDocumentLinks(
            "https://stuartlyonshaulage-my.sharepoint.com/:x:/g/personal/joe_lyonshaulage_com/IQTest?e=abc",
            null);

        Assert.Single(links);
    }
}
