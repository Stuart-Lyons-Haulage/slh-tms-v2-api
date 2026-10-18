using System.Net;
using System.Text;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OutlookDraftTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public OutlookDraftTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public void Draft_opens_as_a_new_message_with_the_pdf_attached()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test");
        var eml = Encoding.ASCII.GetString(OutlookDraft.Build(["planning@barfoots.co.uk", "despatch@barfoots.co.uk"], ["info@lyonshaulage.com"],
            "Barfoots Load Plan-19102026", OutlookDraft.TextToHtml("Good afternoon,\nPlease find attached."), "Barfoots Load Plan-19102026.pdf", pdf));

        Assert.StartsWith("X-Unsent: 1\r\n", eml);
        Assert.Contains("To: planning@barfoots.co.uk, despatch@barfoots.co.uk\r\n", eml);
        Assert.Contains("Cc: info@lyonshaulage.com\r\n", eml);
        Assert.Contains("Subject: Barfoots Load Plan-19102026\r\n", eml);
        Assert.Contains("Content-Disposition: attachment; filename=\"Barfoots Load Plan-19102026.pdf\"", eml);
        Assert.Contains(Convert.ToBase64String(pdf), eml);
        Assert.DoesNotContain("From:", eml);
    }

    [Fact]
    public async Task Nothing_planned_returns_not_found()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var response = await client.GetAsync("/api/v1/customer-load-plans/draft?date=2031-01-01&customerCode=NOBODY&kind=plan");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
