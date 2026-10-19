using UglyToad.PdfPig;
using Xunit;
namespace Slh.Tms.Api.Tests;
public sealed class TmpPdfTextProbe
{
    [Fact]
    public void Probe()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Barfoots Euro Pool Return 0332403646 20.10.2026.pdf")));
        var text = string.Join("\n", doc.GetPages().Select(p => p.Text));
        var parsed = Slh.Tms.Api.Controllers.SpecialistMailboxOrderParser.ParseEuroPoolConfirmationPdf(text);
        Assert.Fail($"PROBE parsed={parsed}\nTEXT<<{text}>>");
    }
}
