using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>The same site written differently must not be reported as "Unusual: new ...".</summary>
public sealed class IntakeNameMatchingTests
{
    private static JsonElement Payload(string collection, string destination) => JsonSerializer.SerializeToElement(new
    {
        customerCode = "NWF", collectionDate = "2026-10-19", pallets = 25, sellerName = collection, stallNumber = destination, intakeProfile = "NWF_PO"
    });

    private static IntakeAutoApproval.FormatHistory History()
    {
        var history = new IntakeAutoApproval.FormatHistory { Orders = 30 };
        history.Destinations["ALDIGOLDTHORPE"] = [24, 25, 26];
        history.Destinations["ALDISAWLEY"] = [20, 22];
        history.Collections.Add("NWFSELSEY");
        return history;
    }

    [Fact]
    public void Longer_or_shorter_versions_of_a_known_name_are_not_new()
    {
        var anomalies = IntakeAutoApproval.Anomalies(Payload("Selsey", "Aldi GOLDTHORPE Distribution Centre"), History());
        Assert.Empty(anomalies);
    }

    [Fact]
    public void A_genuinely_new_depot_or_site_is_still_flagged()
    {
        var anomalies = IntakeAutoApproval.Anomalies(Payload("Runcton", "Aldi Neston"), History());
        Assert.Contains(anomalies, item => item.StartsWith("new delivery point Aldi Neston", StringComparison.Ordinal));
        Assert.Contains(anomalies, item => item.StartsWith("new collection site Runcton", StringComparison.Ordinal));
    }

    [Fact]
    public void Names_resolve_through_site_master_aliases()
    {
        var goldthorpe = Guid.NewGuid();
        var sites = SiteNameIndex.FromNames([("Aldi-Goldthorpe", goldthorpe), ("ALDI RDC Goldthorpe", goldthorpe), ("Aldi Sawley", Guid.NewGuid())]);
        Assert.Equal(goldthorpe, sites.Resolve(SiteNameIndex.Clean("Aldi GOLDTHORPE Distribution Centre")));
        Assert.Null(sites.Resolve(SiteNameIndex.Clean("Aldi")));

        var history = new IntakeAutoApproval.FormatHistory { Orders = 30 };
        history.Destinations[$"SITE{goldthorpe:N}".ToUpperInvariant()] = [25];
        history.Collections.Add("NWFSELSEY");
        Assert.Empty(IntakeAutoApproval.Anomalies(Payload("NWF Selsey", "ALDI RDC Goldthorpe"), history, sites));
    }
}
