using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDataWorkbookMarketNamesTests
{
    [Fact]
    public void Names_tab_separates_markets_senders_stands_and_salesmen()
    {
        var rows = new[]
        {
            new WorkbookRow("Names", 2, new Dictionary<string, string?>
            {
                ["coventsalesmen"] = "A A Produce (Unit B53)",
                ["spitsalesmen"] = "5 Star Fruit & Veg (Stand 14 & 15)",
                ["westernsalesmen"] = "Addey & Son",
                ["salesmen"] = "Night Sales",
                ["sender"] = "APS"
            })
        };

        var result = MasterDataWorkbookImportController.ExpandMarketRows(rows).ToList();

        var covent = Assert.Single(result, row => row.Market == "Covent");
        Assert.Equal("A A Produce", covent.Name);
        Assert.Equal("Unit B53", covent.StandOrLocation);
        Assert.Null(covent.Salesman);
        Assert.Null(covent.Sender);

        var spit = Assert.Single(result, row => row.Market == "Spitalfields");
        Assert.Equal("5 Star Fruit & Veg", spit.Name);
        Assert.Equal("Stand 14 & 15", spit.StandOrLocation);

        var western = Assert.Single(result, row => row.Market == "Western");
        Assert.Equal("Addey & Son", western.Name);
        Assert.Null(western.StandOrLocation);

        var salesman = Assert.Single(result, row => row.Market == "Salesmen");
        Assert.Equal("Night Sales", salesman.Name);
        Assert.Equal("Night Sales", salesman.Salesman);
        Assert.Null(salesman.Sender);

        var sender = Assert.Single(result, row => row.Market == "Sender");
        Assert.Equal("APS", sender.Name);
        Assert.Equal("APS", sender.Sender);
        Assert.Null(sender.Salesman);
    }

    [Fact]
    public void Market_contacts_sheet_reads_combined_stand_header_and_keeps_sender_separate()
    {
        var rows = new[]
        {
            new WorkbookRow("Market Contacts", 2, new Dictionary<string, string?>
            {
                ["market"] = "Covent",
                ["name"] = "Example Seller (Unit B53)",
                ["standorlocation"] = null,
                ["sender"] = "APS"
            })
        };

        var result = MasterDataWorkbookImportController.ExpandMarketRows(rows).ToList();

        var seller = Assert.Single(result);
        Assert.Equal("Unit B53", seller.StandOrLocation);
        Assert.Null(seller.Salesman);
        Assert.Equal("APS", seller.Sender);
    }
}
