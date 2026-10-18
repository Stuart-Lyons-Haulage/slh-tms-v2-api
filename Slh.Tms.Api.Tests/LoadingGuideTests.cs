using System.Text;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>Loading guides for runs that mix euro and standard pallets, as the planners send them.</summary>
public sealed class LoadingGuideTests
{
    private static CustomerLoadPlanRow Row(string load, string collection, string delivery, int pallets, string type, int sequence) =>
        new(Guid.NewGuid(), load, Guid.NewGuid(), $"PO-{collection}-{pallets}", "NWF", collection, delivery, pallets, "05:00", "18:00",
            null, null, null, "Planned", null, type, sequence);

    [Fact]
    public void Mixed_load_is_named_like_the_planners_guides_and_fits_the_deck()
    {
        var guide = LoadingGuide.Build("Load 1", [
            Row("Load 1", "NWF-Selsey", "Aldi-Darlington", 18, "Euro", 4),
            Row("Load 1", "NWF-Drayton", "Aldi-Darlington", 2, "Euro", 4),
            Row("Load 1", "NWF-Runcton", "Morrisons-Stockton", 10, "Standard", 6)])!;

        Assert.Equal("20ep-10p", guide.Code);
        Assert.False(guide.OverLength);
        // Last drop (Stockton, sequence 6) is at the nose.
        Assert.Equal("Morrisons-Stockton", guide.Rows[0].Pallets[0].Delivery);
        Assert.Equal(13.0, guide.LengthMetres);
    }

    [Fact]
    public void Odd_euro_remainder_turns_lengthways_so_28_euro_and_4_standard_fit()
    {
        var guide = LoadingGuide.Build("Load 19", [
            Row("Load 19", "NWF-Selsey", "Aldi-Chelmsford", 28, "Euro", 3),
            Row("Load 19", "NWF-Merston", "Aldi-Chelmsford", 4, "Standard", 3)])!;
        Assert.Equal("28ep-4p", guide.Code);
        Assert.Equal(13.6, guide.LengthMetres);
        Assert.False(guide.OverLength);
        Assert.Contains(guide.Rows, row => row.Layout == "1 lengthways");
    }

    [Fact]
    public void Single_type_loads_and_trays_need_no_guide()
    {
        Assert.Null(LoadingGuide.Build("Load 2", [Row("Load 2", "NWF-Selsey", "Aldi-Goldthorpe", 26, "Standard", 2)]));
        Assert.Null(LoadingGuide.Build("Load 3", [Row("Load 3", "NWF-Selsey", "Aldi-Goldthorpe", 20, "Trays", 2), Row("Load 3", "NWF-Selsey", "Aldi-Goldthorpe", 6, "Euro", 2)]));
    }

    [Fact]
    public void Load_plan_pdf_gets_a_guide_page_for_a_mixed_load()
    {
        var plan = new CustomerLoadPlanPreview("NWF", "Natures Way Foods", new DateOnly(2026, 10, 19), "Lyons Collections-19102026", "x",
            "Lyons Collections-19102026.pdf", ["a@b.test"], [],
            [Row("Load 6", "NWF-Selsey", "Aldi-Neston", 30, "Euro", 2), Row("Load 6", "NWF-Selsey", "Costco-Northampton", 2, "Standard", 4)], 32, null, null);
        var content = Encoding.ASCII.GetString(CustomerLoadPlanPdf.Build(plan));
        Assert.Contains("/Count 2", content);
        Assert.Contains("Load 6 loading guide: 30ep-2p", content);
        Assert.Contains("NOSE", content);
    }
}
