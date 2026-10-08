using Xunit;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Tests;

public sealed class SharePointOrderArchiveServiceTests
{
    [Fact]
    public void Removing_attachment_payload_keeps_attachment_metadata_for_audit_and_replay()
    {
        var cleaned = SharePointOrderArchiveService.RemoveAttachmentBytes("""
        {"messageId":"graph-1","attachments":[{"name":"order.xlsx","contentBase64":"AQID","size":3},{"name":"logo.png","isInline":true,"contentBase64":"BA=="}]}
        """);

        Assert.Contains("order.xlsx", cleaned);
        Assert.Contains("logo.png", cleaned);
        Assert.DoesNotContain("contentBase64", cleaned);
    }
}
