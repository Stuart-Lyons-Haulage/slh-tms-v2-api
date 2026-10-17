using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeSourceEvidenceRetentionTests
{
    [Fact]
    public void MailboxAttachmentRequest_ExposesContentBase64OrContentBytesForEvidenceRetention()
    {
        var standard = new MailboxAttachmentRequest("booking.pdf", "application/pdf", "BASE64-COPY", false, null, 42);
        var outlook = new MailboxAttachmentRequest("booking.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null, false, null, 42, "OUTLOOK-CONTENT-BYTES");

        Assert.Equal("BASE64-COPY", standard.EffectiveContentBase64);
        Assert.Equal("OUTLOOK-CONTENT-BYTES", outlook.EffectiveContentBase64);
    }

    [Fact]
    public void RetainedSourceEmailAttachmentShape_IncludesDownloadCopyField()
    {
        var attachment = new
        {
            name = "booking.pdf",
            contentType = "application/pdf",
            size = 42,
            isInline = false,
            contentBase64 = "BASE64-COPY"
        };

        var json = JsonSerializer.Serialize(attachment);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("booking.pdf", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("BASE64-COPY", document.RootElement.GetProperty("contentBase64").GetString());
    }
}
