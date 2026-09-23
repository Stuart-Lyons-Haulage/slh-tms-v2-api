using System.Text;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class CustomerLoadPlanPdfTests
{
    [Fact]
    public void Build_creates_pdf_with_customer_and_live_plan_rows()
    {
        var plan = new CustomerLoadPlanPreview(
            "NWF",
            "Natures Way Foods",
            new DateOnly(2026, 9, 9),
            "Lyons Collections-09092026",
            "Please find attached load plan.",
            "Lyons Collections-09092026.pdf",
            ["logistics@example.test"],
            [],
            [new CustomerLoadPlanRow(
                Guid.NewGuid(),
                "RUN 1 AM",
                Guid.NewGuid(),
                "NWF-12345",
                "NWF",
                "Runcton",
                "Darlington",
                9,
                "04:30",
                "12:00",
                "Test Driver",
                "AB12CDE",
                "43DD",
                "Planned",
                "Customer handling note")],
            9,
            null,
            null);

        var pdf = CustomerLoadPlanPdf.Build(plan);
        Assert.True(pdf.Length > 1000);
        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(pdf, 0, 8));
        var content = Encoding.ASCII.GetString(pdf);
        Assert.Contains("STUART LYONS HAULAGE", content);
        Assert.Contains("Natures Way Foods", content);
        Assert.Contains("RUN 1 AM", content);
        Assert.Contains("Runcton", content);
        Assert.Contains("Darlington", content);
    }
}
